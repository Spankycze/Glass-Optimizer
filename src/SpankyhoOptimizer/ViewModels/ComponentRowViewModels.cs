using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SpankyhoOptimizer.Models;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer.ViewModels;

public sealed class ServiceRowViewModel : INotifyPropertyChanged
{
    private int _selectedIndex;
    private bool _suppress;
    private readonly Action<ServiceRowViewModel, int> _changed;
    public ServiceInfo Source { get; }
    public string DisplayName { get; }
    public string Subtitle => Source.Name;
    public string SafetyText { get; }
    public string StateText => _selectedIndex == 2 ? "NEAKTIVNÍ" : _selectedIndex == 0 ? "AUTOMATICKY" : "RUČNĚ";
    public bool CanEdit { get; }
    public string ProtectionText => CanEdit ? "EDITABLE" : "PROTECTED";

    public ServiceRowViewModel(ServiceInfo source, Action<ServiceRowViewModel, int> changed)
    {
        Source = source; _changed = changed;
        DisplayName = source.DisplayName.StartsWith("@", StringComparison.Ordinal) ? source.Name : source.DisplayName;
        var safety = ServiceSafetyService.Classify(source.Name);
        SafetyText = $"{safety.Level} • {safety.Reason}";
        CanEdit = safety.Level != ServiceSafetyLevel.Critical;
        _selectedIndex = source.StartType == 2 ? 0 : source.StartType == 4 ? 2 : 1;
    }
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value; OnChanged(); OnChanged(nameof(StateText));
            if (!_suppress) _changed(this, value);
        }
    }

    public void ConfirmStartType(uint startType)
    {
        _suppress = true;
        SelectedIndex = startType == 2 ? 0 : startType == 4 ? 2 : 1;
        _suppress = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class TaskRowViewModel : INotifyPropertyChanged
{
    private bool _enabled;
    private bool _suppress;
    private readonly Action<TaskRowViewModel, bool> _changed;
    public TaskInfo Source { get; }
    public string Name => Source.Name;
    public string Path => Source.Path;
    public string SafetyText { get; }
    public string StateText => _enabled ? "AKTIVNÍ" : "NEAKTIVNÍ";
    public bool CanEdit { get; }
    public string ProtectionText => CanEdit ? "EDITABLE" : "PROTECTED";

    public TaskRowViewModel(TaskInfo source, Action<TaskRowViewModel, bool> changed)
    {
        Source = source; _changed = changed; _enabled = source.Enabled;
        var safety = TaskSafetyService.Classify(source.Path);
        SafetyText = $"{safety.Level} • {safety.Reason}";
        CanEdit = safety.Level != TaskSafetyLevel.Critical;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value; OnChanged(); OnChanged(nameof(StateText));
            if (!_suppress) _changed(this, value);
        }
    }
    public void Confirm(bool enabled)
    {
        _suppress = true; Enabled = enabled; _suppress = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
