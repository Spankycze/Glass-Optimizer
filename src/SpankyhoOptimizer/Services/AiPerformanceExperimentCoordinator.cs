using System;
using System.Diagnostics;
using System.IO;

namespace SpankyhoOptimizer.Services;

// A trial owns this lease across ticks, including both reference measurements.
// The OS file lease serializes trials across RuntimeHost processes of this user.
public static class AiPerformanceExperimentCoordinator
{
    private static readonly object Gate = new();
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(20);
    private static string? _owner;
    private static DateTime _lastTouchUtc = DateTime.MinValue; // diagnostics only
    private static long _lastTouchStamp; // monotonic lease clock
    private static FileStream? _processLease; // held across all trial ticks
    private static string LeasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpankyhoOptimizer", "ai-experiment-global.lock");

    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                ExpireStaleLeaseNoLock(DateTime.UtcNow);
                return _owner != null || IsLeasedByOtherProcess(LeasePath);
            }
        }
    }

    // Only the SAME process which owns the current lease may continue its
    // pending A/B/A trial. A separate RuntimeHost with the same owner label
    // cannot reuse the lease because its exclusive file open would fail.
    public static bool IsOwnedBy(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        lock (Gate)
        {
            ExpireStaleLeaseNoLock(DateTime.UtcNow);
            return HasLiveNativeLease(_processLease) &&
                string.Equals(_owner, owner, StringComparison.Ordinal);
        }
    }

    public static bool TryAcquire(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (Gate)
        {
            DateTime now = DateTime.UtcNow;
            ExpireStaleLeaseNoLock(now);

            if (_owner != null)
            {
                if (!string.Equals(_owner, owner, StringComparison.Ordinal) ||
                    !HasLiveNativeLease(_processLease))
                    return false; // label alone never authorizes another trial tick
            }
            else
            {
                // A dangling native handle must not be overwritten/leaked.
                if (_processLease != null) return false;
                _processLease = TryAcquireFileLease(LeasePath);
                if (!HasLiveNativeLease(_processLease))
                {
                    _processLease?.Dispose();
                    _processLease = null;
                    return false;
                }
            }
            _owner = owner;
            _lastTouchUtc = now;
            _lastTouchStamp = Stopwatch.GetTimestamp();
            return true;
        }
    }

    public static void Release(string owner)
    {
        lock (Gate)
        {
            if (!string.Equals(_owner, owner, StringComparison.Ordinal))
                return;

            _owner = null;
            _lastTouchUtc = DateTime.MinValue;
            _lastTouchStamp = 0;
            _processLease?.Dispose();
            _processLease = null;
        }
    }

    // A valid owner name is not enough for an A/B/A continuation: the native
    // exclusive file handle must still exist and be open. A lost/disposed
    // handle cannot be refreshed into a supposedly owned trial.
    private static bool HasLiveNativeLease(FileStream? lease)
    {
        if (lease == null) return false;
        try
        {
            return !lease.SafeFileHandle.IsClosed && !lease.SafeFileHandle.IsInvalid;
        }
        catch (ObjectDisposedException) { return false; }
        catch (IOException) { return false; }
    }

    // An exclusive OS file handle is released automatically if its owning
    // RuntimeHost crashes. Do not delete the persistent lock file: another
    // process might be trying to open the very same path concurrently.
    private static FileStream? TryAcquireFileLease(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; } // already held or disk failure
        catch (UnauthorizedAccessException) { return null; } // fail closed
    }

    private static bool IsLeasedByOtherProcess(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; } // sharing violation: busy
        catch (UnauthorizedAccessException) { return true; } // no evidence: busy
    }

    // Retained for diagnostics/compatibility only: this wall-clock predicate
    // must NEVER control the live ownership lease (NTP can jump forward).
    public static bool IsLeaseExpired(DateTime lastTouchUtc, DateTime nowUtc) =>
        lastTouchUtc != DateTime.MinValue &&
        nowUtc >= lastTouchUtc &&
        nowUtc - lastTouchUtc > LeaseTimeout;

    internal static bool IsMonotonicLeaseExpired(
        long touchedTicks, long nowTicks, long frequency) =>
        touchedTicks > 0 && frequency > 0 && nowTicks >= touchedTicks &&
        (nowTicks - (double)touchedTicks) / frequency > LeaseTimeout.TotalSeconds;

    // A measured Candidate-B or journalled rollback may legitimately remain
    // pending longer than 20 minutes across PC sleep. Releasing a STILL-HELD
    // OS-exclusive lock solely because of elapsed time would let a second
    // RuntimeHost start conflicting Windows mutations before recovery.
    // A crashed process releases its file handle automatically; a live owner
    // only relinquishes it with Release() after restoring/checkpointing work.
    internal static bool MayExpireLocalLease(
        bool hasOwner, bool holdsFileLease, bool elapsedTimeout) =>
        hasOwner && !holdsFileLease && elapsedTimeout;

    private static void ExpireStaleLeaseNoLock(DateTime nowUtc)
    {
        if (!MayExpireLocalLease(_owner != null, HasLiveNativeLease(_processLease),
                IsMonotonicLeaseExpired(_lastTouchStamp,
                    Stopwatch.GetTimestamp(), Stopwatch.Frequency)))
            return;

        // Defensive self-heal only for a corrupt local state with NO owned
        // file handle. Never dispose a healthy cross-process file lease here.
        LogService.Info("ai-performance",
            $"AI_EXPERIMENT orphaned_local_owner_cleared owner={_owner} last_touch={_lastTouchUtc:O}");
        _owner = null;
        _lastTouchUtc = DateTime.MinValue;
        _lastTouchStamp = 0;
        _processLease?.Dispose(); // already closed/invalid, but clear orphan
        _processLease = null;
    }

    internal static string RunFunctionalSmoke()
    {
        const long frequency = 10_000;
        const long first = 100_000;
        const long atLimit = first + 20 * 60 * frequency;
        if (IsMonotonicLeaseExpired(first, atLimit, frequency) ||
            !IsMonotonicLeaseExpired(first, atLimit + 1, frequency) ||
            IsMonotonicLeaseExpired(first, first - 1, frequency) ||
            IsMonotonicLeaseExpired(0, atLimit + 1, frequency) ||
            IsMonotonicLeaseExpired(first, atLimit + 1, 0) ||
            // Even a timed-out 30-minute or sleep-resumed trial remains
            // exclusive while its live process owns the native file handle.
            MayExpireLocalLease(true, true, true) ||
            MayExpireLocalLease(true, true, false) ||
            MayExpireLocalLease(false, false, true) ||
            MayExpireLocalLease(true, false, false) ||
            !MayExpireLocalLease(true, false, true))
            throw new InvalidOperationException("Monotonic experiment lease ownership guard failed.");
        // A wall-clock 30-minute jump can report expiration, but the real
        // lease must still belong to its owner until 20 elapsed minutes.
        DateTime utc = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        if (!IsLeaseExpired(utc, utc.AddMinutes(30)) ||
            IsMonotonicLeaseExpired(first, first + frequency * 5, frequency))
            throw new InvalidOperationException("Wall-clock change affected monotonic experiment lease.");
        // Use a disposable synthetic path, never the real user's AI lease.
        string temp = Path.Combine(Path.GetTempPath(),
            "spanky-ai-cross-host-" + Guid.NewGuid().ToString("N") + ".lock");
        try
        {
            if (IsLeasedByOtherProcess(temp))
                throw new InvalidOperationException("Uncreated experiment lease appeared busy.");
            using (var owner = TryAcquireFileLease(temp) ??
                throw new InvalidOperationException("Cannot acquire synthetic AI lease."))
            {
                if (!HasLiveNativeLease(owner) ||
                    MayExpireLocalLease(true, !owner.SafeFileHandle.IsClosed,
                        IsMonotonicLeaseExpired(first, atLimit + 1, frequency)))
                    throw new InvalidOperationException(
                        "Expired synthetic trial disposed a live cross-process lease.");
                if (!IsLeasedByOtherProcess(temp))
                    throw new InvalidOperationException("Second runtime host did not observe the exclusive lease.");
                using var contender = TryAcquireFileLease(temp);
                if (contender != null)
                    throw new InvalidOperationException("Competing runtime host acquired active trial lease.");
                // Simulate a native file handle unexpectedly closed while
                // its owner label still exists; never authorize a new tick.
                owner.Dispose();
                if (HasLiveNativeLease(owner) ||
                    !MayExpireLocalLease(true, HasLiveNativeLease(owner), true))
                    throw new InvalidOperationException(
                        "Lost experiment handle was still treated as an active native lease.");
            }
            if (IsLeasedByOtherProcess(temp))
                throw new InvalidOperationException("Released experiment lease still appears busy.");
            using (var recovered = TryAcquireFileLease(temp))
            {
                if (recovered == null)
                    throw new InvalidOperationException("Experiment lock did not become reusable after release.");
            } // close last native file handle BEFORE trying to unlink
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
        if (File.Exists(temp))
            throw new InvalidOperationException("Experiment regression leaked its temporary lock file.");
        return "AI_EXPERIMENT_MONOTONIC_LEASE_SMOKE_OK wallClockJumpIgnored=true " +
               "threshold20min=true retainedWhileHeld=true crossHostExclusive=true " +
               "recoveredAfterRelease=true tempCleanup=true registryWrites=0";
    }
}
