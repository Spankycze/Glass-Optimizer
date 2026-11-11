using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string ClassicMenuId = "gt.ui.classicmenu";
    private const string ClassicMenuRoot =
        @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string ClassicMenuLeaf = "InprocServer32";
    private const string ClassicMenuPath = ClassicMenuRoot + @"\" + ClassicMenuLeaf;
    private const string ClassicMenuOriginalValueId = "gt.ui.classicmenu.original-default";
    private const string ClassicMenuReceiptPrefix = "classic-menu-v1|";
    private static readonly SemaphoreSlim ClassicMenuGate = new(1, 1);
    private static string ClassicMenuReceiptPath =>
        Path.Combine(LogService.Root, "classic-menu-original-v1.txt");

    // A receipt is durable ownership information. The old implementation
    // deleted the entire CLSID tree regardless of who created it.
    private readonly record struct ClassicMenuReceipt(bool RootExisted, bool LeafExisted);

    private static string EncodeClassicMenuReceipt(bool rootExisted, bool leafExisted) =>
        ClassicMenuReceiptPrefix + (rootExisted ? "1" : "0") + "|" +
        (leafExisted ? "1" : "0");

    private static ClassicMenuReceipt ParseClassicMenuReceipt(string text)
    {
        string[] parts = text.TrimEnd('\r', '\n').Split('|');
        if (parts.Length != 3 || parts[0] != "classic-menu-v1" ||
            (parts[1] != "0" && parts[1] != "1") ||
            (parts[2] != "0" && parts[2] != "1") ||
            (parts[1] == "0" && parts[2] == "1"))
            throw new InvalidDataException(
                "Záznam původního stavu klasického kontextového menu je neplatný.");
        return new ClassicMenuReceipt(parts[1] == "1", parts[2] == "1");
    }

    private static async Task<ClassicMenuReceipt> LoadClassicMenuReceiptAsync(
        CancellationToken token)
    {
        if (!File.Exists(ClassicMenuReceiptPath))
            throw new InvalidOperationException(
                "Nelze bezpečně vypnout klasické menu: chybí záznam původního stavu. " +
                "Starší nebo cizí nastavení nebude odstraněno.");
        return ParseClassicMenuReceipt(
            await File.ReadAllTextAsync(ClassicMenuReceiptPath, token).ConfigureAwait(false));
    }

    private static async Task CaptureClassicMenuOriginalAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(ClassicMenuReceiptPath))
        {
            _ = await LoadClassicMenuReceiptAsync(token).ConfigureAwait(false);
            return;
        }

        bool rootExisted, leafExisted;
        using (RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser, RegistryView.Registry64))
        {
            using RegistryKey? originalRoot = root.OpenSubKey(ClassicMenuRoot, false);
            rootExisted = originalRoot is not null;
            using RegistryKey? originalLeaf = originalRoot?.OpenSubKey(ClassicMenuLeaf, false);
            leafExisted = originalLeaf is not null;
        }

        // The pre-existing default REG_SZ (including absence) is saved under a
        // dedicated stable ID before any write. This never overwrites backups.
        await TweakFactory.Backups.SaveOriginalAsync(
            ClassicMenuOriginalValueId, RegistryHive.CurrentUser,
            ClassicMenuPath, string.Empty, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            ClassicMenuReceiptPath,
            EncodeClassicMenuReceipt(rootExisted, leafExisted),
            token).ConfigureAwait(false);
    }

    private static Task<bool> DetectClassicMenuAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(ClassicMenuPath, writable: false);
            return IsClassicMenuEnabled(key);
        }, token);

    private static bool IsClassicMenuEnabled(RegistryKey? key)
    {
        if (key is null || !key.GetValueNames().Contains(string.Empty, StringComparer.Ordinal))
            return false;
        object? value = key.GetValue(string.Empty, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is string text && text.Length == 0 &&
               key.GetValueKind(string.Empty) == RegistryValueKind.String;
    }

    private static Task ApplyClassicMenuAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey key = root.CreateSubKey(ClassicMenuPath, writable: true)
                ?? throw new IOException("Klíč klasického kontextového menu nelze vytvořit.");
            key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
            token.ThrowIfCancellationRequested();
            if (!IsClassicMenuEnabled(key))
                throw new IOException("Windows nepotvrdil zapnutí klasického menu.");
        }, token);

    // Deleting an existing CLSID is unsafe. Only a leaf created by this
    // application may be deleted, and ONLY while it matches the exact empty
    // default value that this application wrote. Fail closed on modifications.
    private static bool CanRemoveClassicMenuLeaf(
        bool emptyDefault, bool hasOtherValues, bool hasChildKeys) =>
        emptyDefault && !hasOtherValues && !hasChildKeys;

    private static Task RemoveOwnedClassicMenuLeafAsync(
        ClassicMenuReceipt receipt, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey hive = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? clsid = hive.OpenSubKey(ClassicMenuRoot, writable: true);
            if (clsid is null) return;

            using (RegistryKey? leaf = clsid.OpenSubKey(ClassicMenuLeaf, writable: false))
            {
                if (leaf is not null)
                {
                    string[] valueNames = leaf.GetValueNames();
                    bool hasOtherValues = valueNames.Any(name => name.Length != 0);
                    bool hasChildren = leaf.GetSubKeyNames().Length != 0;
                    if (!CanRemoveClassicMenuLeaf(
                            IsClassicMenuEnabled(leaf),
                            hasOtherValues, hasChildren))
                        throw new InvalidOperationException(
                            "Klíč klasického menu obsahuje cizí úpravy; nebude odstraněn.");
                }
            }

            // If we created the CLSID itself, never erase new user content.
            if (!receipt.RootExisted &&
                (clsid.GetValueNames().Length > 0 ||
                 clsid.GetSubKeyNames().Any(name =>
                     !string.Equals(name, ClassicMenuLeaf,
                         StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException(
                    "CLSID obsahuje další nastavení; automatické odstranění je zablokováno.");

            if (clsid.OpenSubKey(ClassicMenuLeaf, false) is { } disposable)
            {
                disposable.Dispose();
                clsid.DeleteSubKey(ClassicMenuLeaf, throwOnMissingSubKey: false);
            }
            if (!receipt.RootExisted)
            {
                if (clsid.GetValueNames().Length != 0 ||
                    clsid.GetSubKeyNames().Length != 0)
                    throw new IOException("CLSID obsahuje další nastavení; nelze jej odstranit.");
            }
            // The CLSID handle must be closed before deleting its key.
            clsid.Close();
            if (!receipt.RootExisted)
                hive.DeleteSubKey(ClassicMenuRoot, throwOnMissingSubKey: false);
            using RegistryKey? after = hive.OpenSubKey(ClassicMenuPath, false);
            if (after is not null)
                throw new IOException("Klíč klasického menu zůstal aktivní.");
        }, token);

    private static async Task RestoreClassicMenuAsync(CancellationToken token)
    {
        ClassicMenuReceipt receipt = await LoadClassicMenuReceiptAsync(token).ConfigureAwait(false);
        if (receipt.LeafExisted)
        {
            if (!await TweakFactory.Backups.RestoreAsync(
                    ClassicMenuOriginalValueId, token).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Původní výchozí hodnota klasického menu není zálohovaná.");
        }
        else
        {
            await RemoveOwnedClassicMenuLeafAsync(receipt, token).ConfigureAwait(false);
        }
    }

    // A single source of truth for user OFF and rollback after failed ON.
    // Tests invoke this with fake capture/write/restore delegates.
    internal static async Task ExecuteClassicMenuMutationAsync(
        bool enable,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enable)
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
            try
            {
                await restore(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Změna klasického menu i její obnova selhaly.",
                    mutationError, rollbackError);
            }
            throw;
        }
    }

    private static async Task SetClassicMenuAsync(bool enable, CancellationToken token)
    {
        await ClassicMenuGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ExecuteClassicMenuMutationAsync(
                enable, CaptureClassicMenuOriginalAsync, ApplyClassicMenuAsync,
                RestoreClassicMenuAsync, token).ConfigureAwait(false);
        }
        finally { ClassicMenuGate.Release(); }
    }
}
