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
    private const string UarId = "gt.privacy.uar";
    private const string UarRegistryPath = @"SOFTWARE\Policies\Microsoft\Windows\AppCompat";
    private const string UarRegistryName = "DisableUAR";
    private const string UarReceiptVersion = "uar-original-v1";
    private static readonly SemaphoreSlim UarGate = new(1, 1);
    private static string UarReceiptPath =>
        Path.Combine(LogService.Root, "uar-original-v1.txt");

    internal readonly record struct UarOriginal(bool KeyExisted, bool ValueExisted, int Value);

    internal static string EncodeUarOriginal(UarOriginal original)
    {
        if (!original.KeyExisted && original.ValueExisted)
            throw new InvalidDataException("UAR value cannot exist without its key.");
        return UarReceiptVersion + "|" + UarId + "|" +
            (original.KeyExisted ? "1" : "0") + "|" +
            (original.ValueExisted ? "1" : "0") + "|" +
            (original.ValueExisted ? original.Value.ToString(CultureInfo.InvariantCulture) : "-");
    }

    internal static UarOriginal DecodeUarOriginal(string text)
    {
        string[] fields = text.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 5 || fields[0] != UarReceiptVersion ||
            fields[1] != UarId || fields[2] is not ("0" or "1") ||
            fields[3] is not ("0" or "1") ||
            (fields[2] == "0" && fields[3] == "1") ||
            (fields[3] == "0" && fields[4] != "-") ||
            (fields[3] == "1" && !int.TryParse(fields[4],
                NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw new InvalidDataException("Původní záznam User Activity Recording je neplatný.");

        return new UarOriginal(fields[2] == "1", fields[3] == "1",
            fields[3] == "1" ? int.Parse(fields[4], CultureInfo.InvariantCulture) : 0);
    }

    internal static bool UarMatches(UarOriginal a, UarOriginal b) =>
        a.KeyExisted == b.KeyExisted &&
        a.ValueExisted == b.ValueExisted &&
        (!a.ValueExisted || a.Value == b.Value);

    internal static bool CanRemoveUarOwnedKey(bool originalKeyExisted,
        bool keyPresent, int valueCount, bool hasChildren, bool soleDwordOne) =>
        !keyPresent || (!originalKeyExisted && valueCount == 1 &&
                        !hasChildren && soleDwordOne);

    private static UarOriginal InspectUarState()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(UarRegistryPath, writable: false);
        if (key is null) return new UarOriginal(false, false, 0);
        if (!key.GetValueNames().Contains(UarRegistryName, StringComparer.OrdinalIgnoreCase))
            return new UarOriginal(true, false, 0);
        if (key.GetValueKind(UarRegistryName) != RegistryValueKind.DWord ||
            key.GetValue(UarRegistryName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int value)
            throw new InvalidOperationException(
                "DisableUAR používá cizí datový typ; hodnota nebude přepsána.");
        return new UarOriginal(true, true, value);
    }

    private static Task<bool> DetectUarDisabledAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            UarOriginal state = InspectUarState();
            return state.ValueExisted && state.Value == 1;
        }, ct);

    private static async Task<UarOriginal> ReadUarOriginalAsync(CancellationToken ct)
    {
        if (!File.Exists(UarReceiptPath))
            throw new InvalidOperationException(
                "Chybí záznam původního DisableUAR. Není bezpečné odhadovat jeho stav.");
        return DecodeUarOriginal(
            await File.ReadAllTextAsync(UarReceiptPath, ct).ConfigureAwait(false));
    }

    private static async Task CaptureUarOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(UarReceiptPath))
        {
            _ = await ReadUarOriginalAsync(ct).ConfigureAwait(false);
            return;
        }
        UarOriginal baseline = InspectUarState();
        if (baseline.ValueExisted && baseline.Value == 1)
            throw new InvalidOperationException(
                "DisableUAR=1 existuje bez původního záznamu; starší/cizí zásah se nepřevezme.");

        ct.ThrowIfCancellationRequested();
        if (!UarMatches(baseline, InspectUarState()))
            throw new InvalidOperationException(
                "UAR byl během zjišťování původního stavu změněn jiným nástrojem.");

        // One durable first-activation snapshot rather than a registry backup
        // combined with a second, potentially inconsistent receipt file.
        await AtomicFileService.WriteTextAsync(UarReceiptPath,
            EncodeUarOriginal(baseline), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!UarMatches(baseline, InspectUarState()))
            throw new InvalidOperationException(
                "UAR se změnil během ukládání původního stavu. Úprava je zablokována.");
    }

    private static bool HasOnlyUarOwnedValue(RegistryKey? key)
    {
        if (key is null) return false;
        string[] names = key.GetValueNames();
        bool ownDword = names.Contains(UarRegistryName, StringComparer.OrdinalIgnoreCase) &&
            key.GetValueKind(UarRegistryName) == RegistryValueKind.DWord &&
            key.GetValue(UarRegistryName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is int v && v == 1;
        return CanRemoveUarOwnedKey(false, true, names.Length,
            key.GetSubKeyNames().Length != 0, ownDword);
    }

    private static async Task ApplyUarDisabledAsync(CancellationToken ct)
    {
        UarOriginal baseline = await ReadUarOriginalAsync(ct).ConfigureAwait(false);
        UarOriginal current = InspectUarState();
        if (!UarMatches(baseline, current))
        {
            if (!current.ValueExisted || current.Value != 1)
                throw new InvalidOperationException(
                    "DisableUAR byl změněn jiným nástrojem; cizí hodnotu nepřepíšu.");
            if (!baseline.KeyExisted)
            {
                using RegistryKey hive = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? key = hive.OpenSubKey(UarRegistryPath, false);
                if (!HasOnlyUarOwnedValue(key))
                    throw new InvalidOperationException(
                        "Nový AppCompat klíč obsahuje další data; nelze ho převzít.");
            }
            return; // Subsequent ON does not replace the original receipt.
        }

        ct.ThrowIfCancellationRequested();
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey target = root.CreateSubKey(UarRegistryPath, writable: true)
            ?? throw new IOException("Není možné otevřít AppCompat policy.");
        target.SetValue(UarRegistryName, 1, RegistryValueKind.DWord);
        ct.ThrowIfCancellationRequested();
        if (target.GetValueKind(UarRegistryName) != RegistryValueKind.DWord ||
            target.GetValue(UarRegistryName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int v || v != 1)
            throw new IOException("Windows nepotvrdil DisableUAR REG_DWORD 1.");
    }

    private static Task RemoveOwnedUarKeyAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey hive = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? parent = hive.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows", writable: true);
            if (parent is null) return;
            using (RegistryKey? key = parent.OpenSubKey("AppCompat", false))
            {
                if (key is null) return;
                if (!HasOnlyUarOwnedValue(key))
                    throw new InvalidOperationException(
                        "AppCompat obsahuje cizí hodnoty/podklíče a nesmí být odstraněn.");
            }
            ct.ThrowIfCancellationRequested();
            // Never delete a subtree recursively.
            parent.DeleteSubKey("AppCompat", throwOnMissingSubKey: false);
            using RegistryKey? after = parent.OpenSubKey("AppCompat", false);
            if (after is not null)
                throw new IOException("Windows nepotvrdil obnovení nepřítomného AppCompat klíče.");
        }, ct);

    private static async Task RestoreUarOriginalAsync(CancellationToken ct)
    {
        UarOriginal original = await ReadUarOriginalAsync(ct).ConfigureAwait(false);
        UarOriginal current = InspectUarState();
        if (UarMatches(original, current)) return;
        if (!current.ValueExisted || current.Value != 1)
            throw new InvalidOperationException(
                "DisableUAR byl změněn mimo Optimizer; původní hodnota nebude vnucena.");

        if (!original.KeyExisted)
            await RemoveOwnedUarKeyAsync(ct).ConfigureAwait(false);
        else
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey hive = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = hive.OpenSubKey(UarRegistryPath, writable: true)
                ?? throw new InvalidOperationException(
                    "Původní AppCompat klíč byl odstraněn; nelze jej rekonstruovat.");
            if (original.ValueExisted)
                key.SetValue(UarRegistryName, original.Value, RegistryValueKind.DWord);
            else
                key.DeleteValue(UarRegistryName, throwOnMissingValue: false);
        }

        if (!UarMatches(original, InspectUarState()))
            throw new IOException("Windows nepotvrdil obnovení původního DisableUAR.");
    }

    internal static async Task ExecuteUarMutationAsync(
        bool disable,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disable)
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
        catch (Exception failure)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception recovery)
            {
                throw new AggregateException(
                    "DisableUAR a obnova původního nastavení současně selhaly.",
                    failure, recovery);
            }
            throw;
        }
    }

    private static async Task SetUarAsync(bool disable, CancellationToken ct)
    {
        await UarGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteUarMutationAsync(
                disable, CaptureUarOriginalAsync, ApplyUarDisabledAsync,
                RestoreUarOriginalAsync, ct).ConfigureAwait(false);
        }
        finally { UarGate.Release(); }
    }

    private static TweakDefinition UarTweak() => new()
    {
        Id = UarId,
        Section = "privacy",
        Group = "Activity / Usage",
        Name = "User Activity Recording vypnutý",
        Description = "Zakáže User Activity Recording přes AppCompat policy.",
        Recommended = true,
        Risk = TweakRisk.Safe,
        RestartRequired = true,
        Impact = "Nízký",
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RequiresAdmin = true,
        RegistryHive = RegistryHive.LocalMachine,
        RegistryPath = UarRegistryPath,
        RegistryValueName = UarRegistryName,
        RegistryEnabledValue = 1,
        TechnicalTarget = "Registry: LocalMachine\\" + UarRegistryPath +
            "\\DisableUAR -> 1",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = DetectUarDisabledAsync,
        SetAsync = SetUarAsync,
        RevertAsync = ct => SetUarAsync(false, ct)
    };
}
