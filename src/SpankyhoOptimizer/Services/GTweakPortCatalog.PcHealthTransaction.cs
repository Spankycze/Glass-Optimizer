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
    private const string PcHealthId = "gt.sys.pchealth";
    private const string PcHealthBackupId = "gt.sys.pchealth.original-v1";
    private const string PcHealthPath = @"SOFTWARE\Microsoft\PCHC";
    private const string PcHealthValue = "PreviousUninstall";
    private const string PcHealthReceiptVersion = "pchealth-original-v1";
    private static readonly SemaphoreSlim PcHealthGate = new(1, 1);

    private static string PcHealthReceiptPath =>
        Path.Combine(LogService.Root, "pchealth-original-v1.txt");

    internal readonly record struct PcHealthOriginal(
        bool KeyExisted, bool ValueExisted, int Value);

    internal static string EncodePcHealthOriginal(PcHealthOriginal value) =>
        PcHealthReceiptVersion + "|" + PcHealthId + "|" +
        (value.KeyExisted ? "1" : "0") + "|" +
        (value.ValueExisted ? "1" : "0") + "|" +
        (value.ValueExisted ? value.Value.ToString(CultureInfo.InvariantCulture) : "-");

    internal static PcHealthOriginal DecodePcHealthOriginal(string receipt)
    {
        string[] parts = receipt.TrimEnd('\r', '\n').Split('|');
        if (parts.Length != 5 || parts[0] != PcHealthReceiptVersion ||
            parts[1] != PcHealthId || parts[2] is not ("0" or "1") ||
            parts[3] is not ("0" or "1") ||
            (parts[2] == "0" && parts[3] == "1") ||
            (parts[3] == "0" && parts[4] != "-") ||
            (parts[3] == "1" && !int.TryParse(parts[4],
                NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw new InvalidDataException(
                "Záznam původní hodnoty PC Health Check je neplatný.");
        return new PcHealthOriginal(
            parts[2] == "1", parts[3] == "1",
            parts[3] == "1" ? int.Parse(parts[4], CultureInfo.InvariantCulture) : 0);
    }

    internal static bool PcHealthMatches(
        PcHealthOriginal expected, PcHealthOriginal actual) =>
        expected.KeyExisted == actual.KeyExisted &&
        expected.ValueExisted == actual.ValueExisted &&
        (!expected.ValueExisted || expected.Value == actual.Value);

    internal static bool CanRemoveOwnedPcHealthKey(
        bool originallyExisted, bool currentExisted, int valueCount,
        bool hasChildren, bool exactEnabledValue) =>
        !currentExisted || (!originallyExisted && valueCount == 1 &&
                            !hasChildren && exactEnabledValue);

    private static PcHealthOriginal ReadPcHealthCurrent()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(PcHealthPath, writable: false);
        if (key is null) return new PcHealthOriginal(false, false, 0);
        if (!key.GetValueNames().Contains(PcHealthValue,
                StringComparer.OrdinalIgnoreCase))
            return new PcHealthOriginal(true, false, 0);
        if (key.GetValueKind(PcHealthValue) != RegistryValueKind.DWord ||
            key.GetValue(PcHealthValue, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int value)
            throw new InvalidOperationException(
                "PCHC PreviousUninstall má cizí datový typ; nebude přepsán.");
        return new PcHealthOriginal(true, true, value);
    }

    private static Task<bool> DetectPcHealthAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            PcHealthOriginal observed = ReadPcHealthCurrent();
            return observed.ValueExisted && observed.Value == 1;
        }, ct);

    private static async Task<PcHealthOriginal> ReadPcHealthOriginalAsync(
        CancellationToken ct)
    {
        if (!File.Exists(PcHealthReceiptPath))
            throw new InvalidOperationException(
                "Není uložený původní stav PC Health Check. Bez zálohy jej nelze odhadnout.");
        return DecodePcHealthOriginal(
            await File.ReadAllTextAsync(PcHealthReceiptPath, ct).ConfigureAwait(false));
    }

    private static async Task CapturePcHealthOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(PcHealthReceiptPath))
        {
            _ = await ReadPcHealthOriginalAsync(ct).ConfigureAwait(false);
            return;
        }

        PcHealthOriginal original = ReadPcHealthCurrent();
        if (original.ValueExisted && original.Value == 1)
            throw new InvalidOperationException(
                "PreviousUninstall=1 již existuje bez vlastní zálohy. " +
                "Původní stav starší nebo cizí úpravy nelze bezpečně převzít.");

        // The older generic DWORD toggle used the public ID. A new stable
        // snapshot key avoids silently restoring stale values from that code.
        await TweakFactory.Backups.SaveOriginalAsync(
            PcHealthBackupId, RegistryHive.LocalMachine,
            PcHealthPath, PcHealthValue, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            PcHealthReceiptPath, EncodePcHealthOriginal(original), ct)
            .ConfigureAwait(false);
    }

    private static async Task ApplyPcHealthAsync(CancellationToken ct)
    {
        PcHealthOriginal original = await ReadPcHealthOriginalAsync(ct)
            .ConfigureAwait(false);
        PcHealthOriginal actual = ReadPcHealthCurrent();
        if (!PcHealthMatches(original, actual))
        {
            if (!(actual.ValueExisted && actual.Value == 1))
                throw new InvalidOperationException(
                    "PC Health Check se změnil mimo Optimizer; nepřepíšu cizí hodnotu.");

            if (!original.KeyExisted)
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(PcHealthPath, false);
                if (key is null || !CanRemoveOwnedPcHealthKey(
                        false, true, key.GetValueNames().Length,
                        key.GetSubKeyNames().Length != 0, true))
                    throw new InvalidOperationException(
                        "Nový klíč PCHC obsahuje cizí data; automatická úprava je zablokována.");
            }
            return; // Repeated ON, already matches the tweak.
        }

        ct.ThrowIfCancellationRequested();
        using RegistryKey hive = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey keyToWrite = hive.CreateSubKey(PcHealthPath, writable: true)
            ?? throw new IOException("Nelze vytvořit nastavení PCHC.");
        keyToWrite.SetValue(PcHealthValue, 1, RegistryValueKind.DWord);
        ct.ThrowIfCancellationRequested();
        if (keyToWrite.GetValueKind(PcHealthValue) != RegistryValueKind.DWord ||
            keyToWrite.GetValue(PcHealthValue, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int confirmed ||
            confirmed != 1)
            throw new IOException("Windows nepotvrdil PCHC PreviousUninstall DWORD=1.");
    }

    private static Task RemoveOwnedPcHealthKeyAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? parent = root.OpenSubKey(
                @"SOFTWARE\Microsoft", writable: true);
            if (parent is null) return;
            using (RegistryKey? key = parent.OpenSubKey("PCHC", writable: false))
            {
                if (key is null) return;
                var names = key.GetValueNames();
                bool exact = names.Contains(PcHealthValue, StringComparer.OrdinalIgnoreCase) &&
                    key.GetValueKind(PcHealthValue) == RegistryValueKind.DWord &&
                    key.GetValue(PcHealthValue) is int value && value == 1;
                if (!CanRemoveOwnedPcHealthKey(false, true, names.Length,
                        key.GetSubKeyNames().Length != 0, exact))
                    throw new InvalidOperationException(
                        "PCHC klíč obsahuje další nastavení; jeho odstranění není bezpečné.");
            }
            ct.ThrowIfCancellationRequested();
            parent.DeleteSubKey("PCHC", throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey("PCHC", writable: false);
            if (after is not null)
                throw new IOException("Windows nepotvrdil odstranění vytvořeného PCHC klíče.");
        }, ct);

    private static async Task RestorePcHealthOriginalAsync(CancellationToken ct)
    {
        PcHealthOriginal original = await ReadPcHealthOriginalAsync(ct)
            .ConfigureAwait(false);
        PcHealthOriginal current = ReadPcHealthCurrent();
        if (PcHealthMatches(original, current)) return;

        if (!current.ValueExisted || current.Value != 1)
            throw new InvalidOperationException(
                "Hodnota PCHC byla změněna jiným programem; nepřepíšu ji.");

        if (!original.KeyExisted)
        {
            await RemoveOwnedPcHealthKeyAsync(ct).ConfigureAwait(false);
            return;
        }
        if (!await TweakFactory.Backups.RestoreAsync(
                PcHealthBackupId, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Záloha původní hodnoty PCHC chybí.");
    }

    // Same transaction boundary used by the real tweak and pure fault tests.
    internal static async Task ExecutePcHealthMutationAsync(
        bool enable,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!enable)
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
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Nastavení PCHC i obnova původního stavu selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static async Task SetPcHealthAsync(bool enabled, CancellationToken ct)
    {
        await PcHealthGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecutePcHealthMutationAsync(
                enabled, CapturePcHealthOriginalAsync, ApplyPcHealthAsync,
                RestorePcHealthOriginalAsync, ct).ConfigureAwait(false);
        }
        finally { PcHealthGate.Release(); }
    }

    private static TweakDefinition PcHealthTweak() => new()
    {
        Id = PcHealthId,
        Section = "system",
        Group = "Windows",
        Name = "PC Health Check nepropagovat",
        Description = "Nastaví PreviousUninstall=1 pro omezení opětovné propagace PC Health Check.",
        Risk = TweakRisk.Optional,
        Recommended = false,
        RestartRequired = false,
        Impact = "Nízký",
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RequiresAdmin = true,
        RegistryHive = RegistryHive.LocalMachine,
        RegistryPath = PcHealthPath,
        RegistryValueName = PcHealthValue,
        RegistryEnabledValue = 1,
        TechnicalTarget = "Registry: LocalMachine\\" + PcHealthPath +
            "\\PreviousUninstall -> 1",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = DetectPcHealthAsync,
        SetAsync = SetPcHealthAsync,
        RevertAsync = ct => SetPcHealthAsync(false, ct)
    };
}
