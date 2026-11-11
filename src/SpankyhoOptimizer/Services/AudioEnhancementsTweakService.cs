using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

/// <summary>
/// Explicit per-device audio SysFx choice. Saves original endpoint values
/// before writing and only reverses modifications still owned by this app.
/// No change is made merely by opening System Tweaks.
/// </summary>
internal static class AudioEnhancementsTweakService
{
    private const string Property = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private const string Render = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string Capture = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    private static readonly string JournalPath =
        Path.Combine(LogService.Root, "audio-enhancements-ownership.json");

    private sealed record OriginalValue(string Path, bool Existed, int Value);

    internal static Task<bool> IsDisabledAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var paths = EnumerateEndpoints();
            if (paths.Count == 0) return false;
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null ||
                    !key.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase) ||
                    key.GetValueKind(Property) != RegistryValueKind.DWord ||
                    Convert.ToInt32(key.GetValue(Property)) != 1)
                    return false;
            }
            return true;
        }, token);

    internal static Task SetDisabledAsync(bool disabled, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            // The setting is machine-wide. Serialize explicit writes across
            // elevated worker processes without starting a polling thread.
            using var mutex = new Mutex(false, @"Global\SpankyhoOptimizerAudioFxTransaction");
            bool acquired = false;
            try
            {
                acquired = WaitForAudioMutex(mutex, token, TimeSpan.FromSeconds(15));
                if (!acquired) throw new TimeoutException("Audio Enhancements: jiná změna stále probíhá.");
                token.ThrowIfCancellationRequested();
                if (disabled) DisableCore(token);
                else RestoreCore(token);
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }, token);

    // WaitAny keeps cancellation responsive while preserving OS-thread mutex
    // ownership: caller must ReleaseMutex from the same worker thread.
    // Do not check cancellation after successful acquisition here: only the
    // caller knows it now owns the mutex and can release it in its finally.
    private static bool WaitForAudioMutex(Mutex mutex, CancellationToken token,
        TimeSpan timeout)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            int signaled = WaitHandle.WaitAny(
                new WaitHandle[] { mutex, token.WaitHandle }, timeout);
            if (signaled == 0) return true;
            if (signaled == 1) token.ThrowIfCancellationRequested();
            return false;
        }
        catch (AbandonedMutexException ex) when (ex.MutexIndex == 0)
        {
            // WaitAny acquired the abandoned mutex on the current thread.
            return true;
        }
    }

    private static List<string> EnumerateEndpoints()
    {
        var endpoints = new List<string>();
        foreach (string rootPath in new[] { Render, Capture })
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root == null) continue;
            foreach (string id in root.GetSubKeyNames())
            {
                string endpoint = rootPath + @"\" + id + @"\FxProperties";
                if (!AudioEnhancementsOwnershipPolicy.IsTrustedEndpointPath(endpoint))
                    continue;
                using var properties = Registry.LocalMachine.OpenSubKey(endpoint);
                if (properties != null) endpoints.Add(endpoint);
            }
        }
        return endpoints;
    }

    private static void DisableCore(CancellationToken token)
    {
        if (File.Exists(JournalPath))
        {
            // Never replace an outstanding rollback journal with a new baseline.
            OriginalValue[] pending = ReadJournal();
            if (pending.Length == 0)
                throw new InvalidOperationException("Chybí platný záznam pro obnovení Audio Enhancements.");

            // Audio endpoints may have been connected since the initial ON.
            // Extend the saved originals durably BEFORE changing any new
            // endpoint; never silently report success while leaving it enabled.
            var recorded = new HashSet<string>(
                pending.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            var added = new List<OriginalValue>();
            foreach (string path in EnumerateEndpoints())
            {
                token.ThrowIfCancellationRequested();
                if (recorded.Contains(path)) continue;
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;
                bool exists = key.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
                if (exists && key.GetValueKind(Property) != RegistryValueKind.DWord)
                    throw new InvalidOperationException("Nové zvukové zařízení používá nepodporovaný typ efektů.");
                int original = exists ? Convert.ToInt32(key.GetValue(Property)) : 0;
                if (!AudioEnhancementsOwnershipPolicy.NeedsNewEndpointSnapshot(
                        false, exists, original)) continue;
                added.Add(new OriginalValue(path, exists, original));
            }

            HashSet<string>? freshlyCapturedPaths = added.Count == 0 ? null :
                new HashSet<string>(added.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            if (added.Count != 0)
            {
                if (pending.Length + added.Count > 1024)
                    throw new InvalidOperationException("Audio Enhancements: příliš mnoho zvukových zařízení.");
                var expanded = new OriginalValue[pending.Length + added.Count];
                Array.Copy(pending, expanded, pending.Length);
                added.CopyTo(expanded, pending.Length);
                // Refuse inaccessible endpoint writes before enlarging the
                // recovery journal. Do not touch the live audio registry.
                PreflightWriteAccess(expanded, token, freshlyCapturedPaths);
                // Reuses the project's atomic, flush-to-disk replace path.
                AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(expanded));
                pending = expanded;
            }
            if (added.Count == 0) PreflightWriteAccess(pending, token);
            ApplyOwned(pending, token);
            return;
        }

        var originals = new List<OriginalValue>();
        foreach (string path in EnumerateEndpoints())
        {
            token.ThrowIfCancellationRequested();
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key == null) continue;
            bool exists = key.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
            if (exists && key.GetValueKind(Property) != RegistryValueKind.DWord)
                throw new InvalidOperationException("Audio endpoint používá jiný typ FxProperties.");
            int original = exists ? Convert.ToInt32(key.GetValue(Property)) : 0;
            if (exists && original == 1) continue; // Already disabled outside this app.
            originals.Add(new OriginalValue(path, exists, original));
        }
        if (originals.Count == 0) return;

        // Fail before journaling/mutating when Windows denies access to
        // an endpoint. This prevents a spurious rollback of untouched keys.
        var firstCapturedPaths = new HashSet<string>(
            originals.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        PreflightWriteAccess(originals, token, firstCapturedPaths);
        // Write-ahead journal ensures recovery after an interrupted write.
        WriteJournal(originals);
        try { ApplyOwned(originals, token); }
        catch
        {
            try { RestoreCore(CancellationToken.None); }
            catch { /* Preserve the journal for a later manual retry. */ }
            throw;
        }
    }

    // Give a precise, read-only endpoint identifier on ACL errors, without
    // modifying Windows/vendor permissions or disclosing unrelated registry paths.
    private static string DescribeEndpoint(string path)
    {
        if (!AudioEnhancementsOwnershipPolicy.IsTrustedEndpointPath(path))
            return "neznámé zvukové zařízení";
        bool output = path.StartsWith(Render + @"\", StringComparison.OrdinalIgnoreCase);
        int end = path.LastIndexOf('\\');
        int start = path.LastIndexOf('\\', end - 1) + 1;
        return (output ? "výstup " : "vstup ") + path[start..end];
    }

    private static RegistryKey? OpenPreflightRead(string path)
    {
        try { return Registry.LocalMachine.OpenSubKey(path); }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                "Windows odepřel čtení nastavení zvukového zařízení (" +
                DescribeEndpoint(path) + "). Změny ještě nezačaly.", ex);
        }
        catch (System.Security.SecurityException ex)
        {
            throw new UnauthorizedAccessException(
                "Windows chrání nastavení zvukového zařízení (" +
                DescribeEndpoint(path) + "). Změny ještě nezačaly.", ex);
        }
    }

    private static void PreflightWriteAccess(IEnumerable<OriginalValue> entries, CancellationToken token,
        IReadOnlySet<string>? freshlyCapturedPaths = null)
    {
        foreach (OriginalValue entry in entries)
        {
            token.ThrowIfCancellationRequested();
            // Read before requesting write permission: endpoints already in
            // our desired state need no writable registry handle.
            using var read = OpenPreflightRead(entry.Path);
            if (read == null) continue; // Device unplugged meanwhile.
            bool exists = read.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
            if (exists && read.GetValueKind(Property) != RegistryValueKind.DWord)
                throw new InvalidOperationException("Audio Enhancements: zvukové zařízení změnilo typ nastavení.");
            int current = exists ? Convert.ToInt32(read.GetValue(Property)) : 0;
            // This endpoint's original was just captured, but no write-ahead
            // journal has been created/extended yet. If a driver externally
            // disabled it meanwhile, refuse to claim that value as our own.
            AudioOwnedWriteDecision decision =
                freshlyCapturedPaths?.Contains(entry.Path) == true
                    ? AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                        entry.Existed, entry.Value, exists, current)
                    : AudioEnhancementsOwnershipPolicy.SelectApply(
                        entry.Existed, entry.Value, exists, current);
            if (decision == AudioOwnedWriteDecision.Conflict)
                throw new InvalidOperationException("Audio Enhancements: mezitím došlo ke změně jinou aplikací.");
            if (decision != AudioOwnedWriteDecision.Apply) continue;
            try
            {
                using var writable = Registry.LocalMachine.OpenSubKey(entry.Path, writable: true);
                // A removed device can disappear between enumeration and open.
                if (writable == null) continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    "Windows nepovolil zápis do nastavení efektů zařízení (" +
                    DescribeEndpoint(entry.Path) + "). " +
                    "Další změny nebyly zahájeny; oprávnění výrobce/Windows nepřebíráme.", ex);
            }
            catch (System.Security.SecurityException ex)
            {
                throw new UnauthorizedAccessException(
                    "Windows zablokoval přístup k chráněnému nastavení zařízení (" +
                    DescribeEndpoint(entry.Path) + "). Další změny nebyly zahájeny.", ex);
            }
        }
    }

    private static RegistryKey? OpenApplyWrite(string path)
    {
        try { return Registry.LocalMachine.OpenSubKey(path, writable: true); }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                "Audio Enhancements: Windows odepřel zápis do nastavení zařízení (" +
                DescribeEndpoint(path) + "). Záznam obnovy zůstává zachován; " +
                "oprávnění výrobce ani Windows se nemění.", ex);
        }
        catch (System.Security.SecurityException ex)
        {
            throw new UnauthorizedAccessException(
                "Audio Enhancements: chráněné nastavení zařízení (" +
                DescribeEndpoint(path) + ") nelze upravit. Záznam obnovy zůstává zachován.", ex);
        }
    }

    private static void ApplyOwned(IEnumerable<OriginalValue> entries, CancellationToken token)
    {
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            // A saved audio endpoint can disappear after device unplugging.
            // Its original is still journaled; do not fail the whole batch.
            // An actual access-denied error still propagates and rolls back.
            // Read-only first. Repeated requests for already-disabled devices
            // must not require the protected FxProperties write permission.
            using var read = Registry.LocalMachine.OpenSubKey(entry.Path);
            if (read == null) continue;
            bool exists = read.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
            if (exists && read.GetValueKind(Property) != RegistryValueKind.DWord)
                throw new InvalidOperationException("Zvukové nastavení změnilo typ.");
            int current = exists ? Convert.ToInt32(read.GetValue(Property)) : 0;
            AudioOwnedWriteDecision decision = AudioEnhancementsOwnershipPolicy.SelectApply(
                entry.Existed, entry.Value, exists, current);
            if (decision == AudioOwnedWriteDecision.Conflict)
                throw new InvalidOperationException("Zvukové nastavení mezitím upravila jiná aplikace.");
            if (decision == AudioOwnedWriteDecision.AlreadyApplied) continue;
            using var key = OpenApplyWrite(entry.Path);
            if (key == null) continue;
            // Check again after opening for writes to avoid clobbering an
            // endpoint changed concurrently by Windows or the vendor driver.
            exists = key.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
            if (exists && key.GetValueKind(Property) != RegistryValueKind.DWord)
                throw new InvalidOperationException("Zvukové nastavení změnilo typ.");
            current = exists ? Convert.ToInt32(key.GetValue(Property)) : 0;
            decision = AudioEnhancementsOwnershipPolicy.SelectApply(
                entry.Existed, entry.Value, exists, current);
            if (decision == AudioOwnedWriteDecision.Conflict)
                throw new InvalidOperationException("Zvukové nastavení mezitím upravila jiná aplikace.");
            if (decision == AudioOwnedWriteDecision.AlreadyApplied) continue;
            try { key.SetValue(Property, 1, RegistryValueKind.DWord); }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    "Audio Enhancements: zápis zamítnut u zařízení (" +
                    DescribeEndpoint(entry.Path) + "). Záznam obnovy zůstává zachován.", ex);
            }
            catch (System.Security.SecurityException ex)
            {
                throw new UnauthorizedAccessException(
                    "Audio Enhancements: chráněný zápis zamítnut u zařízení (" +
                    DescribeEndpoint(entry.Path) + "). Záznam obnovy zůstává zachován.", ex);
            }
            if (Convert.ToInt32(key.GetValue(Property)) != 1)
                throw new InvalidOperationException("Ověření změny Audio Enhancements selhalo.");
        }
    }

    private static void RestoreCore(CancellationToken token)
    {
        if (!File.Exists(JournalPath)) return;
        OriginalValue[] entries = ReadJournal();
        (List<OriginalValue> pending, Exception? firstFailure, bool cancelled) =
            ExecuteRestoreWithPending(entries, token, RestoreOriginalEntry);
        // Remove completed restores from the journal: a subsequent external
        // re-disable is not a write still owned by our old transaction.
        if (pending.Count < entries.Length)
        {
            if (pending.Count == 0) File.Delete(JournalPath);
            else AtomicFileService.WriteText(JournalPath, JsonSerializer.Serialize(pending));
        }
        if (cancelled) token.ThrowIfCancellationRequested();
        if (pending.Count != 0)
            throw new InvalidOperationException(
                $"Audio Enhancements: {pending.Count} zařízení zbývá obnovit " +
                $"(první: {DescribeEndpoint(pending[0].Path)}). " +
                "Záznam pro další pokus obsahuje pouze nedokončená zařízení.", firstFailure);
    }

    private static void RestoreOriginalEntry(OriginalValue entry)
    {
        using var read = Registry.LocalMachine.OpenSubKey(entry.Path);
        // A disconnected endpoint must retain its recovery journal record.
        if (read == null)
            throw new InvalidOperationException(
                "Audio Enhancements: odpojené zařízení; původní nastavení zůstává uloženo.");
        bool exists = read.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
        bool dword = exists && read.GetValueKind(Property) == RegistryValueKind.DWord;
        int current = dword ? Convert.ToInt32(read.GetValue(Property)) : 0;
        if (AudioEnhancementsOwnershipPolicy.SelectRestore(exists, dword, current) !=
            AudioOwnedRestoreDecision.RestoreOriginal) return;

        using var key = Registry.LocalMachine.OpenSubKey(entry.Path, writable: true);
        if (key == null)
            throw new InvalidOperationException(
                "Audio Enhancements: zařízení zmizelo během obnovy; záznam zachován.");
        exists = key.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
        dword = exists && key.GetValueKind(Property) == RegistryValueKind.DWord;
        current = dword ? Convert.ToInt32(key.GetValue(Property)) : 0;
        if (AudioEnhancementsOwnershipPolicy.SelectRestore(exists, dword, current) !=
            AudioOwnedRestoreDecision.RestoreOriginal) return;

        if (entry.Existed) key.SetValue(Property, entry.Value, RegistryValueKind.DWord);
        else key.DeleteValue(Property, throwOnMissingValue: false);

        bool afterExists = key.GetValueNames().Contains(Property, StringComparer.OrdinalIgnoreCase);
        if (afterExists != entry.Existed ||
            (entry.Existed && (key.GetValueKind(Property) != RegistryValueKind.DWord ||
                               Convert.ToInt32(key.GetValue(Property)) != entry.Value)))
            throw new InvalidOperationException(
                "Zvukové nastavení nebylo bezpečně obnoveno: " + entry.Path);
    }

    // Synthetic-friendly collector: failure on one endpoint must not block
    // others. Cancellation retains every not-yet-attempted entry for retry.
    private static (List<T> Pending, Exception? FirstFailure, bool Cancelled)
        ExecuteRestoreWithPending<T>(IReadOnlyList<T> entries,
            CancellationToken token, Action<T> restoreEntry)
    {
        var pending = new List<T>();
        Exception? firstFailure = null;
        for (int i = 0; i < entries.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                for (; i < entries.Count; i++) pending.Add(entries[i]);
                return (pending, firstFailure, true);
            }
            try { restoreEntry(entries[i]); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cancellation can occur inside the endpoint operation: retain
                // this endpoint and all entries not yet attempted.
                for (; i < entries.Count; i++) pending.Add(entries[i]);
                return (pending, firstFailure, true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                System.Security.SecurityException or IOException or InvalidOperationException or
                ArgumentException or FormatException or OverflowException or
                System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Vendor property/type/read errors must not erase other
                // endpoints from the durable rollback journal.
                firstFailure ??= ex;
                pending.Add(entries[i]);
            }
        }
        return (pending, firstFailure, false);
    }

    // Testable without opening audio hardware registry keys.
    private static (int Failures, Exception? FirstFailure) ExecuteBestEffortRestore<T>(
        IEnumerable<T> entries, CancellationToken token, Action<T> restoreEntry)
    {
        Exception? firstFailure = null;
        int failures = 0;
        foreach (T entry in entries)
        {
            token.ThrowIfCancellationRequested();
            try { restoreEntry(entry); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                System.Security.SecurityException or IOException or InvalidOperationException)
            {
                firstFailure ??= ex;
                failures++;
            }
        }
        return (failures, firstFailure);
    }


    // Synthetic test only: no production audio devices or journal touched.
    private static string RunAudioMutexCancellationSmoke()
    {
        using var mutex = new Mutex(false);
        if (!mutex.WaitOne(0))
            throw new InvalidOperationException("Audio mutex synthetic owner unavailable.");
        long cancelMs;
        try
        {
            using var cancel = new CancellationTokenSource();
            using var started = new ManualResetEventSlim(false);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Task<bool> waiting = Task.Factory.StartNew(() =>
            {
                started.Set();
                try
                {
                    bool acquired = WaitForAudioMutex(mutex, cancel.Token,
                        TimeSpan.FromSeconds(5));
                    if (acquired) mutex.ReleaseMutex();
                    return false;
                }
                catch (OperationCanceledException) { return true; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!started.Wait(TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Audio mutex waiter never started.");
            cancel.CancelAfter(80);
            if (!waiting.Wait(TimeSpan.FromSeconds(2)) || !waiting.Result)
                throw new InvalidOperationException("Audio mutex cancel request was ignored.");
            cancelMs = stopwatch.ElapsedMilliseconds;
            if (cancelMs > 1600)
                throw new InvalidOperationException("Audio mutex cancel took too long.");

            Task<bool> timeout = Task.Factory.StartNew(() =>
            {
                bool acquired = WaitForAudioMutex(mutex, CancellationToken.None,
                    TimeSpan.FromMilliseconds(60));
                if (acquired) mutex.ReleaseMutex();
                return !acquired;
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!timeout.Wait(TimeSpan.FromSeconds(3)) || !timeout.Result)
                throw new InvalidOperationException("Busy audio mutex timeout was ignored.");

            using var cancelledToken = new CancellationTokenSource();
            cancelledToken.Cancel();
            bool rejected = false;
            try { _ = WaitForAudioMutex(mutex, cancelledToken.Token, TimeSpan.Zero); }
            catch (OperationCanceledException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("Pre-cancelled audio mutex was acquired.");
        }
        finally { mutex.ReleaseMutex(); }

        if (!WaitForAudioMutex(mutex, CancellationToken.None, TimeSpan.Zero))
            throw new InvalidOperationException("Audio mutex did not become available.");
        mutex.ReleaseMutex();

        // AbandonedMutexException means ownership WAS transferred to this
        // thread. Never treat it as a failed acquisition without releasing.
        using var abandoned = new Mutex(false);
        using var ownerReady = new ManualResetEventSlim(false);
        var orphanOwner = new Thread(() =>
        {
            abandoned.WaitOne();
            ownerReady.Set();
            // Deliberately terminate without releasing this synthetic mutex.
        }) { IsBackground = true };
        orphanOwner.Start();
        if (!ownerReady.Wait(TimeSpan.FromSeconds(3)) ||
            !orphanOwner.Join(TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("Synthetic abandoned audio mutex owner stalled.");
        if (!WaitForAudioMutex(abandoned, CancellationToken.None, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("Audio mutex abandonment was not recovered.");
        abandoned.ReleaseMutex();
        return $"audioMutexCancelMs={cancelMs} audioMutexTimeoutHonored=True " +
            "audioMutexReacquired=True audioMutexAbandonedRecovered=True";
    }

    internal static string RunPermissionFlowSmoke()
    {
        string mutexEvidence = RunAudioMutexCancellationSmoke();
        // A fresh capture must not treat an outside 0 -> 1 transition as
        // our own successful write, or a later rollback might revert it.
        // An older durable journal, however, can legitimately reflect our
        // already-applied 1 from an interrupted previous attempt.
        if (AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                true, 0, true, 1) != AudioOwnedWriteDecision.Conflict ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                false, 0, true, 1) != AudioOwnedWriteDecision.Conflict ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                true, 7, true, 1) != AudioOwnedWriteDecision.Conflict ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                true, 0, true, 0) != AudioOwnedWriteDecision.Apply ||
            AudioEnhancementsOwnershipPolicy.SelectApplyForNewCapture(
                false, 0, false, 0) != AudioOwnedWriteDecision.Apply ||
            AudioEnhancementsOwnershipPolicy.SelectApply(
                true, 0, true, 1) != AudioOwnedWriteDecision.AlreadyApplied)
            throw new InvalidOperationException(
                "Audio fresh-capture preflight can misattribute an external change.");
        // Test partial failure and cancellation without touching HKLM or the user's journal.
        int restoredOk = 0;
        (List<int> unresolved, Exception? deniedReason, bool wasCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2 }, CancellationToken.None, index =>
            {
                if (index == 1) throw new UnauthorizedAccessException("synthetic protected endpoint");
                restoredOk++;
            });
        if (restoredOk != 2 || unresolved.Count != 1 || unresolved[0] != 1 ||
            deniedReason is not UnauthorizedAccessException || wasCancelled)
            throw new InvalidOperationException("Audio partial restore journal failed to retain only blocked endpoints.");
        int continued = 0;
        (List<int> volatilePending, Exception? transientFailure, bool volatileCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2, 3 }, CancellationToken.None, index =>
            {
                if (index == 1) throw new ArgumentException("synthetic changed endpoint type");
                if (index == 2) throw new System.ComponentModel.Win32Exception(5,
                    "synthetic vendor registry read failed");
                continued++;
            });
        if (continued != 2 || volatilePending.Count != 2 || volatilePending[0] != 1 ||
            volatilePending[1] != 2 || transientFailure is not ArgumentException ||
            volatileCancelled)
            throw new InvalidOperationException("Audio rollback stopped after independent vendor failures.");
        (List<int> numericPending, Exception? numericFailure, bool numericCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2, 3 }, CancellationToken.None, index =>
            {
                if (index == 1) throw new FormatException("synthetic malformed DWORD");
                if (index == 2) throw new OverflowException("synthetic DWORD conversion overflow");
            });
        if (numericCancelled || numericPending.Count != 2 ||
            numericPending[0] != 1 || numericPending[1] != 2 ||
            numericFailure is not FormatException)
            throw new InvalidOperationException("Audio numeric read lost rollback endpoints.");
        using var interrupted = new CancellationTokenSource();
        (List<int> interruptedPending, _, bool interruptedCancelled) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2 }, interrupted.Token, index =>
            {
                if (index != 0) throw new InvalidOperationException("Later endpoint was reached.");
                interrupted.Cancel();
                throw new OperationCanceledException(interrupted.Token);
            });
        if (!interruptedCancelled || interruptedPending.Count != 3 ||
            interruptedPending[0] != 0 || interruptedPending[1] != 1 ||
            interruptedPending[2] != 2)
            throw new InvalidOperationException("Audio in-flight cancel dropped current endpoint journal.");
        using var partialCancel = new CancellationTokenSource();
        (List<int> cancelledPending, _, bool cancelledPartway) =
            ExecuteRestoreWithPending(new[] { 0, 1, 2 }, partialCancel.Token,
                index => { if (index == 0) partialCancel.Cancel(); });
        if (!cancelledPartway || cancelledPending.Count != 2 ||
            cancelledPending[0] != 1 || cancelledPending[1] != 2)
            throw new InvalidOperationException("Audio cancel did not retain unattempted journal entries.");
        string firstEndpoint = Render + @"\{11111111-1111-1111-1111-111111111111}\FxProperties";
        string inputEndpoint = Capture + @"\{33333333-3333-3333-3333-333333333333}\FxProperties";
        if (!DescribeEndpoint(firstEndpoint).StartsWith("výstup {11111111", StringComparison.Ordinal) ||
            !DescribeEndpoint(inputEndpoint).StartsWith("vstup {33333333", StringComparison.Ordinal) ||
            DescribeEndpoint(@"SOFTWARE\Unrelated\Key") != "neznámé zvukové zařízení")
            throw new InvalidOperationException("Audio endpoint permission diagnostics regressed.");
        string secondEndpoint = Render + @"\{22222222-2222-2222-2222-222222222222}\FxProperties";
        string partialTestFile = Path.Combine(Path.GetTempPath(),
            "SpankyAudioPartial-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var originals = new[]
            {
                new OriginalValue(firstEndpoint, true, 0),
                new OriginalValue(secondEndpoint, false, 0)
            };
            File.WriteAllText(partialTestFile, JsonSerializer.Serialize(originals),
                new UTF8Encoding(false));
            AtomicFileService.WriteText(partialTestFile,
                JsonSerializer.Serialize(new[] { originals[1] }));
            OriginalValue[] compacted = ReadJournalFile(partialTestFile);
            if (compacted.Length != 1 || compacted[0] != originals[1])
                throw new InvalidOperationException("Audio partial journal atomic compaction failed.");
        }
        finally { if (File.Exists(partialTestFile)) File.Delete(partialTestFile); }
        int attempted = 0, restored = 0;
        (int blocked, Exception? first) = ExecuteBestEffortRestore(
            new[] { 0, 1, 2 }, CancellationToken.None, index =>
            {
                attempted++;
                if (index == 0) throw new UnauthorizedAccessException("synthetic denied");
                restored++;
            });
        if (blocked != 1 || attempted != 3 || restored != 2 ||
            first is not UnauthorizedAccessException)
            throw new InvalidOperationException(
                "Audio Enhancements denied endpoint interrupted remaining rollbacks.");
        (int disconnected, Exception? disconnectedFailure) = ExecuteBestEffortRestore(
            new[] { 0, 1 }, CancellationToken.None, index =>
            {
                if (index == 0)
                    throw new InvalidOperationException("synthetic unplugged endpoint");
            });
        if (disconnected != 1 || disconnectedFailure is not InvalidOperationException)
            throw new InvalidOperationException(
                "Audio Enhancements disconnected endpoint recovery regressed.");
        (int clean, Exception? failure) = ExecuteBestEffortRestore(
            new[] { 0, 1 }, CancellationToken.None, _ => { });
        if (clean != 0 || failure != null ||
            AudioEnhancementsOwnershipPolicy.SelectRestore(true, true, 0) !=
                AudioOwnedRestoreDecision.LeaveExternalChange ||
            AudioEnhancementsOwnershipPolicy.SelectRestore(true, true, 1) !=
                AudioOwnedRestoreDecision.RestoreOriginal ||
            AudioEnhancementsOwnershipPolicy.SelectApply(true, 0, true, 1) !=
                AudioOwnedWriteDecision.AlreadyApplied ||
            AudioEnhancementsOwnershipPolicy.SelectApply(true, 0, true, 0) !=
                AudioOwnedWriteDecision.Apply)
            throw new InvalidOperationException("Audio Enhancements ownership policy regressed.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool cancellationHonored = false;
        try
        {
            _ = ExecuteBestEffortRestore(new[] { 0 }, canceled.Token, _ =>
                throw new InvalidOperationException("Canceled rollback attempted a write."));
        }
        catch (OperationCanceledException) { cancellationHonored = true; }
        if (!cancellationHonored)
            throw new InvalidOperationException("Audio Enhancements rollback ignored cancellation.");
        // Synthetic safety cases only: malformed rollback records cannot
        // silently default original Existed/Value and remove an unknown key.
        string endpoint = Render + @"\{11111111-1111-1111-1111-111111111111}\FxProperties";
        var original = new OriginalValue(endpoint, false, 0);
        string valid = JsonSerializer.Serialize(new[] { original });
        OriginalValue[] roundTrip = ParseJournal(valid);
        if (roundTrip.Length != 1 || roundTrip[0] != original ||
            ParseJournal(JsonSerializer.Serialize(new[] {
                new OriginalValue(endpoint, true, 9) }))[0].Value != 9)
            throw new InvalidOperationException("Audio Enhancements strict journal rejected a valid original.");
        string[] rejected = {
            "[]",
            "null",
            "[{]",
            JsonSerializer.Serialize(new { Path = endpoint }),
            JsonSerializer.Serialize(new { Path = endpoint, Existed = false }),
            valid.Replace("\"Existed\":false", "\"Existed\":false,\"Existed\":true",
                StringComparison.Ordinal),
            valid.Replace("\"Value\":0", "\"Value\":0,\"Unknown\":true",
                StringComparison.Ordinal),
            JsonSerializer.Serialize(new[] { original, original }),
            JsonSerializer.Serialize(new[] { new OriginalValue(endpoint, false, 7) }),
            JsonSerializer.Serialize(new[] { new OriginalValue(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\other",
                false, 0) })
        };
        int blockedJournals = 0;
        foreach (string json in rejected)
        {
            try { _ = ParseJournal(json); }
            catch (InvalidDataException) { blockedJournals++; }
        }
        if (blockedJournals != rejected.Length)
            throw new InvalidOperationException(
                $"Audio Enhancements unsafe journals rejected {blockedJournals}/{rejected.Length}.");
        // File-only smoke; no access to ProgramData or audio registry.
        string testFile = Path.Combine(Path.GetTempPath(),
            "SpankyAudioJournalSmoke-" + Guid.NewGuid().ToString("N") + ".json");
        bool oversizedRejected = false, truncatedRejected = false;
        bool invalidEncodingRejected = false, utf16LeRejected = false;
        bool utf16BeRejected = false, utf8BomAccepted = false;
        try
        {
            File.WriteAllText(testFile, valid, new UTF8Encoding(false, true));
            if (ReadJournalFile(testFile).Length != 1)
                throw new InvalidOperationException("Bounded audio journal rejected valid JSON.");
            // Optional UTF-8 BOM is accepted; other BOM encodings must not
            // silently bypass the strict UTF-8 journal requirement.
            File.WriteAllBytes(testFile,
                new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(valid)).ToArray());
            utf8BomAccepted = ReadJournalFile(testFile).Length == 1;
            File.WriteAllBytes(testFile,
                Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(valid)).ToArray());
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { utf16LeRejected = true; }
            File.WriteAllBytes(testFile,
                Encoding.BigEndianUnicode.GetPreamble().Concat(
                    Encoding.BigEndianUnicode.GetBytes(valid)).ToArray());
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { utf16BeRejected = true; }
            File.WriteAllText(testFile, valid[..^1], new UTF8Encoding(false, true));
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { truncatedRejected = true; }
            // Exactly 512 KiB is legal for complete JSON.
            int padding = MaxJournalBytes - Encoding.UTF8.GetByteCount(valid);
            File.WriteAllText(testFile, valid + new string(' ', padding),
                new UTF8Encoding(false, true));
            if (ReadJournalFile(testFile).Length != 1)
                throw new InvalidOperationException("Exact-limit audio journal was rejected.");
            using (var file = new FileStream(testFile, FileMode.Open,
                FileAccess.Write, FileShare.None))
                file.SetLength(MaxJournalBytes + 1);
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { oversizedRejected = true; }
            // Locked readers must prevent a concurrent writer from replacing
            // a validated JSON body while the restore owns its file handle.
            using (var readerLock = new FileStream(testFile, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                bool writeDenied = false;
                try
                {
                    using var writer = new FileStream(testFile, FileMode.Open,
                        FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException) { writeDenied = true; }
                if (!writeDenied)
                    throw new InvalidOperationException("Audio journal read allowed concurrent mutation.");
            }
            File.WriteAllBytes(testFile, new byte[] { (byte)'[', 0xFF, (byte)']' });
            try { _ = ReadJournalFile(testFile); }
            catch (InvalidDataException) { invalidEncodingRejected = true; }
        }
        finally { if (File.Exists(testFile)) File.Delete(testFile); }
        if (!oversizedRejected || !truncatedRejected || !invalidEncodingRejected ||
            !utf8BomAccepted || !utf16LeRejected || !utf16BeRejected)
            throw new InvalidOperationException("UTF-8-only bounded audio journal file safety regressed.");
        return $"AUDIO_PERMISSION_ROLLBACK_SMOKE_OK attempted={attempted} restored={restored} " +
            $"blocked={blocked} disconnectedRetained={disconnected} cancellationHonored={cancellationHonored} " +
            $"validJournal=true malformedRejected={blockedJournals} " +
            $"boundedFile=true truncatedRejected={truncatedRejected} oversizedRejected={oversizedRejected} " +
            $"invalidEncodingRejected={invalidEncodingRejected} utf8BomAccepted={utf8BomAccepted} " +
            $"utf16LeRejected={utf16LeRejected} utf16BeRejected={utf16BeRejected} {mutexEvidence}";
    }

    private const int MaxJournalBytes = 512 * 1024;

    private static OriginalValue[] ReadJournal() => ReadJournalFile(JournalPath);

    // Read and validate the SAME opened file handle. A separate FileInfo
    // length check followed by File.ReadAllText could inspect different
    // files across an atomic rename. Share only reads while validating.
    private static OriginalValue[] ReadJournalFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length > MaxJournalBytes)
            throw new InvalidDataException("Audio Enhancements ownership journal is oversized.");
        // Do not auto-detect UTF-16/UTF-32 via BOM: ownership journals are
        // UTF-8 only. Read the already bounded bytes through this same locked
        // handle, then accept at most an optional UTF-8 BOM.
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF &&
            bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            string json = new UTF8Encoding(false, true).GetString(
                bytes, offset, bytes.Length - offset);
            return ParseJournal(json);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "Audio Enhancements ownership journal must use valid UTF-8 text.", ex);
        }
    }

    // Deserialize strictly: record positional defaults could otherwise turn
    // a missing Existed/Value field into false/0 and later DELETE an audio
    // property whose true original value is unknown.
    private static OriginalValue[] ParseJournal(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Audio Enhancements ownership journal contains invalid JSON.", ex);
        }
        using (document)
        {
            JsonElement array = document.RootElement;
            if (array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() is 0 or > 1024)
                throw new InvalidDataException("Audio Enhancements ownership journal has invalid count.");

            var entries = new List<OriginalValue>(array.GetArrayLength());
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Audio Enhancements journal entry must be an object.");
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty field in element.EnumerateObject())
                {
                    if (!fields.Add(field.Name) ||
                        field.Name is not ("Path" or "Existed" or "Value"))
                        throw new InvalidDataException("Audio Enhancements journal has duplicate or unexpected fields.");
                }
                if (fields.Count != 3 ||
                    !element.TryGetProperty("Path", out JsonElement pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String ||
                    !element.TryGetProperty("Existed", out JsonElement existedElement) ||
                    existedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !element.TryGetProperty("Value", out JsonElement valueElement) ||
                    valueElement.ValueKind != JsonValueKind.Number ||
                    !valueElement.TryGetInt32(out int originalValue))
                    throw new InvalidDataException("Audio Enhancements journal has missing or invalid original values.");

                if (!existedElement.GetBoolean() && originalValue != 0)
                    throw new InvalidDataException(
                        "Audio Enhancements journal has an inconsistent missing original.");
                string? path = pathElement.GetString();
                if (!AudioEnhancementsOwnershipPolicy.IsTrustedEndpointPath(path) ||
                    !seenPaths.Add(path!))
                    throw new InvalidDataException("Audio Enhancements journal contains an unsafe or duplicate endpoint.");
                entries.Add(new OriginalValue(path!, existedElement.GetBoolean(), originalValue));
            }
            return entries.ToArray();
        }
    }

    private static void WriteJournal(List<OriginalValue> originals)
    {
        WriteAheadOwnershipJournal.CreateNew(
            JournalPath, JsonSerializer.SerializeToUtf8Bytes(originals));
    }
}
