using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Rare, bounded WM_NULL responsiveness PROXY on a ThreadPool worker.
/// NO key/mouse injection, app interaction, network changes, window enumeration,
/// new periodic timer, or blocking of the WinUI/FanAgent thread. Skips games.
/// SendMessageTimeout returning 0 is UNKNOWN, not a measured 80ms latency.
/// </summary>
internal sealed class AiForegroundWindowResponseProbe
{
    private const uint WmNull = 0;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    private AiForegroundWindowResponsePolicy.Snapshot? _latest;
    private DateTime _nextDueUtc = DateTime.MinValue;
    private int _inFlight;
    private int _eligibleForegroundPid;
    private long _eligibleWindowHandle;
    private int _generation;

    internal static bool ForegroundIdentityChanged(int oldPid, long oldHwnd, int newPid, long newHwnd) =>
        oldPid != newPid || oldHwnd != newHwnd;

    // A foreground switch can occur after QueueIfDue but before the AI trial
    // reads the result. Reject the stale snapshot at the consumer boundary too.
    // Keep a failed native probe (HWND=0) only for its original foreground.
    internal AiForegroundWindowResponsePolicy.Snapshot? ReadCurrent()
    {
        var sample = Volatile.Read(ref _latest);
        if (sample is null) return null; // no native calls in the common empty case
        int eligiblePid = Volatile.Read(ref _eligibleForegroundPid);
        long eligibleHwnd = Interlocked.Read(ref _eligibleWindowHandle);
        nint foregroundHwnd = OperatingSystem.IsWindows() ? GetForegroundWindow() : 0;
        if (foregroundHwnd == 0 ||
            !TryGetPid(foregroundHwnd, out int foregroundPid) ||
            !MatchesCurrentWindow(sample, eligiblePid, eligibleHwnd,
                foregroundPid, foregroundHwnd.ToInt64()))
            return null;
        return sample;
    }

    internal static bool MatchesCurrentWindow(
        AiForegroundWindowResponsePolicy.Snapshot? sample,
        int eligiblePid, long eligibleHwnd, int foregroundPid, long foregroundHwnd) =>
        sample is not null && eligiblePid > 0 && eligibleHwnd != 0 &&
        eligiblePid == foregroundPid && eligibleHwnd == foregroundHwnd &&
        sample.ForegroundPid == eligiblePid &&
        (sample.WindowHandle == 0 || sample.WindowHandle == eligibleHwnd);

    internal void QueueIfDue(
        bool aiEnabled, AdaptivePowerRuntimeStatus? power,
        AiWorkloadClass workload, DateTime nowUtc)
    {
        if (!AiForegroundWindowResponsePolicy.CanProbe(
                aiEnabled, power, workload, nowUtc))
        {
            // A queued worker must never publish an old measurement after
            // AI is disabled, a game starts or the window becomes unknown.
            int priorPid = Interlocked.Exchange(ref _eligibleForegroundPid, 0);
            long priorHwnd = Interlocked.Exchange(ref _eligibleWindowHandle, 0);
            if (priorPid != 0 || priorHwnd != 0)
                Interlocked.Increment(ref _generation);
            // Disabling a probe invalidates its cadence as well as its sample.
            // A later eligible window must not inherit a stale cooldown.
            _nextDueUtc = DateTime.MinValue;
            Volatile.Write(ref _latest, null);
            return;
        }
        int expectedPid = power!.ForegroundProcessId;
        // The same process can own several different top-level windows. A PID
        // alone does not identify the window used by latency A/B/A trials.
        nint currentHwnd = OperatingSystem.IsWindows() ? GetForegroundWindow() : 0;
        if (currentHwnd == 0 || !TryGetPid(currentHwnd, out int livePid) ||
            livePid != expectedPid)
        {
            int oldPid = Interlocked.Exchange(ref _eligibleForegroundPid, 0);
            long oldHwnd = Interlocked.Exchange(ref _eligibleWindowHandle, 0);
            if (oldPid != 0 || oldHwnd != 0)
                Interlocked.Increment(ref _generation);
            Volatile.Write(ref _latest, null);
            _nextDueUtc = DateTime.MinValue;
            return;
        }
        int previousPid = Interlocked.Exchange(ref _eligibleForegroundPid, expectedPid);
        long previousHwnd = Interlocked.Exchange(ref _eligibleWindowHandle, currentHwnd.ToInt64());
        if (ForegroundIdentityChanged(previousPid, previousHwnd, expectedPid, currentHwnd.ToInt64()))
        {
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _latest, null);
            _nextDueUtc = DateTime.MinValue;
        }
        // Do not advance the cadence while a prior worker is still running.
        // Otherwise a slow/blocked native probe can consume the next slot
        // without scheduling any sample after the worker finishes.
        if (nowUtc < _nextDueUtc ||
            Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;
        _nextDueUtc = nowUtc.AddSeconds(
            AiForegroundWindowResponsePolicy.ProbeIntervalSeconds);
        int generation = Volatile.Read(ref _generation);

        // Task.Run here only schedules the thread-pool worker. The caller
        // NEVER performs SendMessageTimeout or waits for its completion.
        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (Volatile.Read(ref _generation) != generation)
                        return;
                    var result = ProbeNative(expectedPid);
                    if (Volatile.Read(ref _generation) == generation)
                        Volatile.Write(ref _latest, result);
                }
                catch
                {
                    if (Volatile.Read(ref _generation) == generation)
                        Volatile.Write(ref _latest, new AiForegroundWindowResponsePolicy.Snapshot(
                            DateTime.UtcNow, expectedPid, 0, null, "probe-unavailable"));
                }
                finally
                {
                    Volatile.Write(ref _inFlight, 0);
                }
            });
        }
        catch
        {
            // ThreadPool rejected scheduling: release the flight gate and
            // retry on the next eligible FanAgent tick, not after a cooldown.
            _nextDueUtc = DateTime.MinValue;
            Volatile.Write(ref _inFlight, 0);
        }
    }

    private static AiForegroundWindowResponsePolicy.Snapshot ProbeNative(int expectedPid)
    {
        DateTime now = DateTime.UtcNow;
        if (!OperatingSystem.IsWindows() || expectedPid <= 0)
            return new(now, expectedPid, 0, null, "not-windows-or-invalid-pid");
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0 || !TryGetPid(hwnd, out int livePid) || livePid != expectedPid)
            return new(now, expectedPid, 0, null, "foreground-switched-before-probe");

        long started = Stopwatch.GetTimestamp();
        // If target is frozen, only this worker can block, for at most the
        // Windows native timeout. WM_NULL requests no change or input.
        nint sent = SendMessageTimeoutW(
            hwnd, WmNull, 0, 0, SmtoAbortIfHung | SmtoBlock,
            AiForegroundWindowResponsePolicy.NativeTimeoutMs, out _);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        DateTime observed = DateTime.UtcNow;
        if (GetForegroundWindow() != hwnd ||
            !TryGetPid(hwnd, out livePid) || livePid != expectedPid)
            return new(observed, expectedPid, 0, null, "foreground-switched-after-probe");
        if (sent == 0 || !double.IsFinite(elapsed) ||
            elapsed < 0 || elapsed > AiForegroundWindowResponsePolicy.NativeTimeoutMs)
            return new(observed, expectedPid, hwnd.ToInt64(), null,
                "native-timeout-or-unavailable");
        return new(observed, expectedPid, hwnd.ToInt64(),
            Math.Round(elapsed, 3), "WM_NULL-roundtrip");
    }

    private static bool TryGetPid(nint hwnd, out int pid)
    {
        pid = 0;
        uint native = GetWindowThreadProcessId(hwnd, out uint processId);
        if (native == 0 || processId == 0 || processId > int.MaxValue)
            return false;
        pid = (int)processId;
        return true;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(
        nint hwnd, uint msg, nint wParam, nint lParam, uint flags,
        uint timeoutMs, out nint result);
}
