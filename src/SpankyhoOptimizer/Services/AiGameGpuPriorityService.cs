using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// One reversible GPU scheduler adjustment for the proven foreground game.
/// Shares the existing AI Runtime tick; no new polling, timers or hooks.
/// Only Normal (2) -> AboveNormal (3), never High/Realtime.
/// </summary>
internal sealed class AiGameGpuPriorityService
{
    private const int Normal = 2;
    private const int AboveNormal = 3;
    private static readonly string JournalPath =
        Path.Combine(LogService.Root, "ai-game-gpu-priority-ownership.json");
    // A second RuntimeHost must never recover or overwrite the GPU priority
    // owned by the first. Keep this OS-exclusive handle open while owned.
    private static readonly string LeasePath = JournalPath + ".lock";
    private FileStream? _ownershipLease;
    private sealed record Ownership(
        int Pid, long StartUtcTicks, string ImagePath, int Original, int Applied);

    private Ownership? _owned;
    private bool _loaded;
    private bool _faulted;
    private long _nextRecoveryStamp;
    private int _failedPid;
    private long _retryAfterStamp;
    private long _lastOwnedReadbackStamp;

    internal bool HasOwnedProcess => _owned != null;

    private static FileStream? TryAcquireOwnershipLease(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; } // other RuntimeHost or disk failure
        catch (UnauthorizedAccessException) { return null; } // fail closed
    }

    private bool EnsureOwnershipLease()
    {
        _ownershipLease ??= TryAcquireOwnershipLease(LeasePath);
        return _ownershipLease != null;
    }

    private void ReleaseLeaseIfUnowned()
    {
        if (_owned != null) return;
        _ownershipLease?.Dispose();
        _ownershipLease = null;
    }

    // Called ONLY with the exclusive OS lease. A different RuntimeHost can
    // never restore our journal while our GPU scheduler boost is owned.
    private bool RecoverJournalUnderLease()
    {
        if (_ownershipLease == null || !File.Exists(JournalPath)) return false;
        try
        {
            Ownership stored = JsonSerializer.Deserialize<Ownership>(
                File.ReadAllText(JournalPath))
                ?? throw new InvalidDataException("Empty GPU priority owner.");
            if (!AiGameGpuPriorityPolicy.ValidJournalOwnership(
                    stored.Pid, stored.StartUtcTicks, stored.ImagePath,
                    stored.Original, stored.Applied) ||
                !IsUserGameImage(stored.ImagePath))
                throw new InvalidDataException("Unsafe GPU priority owner journal.");
            _owned = stored;
            Restore();
        }
        catch (Exception ex)
        {
            _faulted = true; // preserve malformed journal; never overwrite it
            LogService.Error("ai-system", "GPU priority journal recovery failed", ex);
        }
        return true;
    }

    internal static string RunCrossHostLeaseSmoke()
    {
        // Disposable synthetic lease: no GPU, process or real journal writes.
        string path = Path.Combine(Path.GetTempPath(),
            "spanky-gpu-multi-host-" + Guid.NewGuid().ToString("N") + ".lock");
        try
        {
            using (var owner = TryAcquireOwnershipLease(path) ??
                throw new InvalidOperationException("GPU owner lease unavailable."))
            {
                using var competing = TryAcquireOwnershipLease(path);
                if (competing != null)
                    throw new InvalidOperationException(
                        "Competing RuntimeHost acquired a live GPU ownership lease.");
            }
            using var recovered = TryAcquireOwnershipLease(path);
            if (recovered == null)
                throw new InvalidOperationException(
                    "GPU owner lease did not recover after original host released it.");
        }
        finally { try { File.Delete(path); } catch { } }
        return "AI_GPU_CROSS_HOST_LEASE_SMOKE_OK oneOwner=true " +
               "secondHostDenied=true recoveryAfterRelease=true gpuWrites=0";
    }

    internal void Tick(bool enabled, AdaptivePowerRuntimeStatus? power,
        AiWorkloadClass workload, DateTime nowUtc)
    {
        if (!_loaded)
        {
            // Startup recovery is serialized across ALL RuntimeHost processes,
            // not just instances of this C# class.
            if (!EnsureOwnershipLease()) return;
            _loaded = true;
            bool recovered = RecoverJournalUnderLease();
            ReleaseLeaseIfUnowned();
            if (recovered) return; // never recover + boost in one tick
        }
        else if (_owned == null && !_faulted && File.Exists(JournalPath))
        {
            // The other host may have crashed after our initial empty scan.
            // Wait until its OS handle has actually been released.
            if (!EnsureOwnershipLease()) return;
            bool recovered = RecoverJournalUnderLease();
            ReleaseLeaseIfUnowned();
            if (recovered) return;
        }

        if (_faulted)
        {
            if (_owned == null || !AiGameGpuPriorityPolicy.IsRetryDue(
                    Stopwatch.GetTimestamp(), _nextRecoveryStamp)) return;
            _faulted = false;
            Restore();
            if (_faulted) _nextRecoveryStamp = AiGameGpuPriorityPolicy.NextRetryDeadline(
                Stopwatch.GetTimestamp(), Stopwatch.Frequency, 30);
            ReleaseLeaseIfUnowned();
            return;
        }
        // Compute each stable sample property only once. In non-game ticks
        // avoid even a foreground window native read.
        bool fresh = enabled && AdaptivePowerRuntimeService.IsStatusFresh(power, nowUtc);
        bool gameActive = fresh && power is { GameActive: true, ForegroundProcessId: > 4 };
        bool knownGame = gameActive && workload == AiWorkloadClass.Game &&
            AiKnownGameProcessPolicy.IsKnownGame(power!.ForegroundProcess);
        int livePid = knownGame ? ForegroundProcessProbe.GetProcessId() : 0;
        bool valid = AiGameGpuPriorityPolicy.MayBoost(
            enabled, fresh, gameActive, workload == AiWorkloadClass.Game,
            knownGame, power?.ForegroundProcessId ?? 0, livePid);

        if (_owned != null)
        {
            // User task-switch, lost telemetry and AI OFF must unwind promptly.
            if (!valid || _owned.Pid != power!.ForegroundProcessId)
            {
                // Never restore one game's priority and elevate a second game
                // in the same tick: require a fresh, stable foreground sample.
                Restore();
                ReleaseLeaseIfUnowned();
                return;
            }
            else if (AiGameGpuPriorityPolicy.ShouldRecheckOwnedMonotonic(
                Stopwatch.GetTimestamp(), _lastOwnedReadbackStamp, Stopwatch.Frequency))
            {
                _lastOwnedReadbackStamp = Stopwatch.GetTimestamp();
                if (!StillOwned(_owned))
                {
                    // External priority or process identity changed: reconcile
                    // ownership first, never re-boost on this same sample.
                    Restore();
                    ReleaseLeaseIfUnowned();
                    return;
                }
            }
            if (_owned != null || _faulted) return;
        }
        if (!valid || power == null ||
            (_failedPid == power.ForegroundProcessId &&
             !AiGameGpuPriorityPolicy.IsRetryDue(
                 Stopwatch.GetTimestamp(), _retryAfterStamp))) return;

        // Admission and journal recovery must be serialized by the SAME OS
        // lease, otherwise another host could restore a newly boosted game.
        if (!EnsureOwnershipLease()) return;
        if (RecoverJournalUnderLease())
        {
            ReleaseLeaseIfUnowned();
            return;
        }

        try
        {
            var liveWindow = ForegroundProcessProbe.GetWindowIdentity();
            if (liveWindow.ProcessId != power.ForegroundProcessId) return;
            using Process game = Process.GetProcessById(power.ForegroundProcessId);
            if (game.HasExited || game.Id == Environment.ProcessId ||
                !AiKnownGameProcessPolicy.IsKnownGame(game.ProcessName) ||
                !string.Equals(game.ProcessName, power.ForegroundProcess,
                    StringComparison.OrdinalIgnoreCase))
                return;
            string image = game.MainModule?.FileName ?? string.Empty;
            if (!IsUserGameImage(image)) return;
            long start = game.StartTime.ToUniversalTime().Ticks;
            if (!TryRead(game.Handle, out int prior) || prior != Normal) return;
            if (ForegroundProcessProbe.GetWindowIdentity().ProcessId != game.Id ||
                game.HasExited || game.StartTime.ToUniversalTime().Ticks != start)
                return;

            if (!AiGameGpuPriorityPolicy.ValidJournalOwnership(
                    game.Id, start, image, prior, AboveNormal))
                return;
            var proposed = new Ownership(game.Id, start, image, prior, AboveNormal);
            WriteAheadOwnershipJournal.CreateNew(
                JournalPath, JsonSerializer.SerializeToUtf8Bytes(proposed));
            _owned = proposed;

            // The foreground can change while persisting the write-ahead journal.
            // Never raise a game that lost focus during this filesystem operation.
            if (ForegroundProcessProbe.GetProcessId() != game.Id || game.HasExited ||
                game.StartTime.ToUniversalTime().Ticks != start)
            {
                Restore();
                return;
            }

            if (D3DKMTSetProcessSchedulingPriorityClass(game.Handle, AboveNormal) != 0 ||
                !TryRead(game.Handle, out int check) || check != AboveNormal)
                throw new InvalidOperationException("GPU process priority write/readback failed.");
            _lastOwnedReadbackStamp = Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            // Missing permissions, anti-cheat and unsupported drivers are expected.
            // Never elevate to force access and never keep an unverified mutation.
            _failedPid = power.ForegroundProcessId;
            _retryAfterStamp = AiGameGpuPriorityPolicy.NextRetryDeadline(
                Stopwatch.GetTimestamp(), Stopwatch.Frequency, 60);
            LogService.Info("ai-system", "GPU priority skipped: " + ex.GetType().Name);
            if (_owned != null) Restore();
        }
        finally { ReleaseLeaseIfUnowned(); }
    }

    private static bool IsUserGameImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        string full = Path.GetFullPath(path);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !full.StartsWith(windows.TrimEnd('\\') + "\\",
            StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRead(nint handle, out int priority)
    {
        priority = Normal;
        try { return D3DKMTGetProcessSchedulingPriorityClass(handle, out priority) == 0; }
        catch (Exception ex) when (ex is EntryPointNotFoundException or
            DllNotFoundException or BadImageFormatException) { return false; }
    }

    private static bool Matches(Process process, Ownership owned)
    {
        try
        {
            return process.Id == owned.Pid && !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == owned.StartUtcTicks &&
                string.Equals(process.MainModule?.FileName, owned.ImagePath,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool StillOwned(Ownership owned)
    {
        try
        {
            using Process process = Process.GetProcessById(owned.Pid);
            return Matches(process, owned) &&
                TryRead(process.Handle, out int value) && value == owned.Applied;
        }
        catch { return false; }
    }

    private void Restore()
    {
        Ownership? owned = _owned;
        if (owned == null) return;
        try
        {
            using Process process = Process.GetProcessById(owned.Pid);
            if (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != owned.StartUtcTicks ||
                !string.Equals(process.MainModule?.FileName, owned.ImagePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                DiscardJournal(); // PID reused or game finished.
                return;
            }
            if (!TryRead(process.Handle, out int current))
                throw new InvalidOperationException("GPU priority restoration could not read process.");
            if (AiGameGpuPriorityPolicy.MayRestoreOwned(true, true, current))
            {
                if (D3DKMTSetProcessSchedulingPriorityClass(process.Handle, owned.Original) != 0 ||
                    !TryRead(process.Handle, out int confirmed) || confirmed != owned.Original)
                    throw new InvalidOperationException("GPU priority restoration readback failed.");
            }
            DiscardJournal();
        }
        catch (ArgumentException) { DiscardJournal(); } // Process already gone.
        catch (InvalidOperationException ex)
        {
            _faulted = true;
            LogService.Error("ai-system", "GPU priority restoration needs retry", ex);
        }
        catch (Exception ex)
        {
            _faulted = true;
            LogService.Error("ai-system", "GPU priority restoration blocked", ex);
        }
    }

    private void DiscardJournal()
    {
        try
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
            _owned = null;
            _lastOwnedReadbackStamp = 0;
        }
        catch (Exception ex)
        {
            _faulted = true;
            LogService.Error("ai-system", "GPU priority journal cleanup failed", ex);
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTGetProcessSchedulingPriorityClass(nint process, out int priority);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);
}
