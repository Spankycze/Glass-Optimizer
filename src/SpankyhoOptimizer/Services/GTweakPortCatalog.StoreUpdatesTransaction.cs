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
    private const string StoreUpdatesId = "gt.sys.storeautoupdate";
    private const string StoreUpdatesBackupId = "gt.sys.storeautoupdate.original-v1";
    private const string StoreUpdatesKey = @"SOFTWARE\Policies\Microsoft\WindowsStore";
    private const string StoreUpdatesValue = "AutoDownload";
    private const string StoreUpdatesReceiptVersion = "store-autoupdate-original-v1";
    private static readonly SemaphoreSlim StoreUpdatesGate = new(1, 1);
    private static string StoreUpdatesReceiptPath =>
        Path.Combine(LogService.Root, "store-autoupdate-original-v1.txt");

    internal readonly record struct StoreUpdateOriginal(
        bool KeyExisted, bool ValueExisted, int Value);

    internal static string EncodeStoreUpdateOriginal(StoreUpdateOriginal original) =>
        StoreUpdatesReceiptVersion + "|" + StoreUpdatesId + "|" +
        (original.KeyExisted ? "1" : "0") + "|" +
        (original.ValueExisted ? "1" : "0") + "|" +
        (original.ValueExisted ? original.Value.ToString(
            System.Globalization.CultureInfo.InvariantCulture) : "-");

    internal static StoreUpdateOriginal DecodeStoreUpdateOriginal(string receipt)
    {
        string[] fields = receipt.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 5 || fields[0] != StoreUpdatesReceiptVersion ||
            fields[1] != StoreUpdatesId ||
            fields[2] is not ("0" or "1") || fields[3] is not ("0" or "1") ||
            (fields[2] == "0" && fields[3] == "1") ||
            (fields[3] == "0" && fields[4] != "-") ||
            (fields[3] == "1" && !int.TryParse(
                fields[4], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _)))
            throw new InvalidDataException(
                "Záznam původní politiky Microsoft Store je poškozený.");

        return new StoreUpdateOriginal(fields[2] == "1", fields[3] == "1",
            fields[3] == "1"
                ? int.Parse(fields[4], System.Globalization.CultureInfo.InvariantCulture)
                : 0);
    }

    internal static bool IsStoreUpdatePolicyDisabled(
        bool valueExisted, RegistryValueKind kind, object? value) =>
        valueExisted && kind == RegistryValueKind.DWord &&
        value is int number && number == 2;

    private static StoreUpdateOriginal InspectStoreOriginal()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(StoreUpdatesKey, writable: false);
        if (key is null) return new StoreUpdateOriginal(false, false, 0);
        if (!key.GetValueNames().Contains(StoreUpdatesValue,
                StringComparer.OrdinalIgnoreCase))
            return new StoreUpdateOriginal(true, false, 0);
        if (key.GetValueKind(StoreUpdatesValue) != RegistryValueKind.DWord ||
            key.GetValue(StoreUpdatesValue, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int existing)
            throw new InvalidOperationException(
                "Microsoft Store používá jiný datový typ zásady; cizí nastavení se nepřepisuje.");
        return new StoreUpdateOriginal(true, true, existing);
    }

    private static async Task<StoreUpdateOriginal> ReadStoreOriginalAsync(
        CancellationToken ct)
    {
        if (!File.Exists(StoreUpdatesReceiptPath))
            throw new InvalidOperationException(
                "Původní politika Microsoft Store nebyla zazálohována; obnovu nelze bezpečně odhadnout.");
        return DecodeStoreUpdateOriginal(
            await File.ReadAllTextAsync(StoreUpdatesReceiptPath, ct).ConfigureAwait(false));
    }

    private static async Task CaptureStoreOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(StoreUpdatesReceiptPath))
        {
            _ = await ReadStoreOriginalAsync(ct).ConfigureAwait(false);
            return;
        }

        StoreUpdateOriginal original = InspectStoreOriginal();
        if (original.ValueExisted && original.Value == 2)
            throw new InvalidOperationException(
                "Zásada Store již vypíná automatické aktualizace bez záznamu Optimizeru. " +
                "Neznámé původní nastavení nebude převzato.");
        ct.ThrowIfCancellationRequested();

        // A NEW versioned backup ID cannot silently reuse an original recorded
        // by the legacy generic DWORD setter, perhaps at a different baseline.
        await TweakFactory.Backups.SaveOriginalAsync(StoreUpdatesBackupId,
            RegistryHive.LocalMachine, StoreUpdatesKey, StoreUpdatesValue, ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            StoreUpdatesReceiptPath, EncodeStoreUpdateOriginal(original), ct)
            .ConfigureAwait(false);
    }

    private static Task<bool> DetectStoreUpdatesDisabledAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(StoreUpdatesKey, writable: false);
            return key is not null &&
                IsStoreUpdatePolicyDisabled(
                    key.GetValueNames().Contains(StoreUpdatesValue,
                        StringComparer.OrdinalIgnoreCase),
                    key.GetValueNames().Contains(StoreUpdatesValue,
                        StringComparer.OrdinalIgnoreCase)
                        ? key.GetValueKind(StoreUpdatesValue) : RegistryValueKind.None,
                    key.GetValue(StoreUpdatesValue, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames));
        }, ct);

    private static bool StoreOriginalMatches(
        StoreUpdateOriginal original, StoreUpdateOriginal observed) =>
        observed.KeyExisted == original.KeyExisted &&
        observed.ValueExisted == original.ValueExisted &&
        (!observed.ValueExisted || observed.Value == original.Value);

    internal static bool CanDeleteOwnedStorePolicyKey(
        bool originallyExisted, bool keyExisted, int valueCount,
        bool hasChildren, bool valueIsDisabledPolicy) =>
        !keyExisted || (!originallyExisted && valueCount == 1 &&
                        valueIsDisabledPolicy && !hasChildren);

    private static async Task ApplyStoreUpdatesDisabledAsync(CancellationToken ct)
    {
        StoreUpdateOriginal original = await ReadStoreOriginalAsync(ct).ConfigureAwait(false);
        StoreUpdateOriginal current = InspectStoreOriginal();

        if (StoreOriginalMatches(original, current))
        {
            // Original path can be entirely absent, in which case the policy
            // subtree is created only after a successful durable backup.
        }
        else if (current.ValueExisted && current.Value == 2)
        {
            // Already ours from a previous ON. Protect a newly created
            // originally-absent policy subtree with foreign registry content.
            if (!original.KeyExisted)
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(StoreUpdatesKey, false);
                if (key is null ||
                    !CanDeleteOwnedStorePolicyKey(false, true,
                        key.GetValueNames().Length,
                        key.GetSubKeyNames().Length != 0, true))
                    throw new InvalidOperationException(
                        "Zásada Store obsahuje cizí změny; nemohu ji bezpečně převzít.");
            }
            return;
        }
        else
            throw new InvalidOperationException(
                "Zásada Store se změnila mimo Optimizer; její hodnotu nepřepíšu.");

        ct.ThrowIfCancellationRequested();
        using RegistryKey hive = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey keyToWrite = hive.CreateSubKey(StoreUpdatesKey, writable: true)
            ?? throw new IOException("Nelze otevřít zásady Microsoft Store.");
        keyToWrite.SetValue(StoreUpdatesValue, 2, RegistryValueKind.DWord);
        ct.ThrowIfCancellationRequested();
        if (keyToWrite.GetValueKind(StoreUpdatesValue) != RegistryValueKind.DWord ||
            keyToWrite.GetValue(StoreUpdatesValue, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int value || value != 2)
            throw new IOException("Windows nepotvrdil Store AutoDownload DWORD=2.");
    }

    private static Task RemoveStoreUpdatesOwnedKeyAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey hive = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? parent = hive.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft", writable: true);
            if (parent is null) return;
            using (RegistryKey? key = parent.OpenSubKey("WindowsStore", writable: false))
            {
                if (key is null) return;
                if (!CanDeleteOwnedStorePolicyKey(
                        false, true, key.GetValueNames().Length,
                        key.GetSubKeyNames().Length != 0,
                        key.GetValueNames().Contains(StoreUpdatesValue,
                            StringComparer.OrdinalIgnoreCase) &&
                        key.GetValueKind(StoreUpdatesValue) == RegistryValueKind.DWord &&
                        key.GetValue(StoreUpdatesValue) is int value && value == 2))
                    throw new InvalidOperationException(
                        "Klíč Microsoft Store obsahuje cizí úpravy; nebude odstraněn.");
            }
            ct.ThrowIfCancellationRequested();
            // Nonrecursive: never delete a late-added child subtree.
            parent.DeleteSubKey("WindowsStore", throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey("WindowsStore", writable: false);
            if (after is not null)
                throw new IOException("Windows nepotvrdil obnovení nepřítomné politiky Store.");
        }, ct);

    private static async Task RestoreStoreUpdatesOriginalAsync(CancellationToken ct)
    {
        StoreUpdateOriginal original = await ReadStoreOriginalAsync(ct).ConfigureAwait(false);
        StoreUpdateOriginal current = InspectStoreOriginal();
        if (StoreOriginalMatches(original, current)) return;

        if (!current.ValueExisted || current.Value != 2)
            throw new InvalidOperationException(
                "Politika Store byla mezitím změněna jiným nástrojem; obnovení je zablokováno.");

        if (!original.KeyExisted)
        {
            await RemoveStoreUpdatesOwnedKeyAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!await TweakFactory.Backups.RestoreAsync(
                StoreUpdatesBackupId, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Chybí původní záloha politiky Microsoft Store.");
    }

    internal static async Task ExecuteStoreUpdatesMutationAsync(
        bool disableUpdates,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disableUpdates)
        {
            await restore(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await capture(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await apply(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Změna automatických aktualizací Store i obnova původní politiky selhaly.",
                    mutationError, rollbackError);
            }
            throw;
        }
    }

    private static async Task SetStoreUpdatesAsync(bool disableUpdates, CancellationToken ct)
    {
        await StoreUpdatesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteStoreUpdatesMutationAsync(
                disableUpdates, CaptureStoreOriginalAsync,
                ApplyStoreUpdatesDisabledAsync, RestoreStoreUpdatesOriginalAsync, ct)
                .ConfigureAwait(false);
        }
        finally { StoreUpdatesGate.Release(); }
    }

    private static TweakDefinition StoreAutoUpdateTweak() => new()
    {
        Id = StoreUpdatesId,
        Section = "system",
        Group = "Microsoft Store",
        Name = "Store automatické aktualizace vypnuté",
        Description = "Advanced: vypne automatické aktualizace Store aplikací.",
        Recommended = false,
        Risk = TweakRisk.Advanced,
        RestartRequired = false,
        Impact = "Střední",
        Warning = "Bezpečnostní riziko: aplikace Microsoft Store nedostanou automatické aktualizace. Doporučeno ponechat vypnuté nastavení tweaku.",
        RequiresAdmin = true,
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RegistryHive = RegistryHive.LocalMachine,
        RegistryPath = StoreUpdatesKey,
        RegistryValueName = StoreUpdatesValue,
        RegistryEnabledValue = 2,
        TechnicalTarget = "Registry: LocalMachine\\" + StoreUpdatesKey + "\\AutoDownload -> 2",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = DetectStoreUpdatesDisabledAsync,
        SetAsync = SetStoreUpdatesAsync,
        RevertAsync = ct => SetStoreUpdatesAsync(false, ct)
    };
}
