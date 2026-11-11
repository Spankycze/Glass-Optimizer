using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.CompilerServices;

namespace SpankyhoOptimizer.Services;

public static class LogService
{
    private sealed record LogItem(string Path, string Line, bool Mirror);
    private static readonly AsyncLocal<string?> Correlation = new();
    private static readonly ConcurrentQueue<LogItem> Queue = new();
    private static readonly ConcurrentDictionary<string, string> ModulePathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly object DiskSync = new();
    private static readonly Thread WriterThread;
    private static volatile bool _stopping;
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int ArchiveCount = 3;
    private const int MaxBatch = 128;
    private const int MaxQueued = 4096;
    private static int _queuedCount;
    private static readonly bool AsyncLoggingDisabled =
        string.Equals(Environment.GetEnvironmentVariable("SPANKY_TEST_DISABLE_ASYNC_LOG"), "1", StringComparison.Ordinal);
    private static readonly bool PerformanceLoggingEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SPANKY_PERF_LOG"), "1", StringComparison.Ordinal) ||
        Array.Exists(Environment.GetCommandLineArgs(), static arg =>
            // Navigating to --open-section=performance is not a diagnostic
            // opt-in. Only explicit test/trace/perf switches enable hot logs.
            arg.StartsWith("--", StringComparison.Ordinal) &&
            (arg.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("--perf", StringComparison.OrdinalIgnoreCase) ||
             arg.StartsWith("--perf-", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("--trace", StringComparison.OrdinalIgnoreCase) ||
             arg.StartsWith("--trace-", StringComparison.OrdinalIgnoreCase) ||
             arg.StartsWith("--section-soak=", StringComparison.OrdinalIgnoreCase)));

    // Let hot navigation skip resource snapshots entirely when diagnostic output is disabled.
    internal static bool PerformanceDiagnosticsEnabled => PerformanceLoggingEnabled;

    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer");
    public static readonly string PathName = Path.Combine(Root, "optimizer.log");

    static LogService()
    {
        WriterThread = new Thread(WriterLoop) { IsBackground = true, Name = "SpankyLogWriter", Priority = ThreadPriority.BelowNormal };
        if (AsyncLoggingDisabled)
            return;

        WriterThread.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAndStop();
    }

    public static void Info(string message) => Enqueue(PathName, "INFO", message, null, false);
    public static void Error(string message, Exception? ex = null) => Enqueue(PathName, "ERR", message, ex, false);
    public static void Info(string module, string message)
    {
        if (!ShouldLogModule(module)) return;
        Enqueue(ModulePath(module), "INFO", message, null, true);
    }
    public static void Info(string module, [InterpolatedStringHandlerArgument("module")] ref ModuleInterpolatedStringHandler message)
    {
        if (!message.Enabled) return;
        Enqueue(ModulePath(module), "INFO", message.GetFormattedText(), null, true);
    }
    public static void Error(string module, string message, Exception? ex = null) => Enqueue(ModulePath(module), "ERR", message, ex, true);

    private static bool ShouldLogModule(string module) =>
        PerformanceLoggingEnabled || !IsHotDiagnosticModule(module);

    private static bool IsHotDiagnosticModule(string module) =>
        string.Equals(module, "performance", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "perf", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "navigation", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "visual-qa", StringComparison.OrdinalIgnoreCase) ||
        // High-frequency RuntimeHost telemetry belongs to explicit diagnostics.
        // Keep ERR records and opt-in --perf/--trace/--smoke unchanged; avoid
        // even constructing interpolated INFO strings during ordinary use.
        string.Equals(module, "power", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "hardware", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "lianli", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(module, "ai-system", StringComparison.OrdinalIgnoreCase);

    [InterpolatedStringHandler]
    public ref struct ModuleInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _inner;
        public bool Enabled { get; }

        public ModuleInterpolatedStringHandler(int literalLength, int formattedCount, string module, out bool shouldAppend)
        {
            Enabled = shouldAppend = ShouldLogModule(module);
            _inner = shouldAppend ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        public void AppendLiteral(string value) { if (Enabled) _inner.AppendLiteral(value); }
        public void AppendFormatted<T>(T value) { if (Enabled) _inner.AppendFormatted(value); }
        public void AppendFormatted<T>(T value, string? format) { if (Enabled) _inner.AppendFormatted(value, format); }
        public void AppendFormatted<T>(T value, int alignment) { if (Enabled) _inner.AppendFormatted(value, alignment); }
        public void AppendFormatted<T>(T value, int alignment, string? format) { if (Enabled) _inner.AppendFormatted(value, alignment, format); }
        public void AppendFormatted(string? value) { if (Enabled) _inner.AppendFormatted(value); }
        public string GetFormattedText() => _inner.ToStringAndClear();
    }

    public static IDisposable BeginScope(string area, string? correlationId = null)
    {
        string? previous = Correlation.Value;
        Correlation.Value = correlationId ?? ErrorService.NewCorrelationId();
        Info($"SCOPE {area} START id={Correlation.Value}");
        return new Scope(area, previous, Correlation.Value);
    }

    private static string ModulePath(string module) => ModulePathCache.GetOrAdd(module, static key =>
    {
        string safe = key;
        foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return Path.Combine(Root, safe.ToLowerInvariant() + ".log");
    });

    private static void Enqueue(string path, string level, string message, Exception? ex, bool mirror)
    {
        if (AsyncLoggingDisabled)
            return;
        try
        {
            string id = string.IsNullOrWhiteSpace(Correlation.Value) ? "-" : Correlation.Value!;
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] [id={id}] {message}" +
                          (ex == null ? string.Empty : Environment.NewLine + ex) + Environment.NewLine;
            int queued = Interlocked.Increment(ref _queuedCount);
            if (queued > MaxQueued)
            {
                Interlocked.Decrement(ref _queuedCount);
                return;
            }
            Queue.Enqueue(new LogItem(path, line, mirror));
            if (queued == 1) Signal.Set();
        }
        catch { }
    }

    // Event-driven writer sleeps until work arrives; no periodic idle polling.
    private static void WriterLoop()
    {
        while (true)
        {
            Signal.WaitOne();
            do { FlushBatch(); } while (!Queue.IsEmpty);
            if (_stopping) break;
        }
        FlushBatch(int.MaxValue);
    }

    private static void FlushBatch(int limit = MaxBatch)
    {
        if (Queue.IsEmpty) return;
        try
        {
            Directory.CreateDirectory(Root);
            var byPath = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
            var mirror = new StringBuilder();
            int count = 0;
            while (count < limit && Queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _queuedCount);
                if (!byPath.TryGetValue(item.Path, out var sb)) byPath[item.Path] = sb = new StringBuilder();
                sb.Append(item.Line);
                if (item.Mirror && !string.Equals(item.Path, PathName, StringComparison.OrdinalIgnoreCase)) mirror.Append(item.Line);
                count++;
            }
            lock (DiskSync)
            {
                foreach (var pair in byPath)
                {
                    RotateIfNeeded(pair.Key);
                    File.AppendAllText(pair.Key, pair.Value.ToString(), Encoding.UTF8);
                }
                if (mirror.Length > 0)
                {
                    RotateIfNeeded(PathName);
                    File.AppendAllText(PathName, mirror.ToString(), Encoding.UTF8);
                }
            }
        }
        catch { }
    }

    private static void FlushAndStop()
    {
        _stopping = true;
        Signal.Set();
        try { WriterThread.Join(1200); } catch { }
        FlushBatch(int.MaxValue);
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes) return;
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = ArchiveCount - 1; i >= 1; i--)
        {
            string src = Path.Combine(dir, $"{stem}.{i}{ext}");
            string dst = Path.Combine(dir, $"{stem}.{i + 1}{ext}");
            if (File.Exists(dst)) File.Delete(dst);
            if (File.Exists(src)) File.Move(src, dst);
        }
        string first = Path.Combine(dir, $"{stem}.1{ext}");
        if (File.Exists(first)) File.Delete(first);
        File.Move(path, first);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _area;
        private readonly string? _previous;
        private readonly string _id;
        private readonly DateTime _started = DateTime.UtcNow;
        private bool _disposed;

        public Scope(string area, string? previous, string id) { _area = area; _previous = previous; _id = id; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Info($"SCOPE {_area} END id={_id} ms={(DateTime.UtcNow - _started).TotalMilliseconds:0}");
            Correlation.Value = _previous;
        }
    }
}
