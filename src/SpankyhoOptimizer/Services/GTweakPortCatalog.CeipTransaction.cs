using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string CeipId = "gt.privacy.ceip";
    // Isolate the first original from backups created by the old DWORD factory.
    private const string CeipBackupId = "gt.privacy.ceip.original-v1";
    private const string CeipRegistryPath = @"SOFTWARE\Policies\Microsoft\SQMClient\Windows";
    private const string CeipValueName = "CEIPEnable";
    private const string CeipReceiptVersion = "ceip-original-v1";
    private static readonly SemaphoreSlim CeipGate = new(1, 1);

    private static string CeipReceiptPath =>
        Path.Combine(LogService.Root, "ceip-original-v1.txt");

    internal readonly record struct CeipOriginal(bool KeyExisted, bool ValueExisted, int Value);

    internal static string EncodeCeipOriginal(CeipOriginal original) =>
        CeipReceiptVersion + "|" + CeipId + "|" +
        (original.KeyExisted ? "1" : "0") + "|" +
        (original.ValueExisted ? "1" : "0") + "|" +
        (original.ValueExisted
            ? original.Value.ToString(CultureInfo.InvariantCulture) : "-");

    internal static CeipOriginal DecodeCeipOriginal(string receipt)
    {
        string[] fields = receipt.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 5 || fields[0] != CeipReceiptVersion ||
            fields[1] != CeipId || fields[2] is not ("0" or "1") ||
            fields[3] is not ("0" or "1") ||
            (fields[2] == "0" && fields[3] == "1") ||
            (fields[3] == "0" && fields[4] != "-") ||
            (fields[3] == "1" && !int.TryParse(
                fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw new InvalidDataException("Neplatný záznam původní CEIP politiky.");
        return new CeipOriginal(fields[2] == "1", fields[3] == "1",
            fields[3] == "1"
                ? int.Parse(fields[4], CultureInfo.InvariantCulture) : 0);
    }

    internal static bool CeipStatesMatch(CeipOriginal x, CeipOriginal y) =>
        x.KeyExisted == y.KeyExisted &&
        x.ValueExisted == y.ValueExisted &&
        (!x.ValueExisted || x.Value == y.Value);

    internal static bool CanRemoveOwnedCeipKey(
        bool originallyExisted, bool currentExists, int valueCount,
        bool hasChildren, bool onlyDisabledDword) =>
        !currentExists || (!originallyExisted &&
            valueCount == 1 && !hasChildren && onlyDisabledDword);

    private static CeipOriginal InspectCeipCurrent()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(CeipRegistryPath, writable: false);
        if (key is null) return new CeipOriginal(false, false, 0);
        if (!key.GetValueNames().Contains(CeipValueName,
                StringComparer.OrdinalIgnoreCase))
            return new CeipOriginal(true, false, 0);
        if (key.GetValueKind(CeipValueName) != RegistryValueKind.DWord ||
            key.GetValue(CeipValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int actual)
            throw new InvalidOperationException(
                "CEIP používá vlastní datový typ; cizí politiku nepřepíšu.");
        return new CeipOriginal(true, true, actual);
    }

    private static Task<bool> DetectCeipOffAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            CeipOriginal current = InspectCeipCurrent();
            return current.ValueExisted && current.Value == 0;
        }, token);

    private static async Task<CeipOriginal> ReadCeipOriginalAsync(CancellationToken token)
    {
        if (!File.Exists(CeipReceiptPath))
            throw new InvalidOperationException(
                "Chybí původní záznam CEIP politiky. Nemohu bezpečně určit návratovou hodnotu.");
        return DecodeCeipOriginal(
            await File.ReadAllTextAsync(CeipReceiptPath, token).ConfigureAwait(false));
    }

    private static async Task CaptureCeipOriginalAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(CeipReceiptPath))
        {
            _ = await ReadCeipOriginalAsync(token).ConfigureAwait(false);
            return;
        }
        CeipOriginal original = InspectCeipCurrent();
        if (original.ValueExisted && original.Value == 0)
            throw new InvalidOperationException(
                "CEIP je již vypnuté bez prvotní zálohy. Původní cizí nebo starší změna nebude převzata.");

        await TweakFactory.Backups.SaveOriginalAsync(CeipBackupId,
            RegistryHive.LocalMachine, CeipRegistryPath, CeipValueName, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Another writer could have edited the value during backup. Fail
        // closed instead of writing a receipt that describes a stale baseline.
        if (!CeipStatesMatch(original, InspectCeipCurrent()))
            throw new InvalidOperationException(
                "CEIP politika se změnila během zálohování; zápis je zablokován.");
        await AtomicFileService.WriteTextAsync(CeipReceiptPath,
            EncodeCeipOriginal(original), token).ConfigureAwait(false);
    }

    private static async Task ApplyCeipOffAsync(CancellationToken token)
    {
        CeipOriginal original = await ReadCeipOriginalAsync(token).ConfigureAwait(false);
        CeipOriginal current = InspectCeipCurrent();
        if (!CeipStatesMatch(original, current))
        {
            if (!current.ValueExisted || current.Value != 0)
                throw new InvalidOperationException(
                    "CEIP změnil jiný program; jeho hodnotu nebudu přepisovat.");
            if (!original.KeyExisted)
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(CeipRegistryPath, false);
                if (key is null ||
                    !CanRemoveOwnedCeipKey(false, true, key.GetValueNames().Length,
                        key.GetSubKeyNames().Length != 0, true))
                    throw new InvalidOperationException(
                        "CEIP klíč obsahuje další nastavení; nelze ho převzít.");
            }
            return; // Already applied by this tweak; do not overwrite its backup.
        }

        token.ThrowIfCancellationRequested();
        using RegistryKey hive = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey target = hive.CreateSubKey(CeipRegistryPath, writable: true)
            ?? throw new IOException("CEIP policy nelze otevřít k zápisu.");
        target.SetValue(CeipValueName, 0, RegistryValueKind.DWord);
        token.ThrowIfCancellationRequested();
        if (target.GetValueKind(CeipValueName) != RegistryValueKind.DWord ||
            target.GetValue(CeipValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int value || value != 0)
            throw new IOException("Windows nepotvrdil CEIPEnable REG_DWORD 0.");
    }

    private static Task RemoveOwnedCeipKeyAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey hive = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? parent = hive.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\SQMClient", writable: true);
            if (parent is null) return;
            using (RegistryKey? leaf = parent.OpenSubKey("Windows", writable: false))
            {
                if (leaf is null) return;
                string[] names = leaf.GetValueNames();
                bool hasExpected = names.Contains(
                    CeipValueName, StringComparer.OrdinalIgnoreCase) &&
                    leaf.GetValueKind(CeipValueName) == RegistryValueKind.DWord &&
                    leaf.GetValue(CeipValueName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) is int value &&
                    value == 0;
                if (!CanRemoveOwnedCeipKey(false, true, names.Length,
                        leaf.GetSubKeyNames().Length != 0, hasExpected))
                    throw new InvalidOperationException(
                        "Původně neexistující CEIP klíč nyní obsahuje cizí data; nebude odstraněn.");
            }
            token.ThrowIfCancellationRequested();
            // A nonrecursive deletion cannot destroy child registry subtrees.
            parent.DeleteSubKey("Windows", throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey("Windows", writable: false);
            if (after is not null)
                throw new IOException("Windows nepotvrdil obnovení původní absence CEIP klíče.");
        }, token);

    private static async Task RestoreCeipOriginalAsync(CancellationToken token)
    {
        CeipOriginal original = await ReadCeipOriginalAsync(token).ConfigureAwait(false);
        CeipOriginal current = InspectCeipCurrent();
        if (CeipStatesMatch(original, current)) return;
        if (!current.ValueExisted || current.Value != 0)
            throw new InvalidOperationException(
                "CEIP policy byla mezitím upravena mimo Optimizer; obnovení je zablokováno.");
        if (!original.KeyExisted)
        {
            await RemoveOwnedCeipKeyAsync(token).ConfigureAwait(false);
            return;
        }

        if (!await TweakFactory.Backups.RestoreAsync(CeipBackupId, token)
                .ConfigureAwait(false))
            throw new InvalidOperationException(
                "Původní záloha CEIP politiky nebyla nalezena.");
        if (!CeipStatesMatch(original, InspectCeipCurrent()))
            throw new IOException("Windows nepotvrdil přesné obnovení CEIP hodnoty.");
    }

    // Same production transaction is tested with fake registry/storage delegates.
    internal static async Task ExecuteCeipMutationAsync(
        bool disableCeip,
        Func<CancellationToken, Task> captureOriginal,
        Func<CancellationToken, Task> applyAndVerify,
        Func<CancellationToken, Task> restoreOriginal,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!disableCeip)
        {
            await restoreOriginal(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await captureOriginal(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await applyAndVerify(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception changeFailure)
        {
            try { await restoreOriginal(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception restoreFailure)
            {
                throw new AggregateException(
                    "CEIP politika ani obnova původního stavu nebyly dokončeny.",
                    changeFailure, restoreFailure);
            }
            throw;
        }
    }

    private static async Task SetCeipAsync(bool disable, CancellationToken token)
    {
        await CeipGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ExecuteCeipMutationAsync(disable,
                CaptureCeipOriginalAsync, ApplyCeipOffAsync,
                RestoreCeipOriginalAsync, token).ConfigureAwait(false);
        }
        finally { CeipGate.Release(); }
    }

    private static TweakDefinition CeipTweak() => new()
    {
        Id = CeipId,
        Section = "privacy",
        Group = "Telemetry",
        Name = "Windows CEIP vypnutý",
        Description = "Vypne Customer Experience Improvement Program policy.",
        Recommended = true,
        Risk = TweakRisk.Safe,
        RestartRequired = true,
        Impact = "Nízký",
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RequiresAdmin = true,
        RegistryHive = RegistryHive.LocalMachine,
        RegistryPath = CeipRegistryPath,
        RegistryValueName = CeipValueName,
        RegistryEnabledValue = 0,
        TechnicalTarget = "Registry: LocalMachine\\" + CeipRegistryPath +
            "\\CEIPEnable -> 0",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = DetectCeipOffAsync,
        SetAsync = SetCeipAsync,
        RevertAsync = token => SetCeipAsync(false, token)
    };
}
