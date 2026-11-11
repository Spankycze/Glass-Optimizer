using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string OneDriveNavId = "gt.ui.onedrivenav";
    private const string OneDriveNavClsid = "{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    private const string OneDrivePinnedName = "System.IsPinnedToNameSpaceTree";
    private const string OneDriveNavReceiptVersion = "onedrive-nav-v1";
    private static readonly string[] OneDriveUserPaths =
    [
        @"Software\Classes\CLSID\" + OneDriveNavClsid,
        @"Software\Classes\Wow6432Node\CLSID\" + OneDriveNavClsid
    ];
    private static readonly string[] OneDriveClassesPaths =
    [
        @"CLSID\" + OneDriveNavClsid,
        @"Wow6432Node\CLSID\" + OneDriveNavClsid
    ];
    private static readonly string[] OneDriveBackupIds =
    [
        OneDriveNavId + ".native",
        OneDriveNavId + ".wow6432"
    ];
    private static readonly SemaphoreSlim OneDriveNavGate = new(1, 1);
    private static string OneDriveNavReceiptFile =>
        Path.Combine(LogService.Root, "onedrive-nav-original-v1.txt");

    internal static string EncodeOneDriveNavReceipt(bool nativeExisted, bool wowExisted) =>
        OneDriveNavReceiptVersion + "|" + OneDriveNavId + "|" +
        (nativeExisted ? "1" : "0") + "|" + (wowExisted ? "1" : "0");

    internal static (bool NativeExisted, bool WowExisted) DecodeOneDriveNavReceipt(string value)
    {
        string[] fields = value.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 4 || fields[0] != OneDriveNavReceiptVersion ||
            fields[1] != OneDriveNavId || !IsBooleanField(fields[2]) ||
            !IsBooleanField(fields[3]))
            throw new InvalidDataException(
                "Záznam původního stavu OneDrive navigace je neplatný.");
        return (fields[2] == "1", fields[3] == "1");

        static bool IsBooleanField(string field) => field is "0" or "1";
    }

    internal static bool IsOneDrivePinHidden(RegistryKey? key)
    {
        if (key is null ||
            !key.GetValueNames().Contains(OneDrivePinnedName, StringComparer.OrdinalIgnoreCase))
            return false;
        return key.GetValueKind(OneDrivePinnedName) == RegistryValueKind.DWord &&
            key.GetValue(OneDrivePinnedName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is int pinned && pinned == 0;
    }

    internal static bool CanRemoveOneDriveOverride(
        bool originallyExisted, int valueCount, bool hasSubkeys, bool pinnedIsOurValue) =>
        !originallyExisted && !hasSubkeys &&
        (valueCount == 0 || (valueCount == 1 && pinnedIsOurValue));

    private static async Task<(bool NativeExisted, bool WowExisted)>
        ReadOneDriveNavReceiptAsync(CancellationToken token)
    {
        if (!File.Exists(OneDriveNavReceiptFile))
            throw new InvalidOperationException(
                "Chybí původní záznam OneDrive navigace. Starší nebo cizí změny se neodstraní.");
        return DecodeOneDriveNavReceipt(
            await File.ReadAllTextAsync(OneDriveNavReceiptFile, token).ConfigureAwait(false));
    }

    private static Task<bool> DetectOneDriveNavHiddenAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            // HKCR read-only presents the merged effective HKCU/HKLM registration.
            // Mutations never write to HKCR/HKLM; only our HKCU overlay is changed.
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.ClassesRoot, RegistryView.Registry64);
            foreach (string path in OneDriveClassesPaths)
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey? key = root.OpenSubKey(path, writable: false);
                if (!IsOneDrivePinHidden(key)) return false;
            }
            return true;
        }, token);

    private static async Task CaptureOneDriveNavOriginalsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(OneDriveNavReceiptFile))
        {
            _ = await ReadOneDriveNavReceiptAsync(token).ConfigureAwait(false);
            return;
        }

        bool[] existed = new bool[2];
        using (RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser, RegistryView.Registry64))
        {
            for (int i = 0; i < OneDriveUserPaths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey? key = root.OpenSubKey(OneDriveUserPaths[i], writable: false);
                existed[i] = key is not null;
            }
        }
        for (int i = 0; i < OneDriveUserPaths.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            await TweakFactory.Backups.SaveOriginalAsync(
                OneDriveBackupIds[i], RegistryHive.CurrentUser,
                OneDriveUserPaths[i], OneDrivePinnedName, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            OneDriveNavReceiptFile, EncodeOneDriveNavReceipt(existed[0], existed[1]),
            token).ConfigureAwait(false);
    }

    private static async Task ApplyOneDriveNavHiddenAsync(CancellationToken token)
    {
        var receipt = await ReadOneDriveNavReceiptAsync(token).ConfigureAwait(false);
        bool[] originallyExisted = [receipt.NativeExisted, receipt.WowExisted];
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser, RegistryView.Registry64);
        for (int i = 0; i < OneDriveUserPaths.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            string path = OneDriveUserPaths[i];
            // An originally absent key appearing later may be a third-party
            // override; do not overwrite it unless it is exactly our own state.
            if (!originallyExisted[i])
            {
                using RegistryKey? current = root.OpenSubKey(path, writable: false);
                if (current is not null &&
                    !CanRemoveOneDriveOverride(false, current.GetValueNames().Length,
                        current.GetSubKeyNames().Length != 0, IsOneDrivePinHidden(current)))
                    throw new InvalidOperationException(
                        "OneDrive namespace obsahuje cizí data; změna je zablokována.");
            }
            using RegistryKey key = root.CreateSubKey(path, writable: true)
                ?? throw new IOException("Nelze otevřít uživatelské nastavení OneDrive.");
            key.SetValue(OneDrivePinnedName, 0, RegistryValueKind.DWord);
            token.ThrowIfCancellationRequested();
            if (!IsOneDrivePinHidden(key))
                throw new IOException("Windows nepotvrdil skrytí OneDrive: " + path);
        }
    }

    private static Task RemoveOwnedOneDriveOverrideAsync(
        string fullPath, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            int separator = fullPath.LastIndexOf('\\');
            string parentPath = fullPath[..separator];
            string leafName = fullPath[(separator + 1)..];
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? parent = root.OpenSubKey(parentPath, writable: true);
            if (parent is null) return;
            using (RegistryKey? leaf = parent.OpenSubKey(leafName, writable: false))
            {
                if (leaf is null) return;
                if (!CanRemoveOneDriveOverride(false, leaf.GetValueNames().Length,
                        leaf.GetSubKeyNames().Length != 0, IsOneDrivePinHidden(leaf)))
                    throw new InvalidOperationException(
                        "Klíč OneDrive obsahuje cizí hodnoty nebo podklíče; nebude odstraněn.");
            }
            token.ThrowIfCancellationRequested();
            parent.DeleteSubKey(leafName, throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey(leafName, writable: false);
            if (after is not null)
                throw new IOException("Windows nepotvrdil původní stav OneDrive.");
        }, token);

    private static async Task RestoreOneDriveNavOriginalsAsync(CancellationToken token)
    {
        var receipt = await ReadOneDriveNavReceiptAsync(token).ConfigureAwait(false);
        bool[] originallyExisted = [receipt.NativeExisted, receipt.WowExisted];
        await RestoreOneDriveNavEntriesAsync(async (i, recoveryToken) =>
        {
            if (!originallyExisted[i])
            {
                await RemoveOwnedOneDriveOverrideAsync(
                    OneDriveUserPaths[i], recoveryToken).ConfigureAwait(false);
            }
            else
            {
                // Never reconstruct a key externally removed since capture.
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(OneDriveUserPaths[i], false);
                if (key is null)
                    throw new InvalidOperationException(
                        "Původní OneDrive klíč již neexistuje: " + OneDriveUserPaths[i]);
                if (!await TweakFactory.Backups.RestoreAsync(
                        OneDriveBackupIds[i], recoveryToken).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Chybí původní záloha OneDrive: " + OneDriveBackupIds[i]);
            }
        }).ConfigureAwait(false);
    }

    // A failure restoring one registry view must not skip the other view.
    // The same routine is exercised by no-registry-write fault tests.
    internal static async Task RestoreOneDriveNavEntriesAsync(
        Func<int, CancellationToken, Task> restoreIndex)
    {
        var failures = new List<Exception>();
        for (int i = 0; i < 2; i++)
        {
            try { await restoreIndex(i, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Obnovení OneDrive registry hodnoty selhalo: " + OneDriveUserPaths[i], ex));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException(
                "Původní uživatelské nastavení OneDrive nebylo úplně obnoveno.", failures);
    }
    internal static async Task ExecuteOneDriveNavMutationAsync(
        bool hide, Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!hide)
        {
            await restore(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        await capture(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await apply(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Skrytí OneDrive i obnova původního nastavení selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static async Task SetOneDriveNavAsync(bool hide, CancellationToken token)
    {
        await OneDriveNavGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ExecuteOneDriveNavMutationAsync(hide, CaptureOneDriveNavOriginalsAsync,
                ApplyOneDriveNavHiddenAsync, RestoreOneDriveNavOriginalsAsync, token)
                .ConfigureAwait(false);
        }
        finally { OneDriveNavGate.Release(); }
    }

    private static TweakDefinition ExplorerOneDriveNavTweak() =>
        TweakFactory.Command(OneDriveNavId, "ui", "Explorer",
            "Skrýt OneDrive v navigaci Exploreru",
            "Skryje OneDrive namespace položku, samotný OneDrive neodinstaluje.",
            DetectOneDriveNavHiddenAsync, SetOneDriveNavAsync,
            false, TweakRisk.Optional, true, "Nízký",
            "Upravuje pouze uživatelské CLSID přepsání. Starší stavy bez zálohy nebude mazat.",
            19041, "Any", false, false,
            revert: token => SetOneDriveNavAsync(false, token));
}
