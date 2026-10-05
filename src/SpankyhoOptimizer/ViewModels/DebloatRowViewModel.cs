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
            try { IconSource = new BitmapImage(new Uri(source.LogoPath, UriKind.Absolute)); }
            catch { IconSource = null; }
        }
    }

    public bool SetIconPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var next = new BitmapImage(new Uri(path, UriKind.Absolute));
            IconSource = next;
            PropertyChanged?.Invoke(this, new(nameof(IconSource)));
            return true;
        }
        catch { return false; }
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
