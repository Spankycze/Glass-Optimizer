using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer.ViewModels;

public sealed class StartupRowViewModel : INotifyPropertyChanged
{
    private StartupEntry _source;
    private bool _enabled;
    private bool _canEdit;
    private bool _busy;
    private bool _suppress;
    private bool _insightReady;
    private bool _insightProtected;
    private readonly Action<StartupRowViewModel, bool> _changed;

    public StartupRowViewModel(
        StartupEntry source,
        StartupInsight? insight,
        Action<StartupRowViewModel, bool> changed)
    {
        _source = source;
        _enabled = source.Enabled;
        _insightReady = insight != null;
        _insightProtected = insight?.Protected == true;
        _canEdit = source.CanToggle && _insightReady && !_insightProtected;
        _changed = changed;
        Impact = insight?.Impact ?? "Unknown";
    }

    public StartupEntry Source => _source;
    public string Name => _source.Name;
    public string FilePath => _source.FilePath ?? string.Empty;
    public bool IsOrphaned
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FilePath)) return false;
            string expanded = Environment.ExpandEnvironmentVariables(FilePath).Trim().Trim('"');
            return !File.Exists(expanded);
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

        if (impactChanged)
        {
            OnChanged(nameof(Impact));
            OnChanged(nameof(Subtitle));
        }
        if (CanEdit != canEditBefore)
            OnChanged(nameof(CanEdit));
    }

    public void Confirm(StartupEntry source)
    {
        _source = source;
        _suppress = true;
        _enabled = source.Enabled;
        _suppress = false;
        _busy = false;
        _canEdit = source.CanToggle && _insightReady && !_insightProtected;
        OnChanged(nameof(Enabled));
        OnChanged(nameof(StateText));
        OnChanged(nameof(FilePath));
        OnChanged(nameof(HasFilePath));
        OnChanged(nameof(IsOrphaned));
        OnChanged(nameof(Subtitle));
        OnChanged(nameof(CanEdit));
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
