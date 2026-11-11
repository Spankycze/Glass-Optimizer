using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer.ViewModels;

public sealed class DebloatRowViewModel : INotifyPropertyChanged
{
    private bool _keep;
    private string? _iconPath;
    private readonly Action<DebloatRowViewModel, bool> _changed;
    public InstalledAppInfo Source { get; }
    public string DisplayName { get; }
    public string Subtitle { get; }
    public string BadgeText { get; }
    public ImageSource? IconSource { get; private set; }
    public string StateText => Source.Protected ? "CHRÁNĚNO" : _keep ? "PONECHAT" : "ODSTRANIT";
    public bool CanEdit => !Source.Protected;

    public DebloatRowViewModel(InstalledAppInfo source, bool keep, Action<DebloatRowViewModel, bool> changed)
    {
        Source = source;
        _keep = keep;
        _changed = changed;
        DisplayName = source.DisplayName;
        Subtitle = $"{source.CategoryLabel} • {source.RiskLabel} • {source.Publisher}";
        BadgeText = source.Protected ? "Protected" : source.Recommended ? "Recommended" : source.RiskLabel;
        if (!string.IsNullOrWhiteSpace(source.LogoPath))
        {
            try
            {
                IconSource = new BitmapImage(new Uri(source.LogoPath, UriKind.Absolute));
                _iconPath = source.LogoPath;
            }
            catch { IconSource = null; }
        }
    }

    internal static bool NeedsIconUpdate(string? current, string? next) =>
        !string.IsNullOrWhiteSpace(next) &&
        !string.Equals(current, next, StringComparison.OrdinalIgnoreCase);

    public bool SetIconPath(string? path)
    {
        // Deferred logo enrichment may run again after a cache refresh.
        // Recreating the same BitmapImage on every pass spikes native image
        // allocations and needlessly notifies the virtualized ItemsRepeater.
        if (!NeedsIconUpdate(_iconPath, path)) return false;
        try
        {
            var next = new BitmapImage(new Uri(path!, UriKind.Absolute));
            IconSource = next;
            _iconPath = path;
            PropertyChanged?.Invoke(this, new(nameof(IconSource)));
            return true;
        }
        catch { return false; }
    }

    internal static void RunIconUpdatePolicySmoke()
    {
        if (NeedsIconUpdate(null, null) ||
            NeedsIconUpdate("C:\\App\\Logo.png", "c:\\app\\logo.PNG") ||
            !NeedsIconUpdate(null, "C:\\App\\Logo.png") ||
            !NeedsIconUpdate("C:\\App\\old.png", "C:\\App\\new.png"))
            throw new InvalidOperationException("Debloat repeated-logo update policy is incorrect.");
    }

    public bool Keep
    {
        get => _keep;
        set
        {
            if (_keep == value) return;
            _keep = value;
            PropertyChanged?.Invoke(this, new(nameof(Keep)));
            PropertyChanged?.Invoke(this, new(nameof(StateText)));
            _changed(this, value);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
