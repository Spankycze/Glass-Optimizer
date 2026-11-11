using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string ExplorerHomeId = "gt.ui.explorerhome";
    private const string ExplorerHomeClsid = "{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    private const string ExplorerHomePath = @"Software\Classes\CLSID\" + ExplorerHomeClsid;
    private const string ExplorerHomePinned = "System.IsPinnedToNameSpaceTree";
    private const string ExplorerHomeReceiptVersion = "explorer-home-v1";
    private static readonly SemaphoreSlim ExplorerHomeGate = new(1, 1);

    private static string ExplorerHomeReceiptFile =>
        Path.Combine(LogService.Root, "explorer-home-original-v1.txt");

    internal static string EncodeExplorerHomeReceipt(bool originallyExisted) =>
        ExplorerHomeReceiptVersion + "|" + ExplorerHomeId + "|" +
        (originallyExisted ? "1" : "0");

    internal static bool DecodeExplorerHomeReceipt(string receipt)
    {
        string[] fields = receipt.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 3 || fields[0] != ExplorerHomeReceiptVersion ||
            fields[1] != ExplorerHomeId ||
            (fields[2] != "0" && fields[2] != "1"))
            throw new InvalidDataException(
                "Záznam původního stavu Explorer Home je neplatný.");
        return fields[2] == "1";
    }

    internal static bool CanRemoveExplorerHomeKey(
        bool originallyExisted, bool exists, bool ownedPinnedValue,
        bool otherValues, bool children) =>
        !originallyExisted && exists && ownedPinnedValue &&
        !otherValues && !children;

    private static async Task<bool> ReadExplorerHomeReceiptAsync(CancellationToken ct)
    {
        if (!File.Exists(ExplorerHomeReceiptFile))
            throw new InvalidOperationException(
                "Nelze bezpečně obnovit Home: chybí původní záznam. " +
                "Cizí a starší registry úpravy nebudou odstraněny.");
        return DecodeExplorerHomeReceipt(
            await File.ReadAllTextAsync(ExplorerHomeReceiptFile, ct).ConfigureAwait(false));
    }

    private static Task<bool> DetectExplorerHomeHiddenAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(ExplorerHomePath, writable: false);
            return HasExpectedExplorerHomePinValue(key);
        }, ct);

    private static bool HasExpectedExplorerHomePinValue(RegistryKey? key)
    {
        if (key is null ||
            !key.GetValueNames().Contains(ExplorerHomePinned, StringComparer.OrdinalIgnoreCase))
            return false;
        object? value = key.GetValue(ExplorerHomePinned, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is int pin && pin == 0 &&
               key.GetValueKind(ExplorerHomePinned) == RegistryValueKind.DWord;
    }

    private static async Task CaptureExplorerHomeOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(ExplorerHomeReceiptFile))
        {
            _ = await ReadExplorerHomeReceiptAsync(ct).ConfigureAwait(false);
            return;
        }

        bool keyExisted;
        using (RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser, RegistryView.Registry64))
        using (RegistryKey? key = root.OpenSubKey(ExplorerHomePath, writable: false))
        {
            keyExisted = key is not null;
            // If this particular CLSID was absent originally, it may be
            // created by this tweak. Do not assume ownership of existing keys.
        }
        await TweakFactory.Backups.SaveOriginalAsync(
            ExplorerHomeId, RegistryHive.CurrentUser, ExplorerHomePath,
            ExplorerHomePinned, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            ExplorerHomeReceiptFile, EncodeExplorerHomeReceipt(keyExisted), ct)
            .ConfigureAwait(false);
    }

    private static async Task ApplyExplorerHomeHiddenAsync(CancellationToken ct)
    {
        bool originallyExisted = await ReadExplorerHomeReceiptAsync(ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            if (!originallyExisted)
            {
                using RegistryKey? current = root.OpenSubKey(ExplorerHomePath, writable: false);
                if (current is not null &&
                    (!HasExpectedExplorerHomePinValue(current) ||
                     current.GetValueNames().Length != 1 ||
                     current.GetSubKeyNames().Length != 0))
                    throw new InvalidOperationException(
                        "Explorer Home vznikl po záznamu původního stavu a obsahuje jiná data.");
            }
            using RegistryKey key = root.CreateSubKey(ExplorerHomePath, writable: true)
                ?? throw new IOException("Klíč Explorer Home nelze vytvořit.");
            key.SetValue(ExplorerHomePinned, 0, RegistryValueKind.DWord);
            ct.ThrowIfCancellationRequested();
            if (!HasExpectedExplorerHomePinValue(key))
                throw new IOException("Windows nepotvrdil skrytí Home.");
        }, ct).ConfigureAwait(false);
    }

    private static Task RestoreOriginallyAbsentExplorerHomeAsync(
        CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? parent = root.OpenSubKey(@"Software\Classes\CLSID", writable: true);
            if (parent is null) return;
            using (RegistryKey? child = parent.OpenSubKey(ExplorerHomeClsid, writable: false))
            {
                if (child is null) return;
                var names = child.GetValueNames();
                bool ownsOnlyPin = names.Length == 1 &&
                    string.Equals(names[0], ExplorerHomePinned, StringComparison.OrdinalIgnoreCase) &&
                    HasExpectedExplorerHomePinValue(child);
                if (!CanRemoveExplorerHomeKey(false, true,
                        ownsOnlyPin, names.Length != 1,
                        child.GetSubKeyNames().Length != 0))
                    throw new InvalidOperationException(
                        "Explorer Home obsahuje cizí změny; původně neexistující klíč nelze smazat.");
            }
            ct.ThrowIfCancellationRequested();
            // DeleteSubKey is nonrecursive; if another process added a subkey
            // after readback, this operation fails rather than deleting it.
            parent.DeleteSubKey(ExplorerHomeClsid, throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey(ExplorerHomeClsid, false);
            if (after is not null)
                throw new IOException("Explorer Home původní stav nebyl obnoven.");
        }, ct);

    private static async Task RestoreExplorerHomeAsync(CancellationToken ct)
    {
        bool existed = await ReadExplorerHomeReceiptAsync(ct).ConfigureAwait(false);
        if (!existed)
        {
            await RestoreOriginallyAbsentExplorerHomeAsync(ct).ConfigureAwait(false);
            return;
        }
        if (!await TweakFactory.Backups.RestoreAsync(ExplorerHomeId, ct)
                .ConfigureAwait(false))
            throw new InvalidOperationException(
                "Chybí původní hodnota pro Explorer Home.");
    }

    // Shared seam for production and no-Windows-write fault-injection tests.
    internal static async Task ExecuteExplorerHomeMutationAsync(
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
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Skrytí Explorer Home i obnovení původního stavu selhaly.",
                    mutationError, restoreError);
            }
            throw;
        }
    }

    private static async Task SetExplorerHomeAsync(bool hide, CancellationToken ct)
    {
        await ExplorerHomeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteExplorerHomeMutationAsync(
                hide, CaptureExplorerHomeOriginalAsync, ApplyExplorerHomeHiddenAsync,
                RestoreExplorerHomeAsync, ct).ConfigureAwait(false);
        }
        finally { ExplorerHomeGate.Release(); }
    }

    private static TweakDefinition ExplorerHomeTweak() =>
        TweakFactory.Command(
            ExplorerHomeId, "ui", "Explorer", "Skrýt Home v Exploreru",
            "Skryje Home z navigačního stromu Exploreru bez odstranění uživatelských souborů.",
            DetectExplorerHomeHiddenAsync, SetExplorerHomeAsync,
            false, TweakRisk.Optional, true, "Nízký",
            "Cizí nebo starší úpravy bez uloženého původního stavu se automaticky nemažou.",
            22000, "Any", false, false,
            revert: ct => SetExplorerHomeAsync(false, ct));
}
