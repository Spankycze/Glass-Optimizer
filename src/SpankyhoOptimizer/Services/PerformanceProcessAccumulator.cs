using System;
using System.Collections.Generic;

namespace SpankyhoOptimizer.Services;

// Accumulate CPU coverage and foreground candidates while process handles are
// visited; no per-sample list of every running process or LINQ sorting required.
internal sealed class PerformanceProcessAccumulator
{
    private readonly List<string> _components = new();
    private readonly HashSet<string> _componentNames = new(StringComparer.OrdinalIgnoreCase);
    private (int Id, string Name, double Cpu) _first, _second;
    private bool _hasFirst, _hasSecond;
    private double _totalCpu;

    internal void Reset()
    {
        _components.Clear();
        _componentNames.Clear();
        _first = default;
        _second = default;
        _hasFirst = _hasSecond = false;
        _totalCpu = 0;
    }

    internal void Add(int pid, string name, double cpuPercent, bool isObservedComponent)
    {
        _totalCpu += cpuPercent;
        // Candidate admission needs presence, not an instance count. Reuse
        // both collections across samples; the set makes repeated components
        // O(1) rather than repeatedly scanning the list of names.
        if (isObservedComponent && cpuPercent >= 0.05 && _componentNames.Add(name))
            _components.Add(name);
        if (!(cpuPercent >= 0.1) || isObservedComponent || name == "Idle" ||
            name.Contains("Spanky", StringComparison.OrdinalIgnoreCase))
            return;

        var item = (Id: pid, Name: name, Cpu: cpuPercent);
        // Strict > preserves first-seen order for exact ties, as in stable LINQ.
        if (!_hasFirst || cpuPercent > _first.Cpu)
        {
            if (_hasFirst) { _second = _first; _hasSecond = true; }
            _first = item;
            _hasFirst = true;
        }
        else if (!_hasSecond || cpuPercent > _second.Cpu)
        {
            _second = item;
            _hasSecond = true;
        }
    }

    internal double TotalCpu => _totalCpu;

    internal ForegroundCpuSelectionResult SelectForeground()
    {
        if (!_hasFirst) return new(string.Empty, 0);
        if (!_hasSecond) return new(_first.Id + ":" + _first.Name, _first.Cpu);
        string key = _first.Id <= _second.Id
            ? _first.Id + ":" + _first.Name + "|" + _second.Id + ":" + _second.Name
            : _second.Id + ":" + _second.Name + "|" + _first.Id + ":" + _first.Name;
        return new(key, _first.Cpu + _second.Cpu);
    }

    internal string GetObservedComponents()
    {
        // Equivalent to OrderBy(StringComparer.Ordinal) and Join for string names.
        _components.Sort(StringComparer.Ordinal);
        return string.Join("|", _components);
    }
}
