using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer.ViewModels;

public sealed class StartupRowViewModel : INotifyPropertyChanged
{
    private StartupEntry _source;
    private bool _enabled;
    private bool _canEdit;
    private bool _canDelete;
    private bool _busy;
    private bool _suppress;
    private bool _insightReady;
    private bool _insightProtected;
    private bool _cachedOrphanState;
    private DateTime _orphanCheckedUtc;
    private static readonly TimeSpan OrphanCheckTtl = TimeSpan.FromSeconds(20);
    private readonly Action<StartupRowViewModel, bool> _changed;
    private readonly Func<StartupRowViewModel, Task>? _delete;

    public StartupRowViewModel(
        StartupEntry source,
        StartupInsight? insight,
        Action<StartupRowViewModel, bool> changed,
        Func<StartupRowViewModel, Task>? delete = null)
    {
        _source = source;
        _enabled = source.Enabled;
        _insightReady = insight != null;
        _insightProtected = insight?.Protected == true;
        _canEdit = source.CanToggle && _insightReady && !_insightProtected;
        _changed = changed;
        _delete = delete;
        _canDelete = delete != null && IsDeletable(source) && !_insightProtected;
        DeleteCommand = new AsyncCommand(
            () => _delete!(this), () => CanDelete);
        Impact = insight?.Impact ?? "Unknown";
    }

    private static bool IsDeletable(StartupEntry source)
    {
        if (!source.CanToggle) return false;
        if (source.SourceKind.Equals("Registry", StringComparison.OrdinalIgnoreCase) ||
            source.SourceKind.Equals("StartupFolder", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!source.SourceKind.Equals("ScheduledTask", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(source.SourceId))
            return false;
        // Deleting a Windows component or our own maintenance task would break
        // the OS or the optimizer. Only non-critical third-party tasks qualify.
        return !source.SourceId.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) &&
            !source.SourceId.StartsWith(@"\SpankyhoOptimizer-", StringComparison.OrdinalIgnoreCase) &&
            TaskSafetyService.CanDisable(source.SourceId);
    }

    public ICommand DeleteCommand { get; }

    public bool CanDelete => _canDelete && !_busy;

    public Microsoft.UI.Xaml.Visibility DeleteVisibility =>
        CanDelete ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public StartupEntry Source => _source;
    public string Name => _source.Name;
    public string FilePath => _source.FilePath ?? string.Empty;
    public bool IsOrphaned
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FilePath)) return false;
            DateTime now = DateTime.UtcNow;
            if (_orphanCheckedUtc != default && now - _orphanCheckedUtc < OrphanCheckTtl)
                return _cachedOrphanState;
            string expanded = Environment.ExpandEnvironmentVariables(FilePath).Trim().Trim('"');
            _cachedOrphanState = !File.Exists(expanded);
            _orphanCheckedUtc = now;
            return _cachedOrphanState;
        }
    }
    public string Subtitle => IsOrphaned
        ? $"{_source.Publisher} • {_source.SourceKind} • CHYBÍ SOUBOR"
        : $"{_source.Publisher} • {_source.SourceKind} • {Impact} impact";
    public string Impact { get; private set; }
    public string StateText => _enabled ? "AKTIVNÍ" : "VYPNUTO";
    public bool HasFilePath => !string.IsNullOrWhiteSpace(FilePath);
    public bool CanEdit => _canEdit && !_busy;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnChanged();
            OnChanged(nameof(StateText));
            if (!_suppress) _changed(this, value);
        }
    }

    public void BeginBusy()
    {
        if (_busy) return;
        _busy = true;
        OnChanged(nameof(CanEdit));
        OnChanged(nameof(CanDelete));
        OnChanged(nameof(DeleteVisibility));
    }

    public void ApplyInsight(StartupInsight? insight)
    {
        string impact = insight?.Impact ?? "Unknown";
        bool protectedState = insight?.Protected == true;
        bool impactChanged = !string.Equals(Impact, impact, StringComparison.Ordinal);
        bool canEditBefore = CanEdit;

        Impact = impact;
        _insightReady = insight != null;
        _insightProtected = protectedState;
        _canEdit = _source.CanToggle && _insightReady && !_insightProtected;
        _canDelete = _delete != null && IsDeletable(_source) && !_insightProtected;

        if (impactChanged)
        {
            OnChanged(nameof(Impact));
            OnChanged(nameof(Subtitle));
        }
        if (CanEdit != canEditBefore)
            OnChanged(nameof(CanEdit));
        OnChanged(nameof(CanDelete));
        OnChanged(nameof(DeleteVisibility));
    }

    public void Confirm(StartupEntry source)
    {
        _source = source;
        _orphanCheckedUtc = default; // toggles may move a startup file
        _suppress = true;
        _enabled = source.Enabled;
        _suppress = false;
        _busy = false;
        _canEdit = source.CanToggle && _insightReady && !_insightProtected;
        _canDelete = _delete != null && IsDeletable(source) && !_insightProtected;
        OnChanged(nameof(Enabled));
        OnChanged(nameof(StateText));
        OnChanged(nameof(FilePath));
        OnChanged(nameof(HasFilePath));
        OnChanged(nameof(IsOrphaned));
        OnChanged(nameof(Subtitle));
        OnChanged(nameof(CanEdit));
        OnChanged(nameof(CanDelete));
        OnChanged(nameof(DeleteVisibility));
    }

    public void Revert(bool enabled)
    {
        _suppress = true;
        _enabled = enabled;
        _suppress = false;
        _busy = false;
        OnChanged(nameof(Enabled));
        OnChanged(nameof(StateText));
        OnChanged(nameof(CanEdit));
        OnChanged(nameof(CanDelete));
        OnChanged(nameof(DeleteVisibility));
    }

    internal static string RunOrphanPathReadOnlyBenchmark()
    {
        const int rowCount = 64, frames = 120;
        string existing = typeof(StartupRowViewModel).Assembly.Location;
        string missingRoot = Path.Combine(Path.GetTempPath(), "SpankyNonexistentStartupProbe");
        var rows = new StartupRowViewModel[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            string path = (i & 1) == 0 ? existing :
                Path.Combine(missingRoot, $"missing-{i}.exe");
            var entry = new StartupEntry($"smoke-{i}", $"Smoke {i}", path,
                Microsoft.Win32.RegistryHive.CurrentUser, string.Empty, true, "Smoke", path);
            rows[i] = new StartupRowViewModel(entry, null, static (_, _) => { });
        }
        for (int i = 0; i < rowCount; i++) _ = rows[i].Subtitle; // JIT and file-system warm
        long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int missing = 0;
        for (int frame = 0; frame < frames; frame++)
            foreach (var row in rows)
                if (row.IsOrphaned && row.Subtitle.Contains("CHYBÍ SOUBOR", StringComparison.Ordinal))
                    missing++;
        sw.Stop();
        if (missing != 32 * frames)
            throw new InvalidOperationException($"Startup orphan detection benchmark mismatch: {missing}.");

        var initial = rows[0].Source;
        if (rows[0].IsOrphaned)
            throw new InvalidOperationException("Existing startup executable was treated as missing.");
        rows[0].Confirm(initial with { FilePath = Path.Combine(missingRoot, "moved.exe") });
        if (!rows[0].IsOrphaned)
            throw new InvalidOperationException("Startup row toggle did not invalidate file probe.");
        rows[0].Confirm(initial);
        if (rows[0].IsOrphaned)
            throw new InvalidOperationException("Startup row toggle did not restore file probe.");

        return $"STARTUP_ROW_ORPHAN_BENCH_OK rows={rowCount} frames={frames} " +
            $"wallMs={sw.Elapsed.TotalMilliseconds:F2} " +
            $"allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocatedStart} " +
            $"orphanCount={missing} testOnly=true";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _running;

        internal AsyncCommand(Func<Task> execute, Func<bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !_running && _canExecute();

        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _ = ExecuteAsync();
        }

        private async Task ExecuteAsync()
        {
            _running = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await _execute().ConfigureAwait(true); }
            catch (Exception ex)
            {
                // The command callback reports the failure to the user; this
                // catch only keeps a fire-and-forget command from crashing WinUI.
                LogService.Error("startup", "Startup delete command failed", ex);
            }
            finally
            {
                _running = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
