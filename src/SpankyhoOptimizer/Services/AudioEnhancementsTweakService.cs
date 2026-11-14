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

internal static partial class AudioEnhancementsTweakService
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
}
