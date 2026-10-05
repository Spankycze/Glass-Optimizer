using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Models;

public sealed class TweakDefinition : INotifyPropertyChanged
{
    private bool _current;

    private bool _pending;

    private bool _isLoaded;

    private bool _isBusy;

    private string _status = "Načítám…";
    private DateTime? _lastVerifiedUtc;
    private string? _lastError;
    private int _revision;

    public required string Id { get; init; }

    public required string Section { get; init; }

    public required string Group { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public string Impact { get; init; } = "Nízký";

    public bool Recommended { get; init; }

    public bool RestartRequired { get; init; }

    public TweakRisk Risk { get; init; }

    public string? Warning { get; init; }

    public Func<CancellationToken, Task<bool>> DetectAsync { get; init; } = (CancellationToken _) => Task.FromResult(result: false);

    public Func<CancellationToken, Task<bool>>? VerifyAsync { get; init; }
    public Func<bool, CancellationToken, Task> SetAsync { get; init; } = (bool _, CancellationToken _) => Task.CompletedTask;
    public Func<CancellationToken, Task>? RevertAsync { get; init; }
    public bool SupportsRevert => true;
    public bool CanRestoreOriginal => RevertAsync != null;
    public string RevertMode { get; init; } = "BooleanState";
    public bool RequiresAdmin { get; init; } = true;
    public int MinWindowsBuild { get; init; } = 19041;
    public string HardwareScope { get; init; } = "Any";
    public string TechnicalTarget { get; init; } = "Interní akce / příkaz";
    public bool LazyDetect { get; init; }
    public string[] RequiresTweaks { get; init; } = Array.Empty<string>();
    public string[] ConflictsWithTweaks { get; init; } = Array.Empty<string>();
    public Microsoft.Win32.RegistryHive? RegistryHive { get; init; }
    public string? RegistryPath { get; init; }
    public string? RegistryValueName { get; init; }
    public long? RegistryEnabledValue { get; init; }
    public int Revision => Volatile.Read(ref _revision);

    public DateTime? LastVerifiedUtc
    {
        get => _lastVerifiedUtc;
        internal set
        {
            if (_lastVerifiedUtc == value) return;
            _lastVerifiedUtc = value;
            Interlocked.Increment(ref _revision);
            OnChanged(nameof(LastVerifiedUtc));
        }
    }

    public string? LastError
    {
        get => _lastError;
        internal set
        {
            if (string.Equals(_lastError, value, StringComparison.Ordinal)) return;
            _lastError = value;
            Interlocked.Increment(ref _revision);
            OnChanged(nameof(LastError));
        }
    }

    public bool Current
    {
        get
        {
            return _current;
        }
        set
        {
            if (_current != value)
            {
                _current = value;
                Interlocked.Increment(ref _revision);
                OnChanged("Current");
                OnChanged("IsDirty");
            }
        }
    }

    public bool Pending
    {
        get
        {
            return _pending;
        }
        set
        {
            if (_pending != value)
            {
                _pending = value;
                Interlocked.Increment(ref _revision);
                OnChanged("Pending");
                OnChanged("IsDirty");
            }
        }
    }

    public bool IsLoaded
    {
        get
        {
            return _isLoaded;
        }
        set
        {
            if (_isLoaded != value)
            {
                _isLoaded = value;
                Interlocked.Increment(ref _revision);
                OnChanged("IsLoaded");
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            return _isBusy;
        }
        set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                Interlocked.Increment(ref _revision);
                OnChanged("IsBusy");
            }
        }
    }

    public bool IsDirty
    {
        get
        {
            if (IsLoaded)
            {
                return Pending != Current;
            }
            return false;
        }
    }

    public string Status
    {
        get
        {
            return _status;
        }
        set
        {
            if (!(_status == value))
            {
                _status = value;
                Interlocked.Increment(ref _revision);
                OnChanged("Status");
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
