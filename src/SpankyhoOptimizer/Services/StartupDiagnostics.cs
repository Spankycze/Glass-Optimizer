using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class StartupDiagnostics
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Sync = new();
    private static readonly List<(string Stage, long Ms)> PendingMarks = new(24);
    private static readonly List<string> PendingInfo = new(24);
    private static string _lastStage = "Bootstrap";
    private static string? _environmentVersion;
    private static bool _marksFlushed;

    public static string LastStage
    {
        get { lock (Sync) return _lastStage; }
    }

    public static long ElapsedMilliseconds => Clock.ElapsedMilliseconds;

    public static void BufferInfo(string message)
    {
        lock (Sync)
        {
            if (!_marksFlushed)
            {
                PendingInfo.Add(message);
                return;
            }
        }
        LogService.Info("performance", message);
    }

    public static void Mark(string stage)
    {
        long ms = Clock.ElapsedMilliseconds;
        lock (Sync)
        {
            _lastStage = stage;
            if (!_marksFlushed)
            {
                PendingMarks.Add((stage, ms));
                return;
            }
        }

        // After startup completion, diagnostics are no longer on the activation hot path.
        LogService.Info($"STARTUP stage={stage} ms={ms}");
    }

    public static IReadOnlyList<string> ValidateBaseDirectory()
    {
        var missing = new List<string>();
        string[] required = { "SpankyhoOptimizer.dll", "Microsoft.WinUI.dll", "Microsoft.ui.xaml.dll" };
        foreach (string file in required)
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, file))) missing.Add(file);
        return missing;
    }

    public static void LogEnvironment(string version)
    {
        // Keep startup hot path free of disk/log I/O. Environment details and file validation
        // are flushed after the window has activated.
        lock (Sync) _environmentVersion = version;
    }

    public static void Complete() => Complete(false);

    public static void Complete(bool synchronousFlush)
    {
        Mark("Ready");

        List<(string Stage, long Ms)> buffered;
        List<string> bufferedInfo;
        string? environmentVersion;
        lock (Sync)
        {
            if (_marksFlushed) return;
            _marksFlushed = true;
            buffered = new List<(string Stage, long Ms)>(PendingMarks);
            bufferedInfo = new List<string>(PendingInfo);
            environmentVersion = _environmentVersion;
            PendingMarks.Clear();
            PendingInfo.Clear();
        }

        long readyMs = Clock.ElapsedMilliseconds;
        void Flush()
        {
            foreach ((string stage, long ms) in buffered)
                LogService.Info($"STARTUP stage={stage} ms={ms}");
            foreach (string message in bufferedInfo)
                LogService.Info("performance", message);

            if (!string.IsNullOrWhiteSpace(environmentVersion))
            {
                LogService.Info($"STARTUP version={environmentVersion} os={Environment.OSVersion} framework={RuntimeInformation.FrameworkDescription} arch={RuntimeInformation.ProcessArchitecture} base={AppContext.BaseDirectory}");
                var missing = ValidateBaseDirectory();
                if (missing.Count > 0)
                    LogService.Error("STARTUP missing critical files: " + string.Join(", ", missing));
            }

            LogService.Info($"STARTUP complete readyMs={readyMs} flushMs={Clock.ElapsedMilliseconds - readyMs} totalMs={Clock.ElapsedMilliseconds}");
        }

        if (synchronousFlush)
            Flush();
        else
            _ = Task.Run(Flush);
    }
}
