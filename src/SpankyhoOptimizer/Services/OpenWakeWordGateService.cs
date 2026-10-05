using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

public static class OpenWakeWordGateService
{
    private static readonly object Sync = new();
    private static Process? _process;
    private static Action<string, double>? _onWake;
    private static int _ready;

    public static bool IsRunning
    {
        get
        {
            lock (Sync)
                return _process is { HasExited: false } && Volatile.Read(ref _ready) != 0;
        }
    }

    public static string ActiveModel { get; private set; } = string.Empty;
    public static void Start(Action<string, double> onWake)
    {
        lock (Sync)
        {
            if (_process is { HasExited: false })
            {
                _onWake = onWake;
                return;
            }

            string python = Path.Combine(VoiceRuntimeBootstrapService.OpenWakeWordDir, ".venv", "Scripts", "python.exe");
            string script = Path.Combine(VoiceRuntimeBootstrapService.OpenWakeWordDir, "wakeword_bridge.py");
            if (!File.Exists(python) || !File.Exists(script))
            {
                LogService.Info("voice", "OPENWAKEWORD unavailable payload_missing");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = Path.GetDirectoryName(script)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add(script);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnError;
            process.Exited += (_, _) =>
            {
                Volatile.Write(ref _ready, 0);
                LogService.Info("voice", "OPENWAKEWORD process_exited");
            };

            if (!process.Start())
                return;

            _process = process;
            _onWake = onWake;
            Volatile.Write(ref _ready, 0);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
    }

    public static void PushPcm(byte[] pcm)
    {
        Process? process;
        lock (Sync)
            process = _process;

        if (process == null || process.HasExited)
            return;

        try
        {
            Stream stream = process.StandardInput.BaseStream;
            stream.Write(pcm, 0, pcm.Length);
            stream.Flush();
        }
        catch
        {
            Volatile.Write(ref _ready, 0);
        }
    }

    private static void OnOutput(object sender, DataReceivedEventArgs e)
    {
        string line = e.Data?.Trim() ?? string.Empty;
        if (line.StartsWith("READY ", StringComparison.Ordinal))
        {
            ActiveModel = line[6..].Trim();
            Volatile.Write(ref _ready, 1);
            LogService.Info("voice", "OPENWAKEWORD ready model=" + ActiveModel);
            return;
        }

        if (!line.StartsWith("WAKE ", StringComparison.Ordinal))
            return;

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string model = parts.Length >= 2 ? parts[1] : "unknown";
        double score = parts.Length >= 3 &&
                       double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0;

        LogService.Info("voice", $"OPENWAKEWORD wake model={model} score={score:0.000}");
        try { _onWake?.Invoke(model, score); } catch { }
    }

    private static void OnError(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
            LogService.Info("voice", "OPENWAKEWORD " + e.Data.Trim());
    }

    public static async ValueTask StopAsync()
    {
        Process? process;
        lock (Sync)
        {
            process = _process;
            _process = null;
            _onWake = null;
            ActiveModel = string.Empty;
            Volatile.Write(ref _ready, 0);
        }

        if (process == null)
            return;

        try
        {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch { process.Kill(entireProcessTree: true); }
            }
        }
        catch { }
        finally
        {
            process.Dispose();
        }
    }
}
