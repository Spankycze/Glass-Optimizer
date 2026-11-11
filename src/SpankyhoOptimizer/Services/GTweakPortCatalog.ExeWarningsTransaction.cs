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
    private const string ExeWarningsId = "gt.sys.exelaunchwarnings";
    private const string ExeWarningsLmPath = @"SOFTWARE\Microsoft\Internet Explorer\Security";
    private const string ExeWarningsCuPath =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Zones\3";
    private const string ExeWarningsLmName = "DisableSecuritySettingsCheck";
    private const string ExeWarningsCuName = "1806";
    private const string ExeWarningsReceiptVersion = "exe-launch-warnings-v1";
    private static readonly string[] ExeWarningsBackupIds =
        [ExeWarningsId + ".lm", ExeWarningsId + ".cu"];
    private static readonly SemaphoreSlim ExeWarningsGate = new(1, 1);

    private static string ExeWarningsReceiptPath =>
        Path.Combine(LogService.Root, "exe-launch-warnings-original-v1.txt");

    internal static string EncodeExeWarningsReceipt(bool lmExisted, bool cuExisted) =>
        ExeWarningsReceiptVersion + "|" + ExeWarningsId + "|" +
        (lmExisted ? "1" : "0") + "|" + (cuExisted ? "1" : "0");

    internal static (bool LmExisted, bool CuExisted) DecodeExeWarningsReceipt(string text)
    {
        string[] fields = text.TrimEnd('\r', '\n').Split('|');
        if (fields.Length != 4 || fields[0] != ExeWarningsReceiptVersion ||
            fields[1] != ExeWarningsId || fields[2] is not ("0" or "1") ||
            fields[3] is not ("0" or "1"))
            throw new InvalidDataException(
                "Záznam původních hodnot EXE launch warnings je neplatný.");
        return (fields[2] == "1", fields[3] == "1");
    }

    private static async Task<(bool LmExisted, bool CuExisted)> ReadExeWarningsReceiptAsync(
        CancellationToken token)
    {
        if (!File.Exists(ExeWarningsReceiptPath))
            throw new InvalidOperationException(
                "Chybí důvěryhodný záznam původního stavu EXE varování; " +
                "automatické obnovení je zablokováno.");
        return DecodeExeWarningsReceipt(
            await File.ReadAllTextAsync(ExeWarningsReceiptPath, token).ConfigureAwait(false));
    }

    private static bool HasExpectedExeWarningValue(RegistryKey? key, string name, int desired)
    {
        if (key is null ||
            !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;
        object? value = key.GetValue(name, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is int number && number == desired &&
               key.GetValueKind(name) == RegistryValueKind.DWord;
    }

    internal static bool IsExeWarningPolicyActive(bool lmApplied, bool cuApplied) =>
        lmApplied && cuApplied;

    private static Task<bool> DetectExeWarningsDisabledAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey lm = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey cu = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? lmKey = lm.OpenSubKey(ExeWarningsLmPath, false);
            using RegistryKey? cuKey = cu.OpenSubKey(ExeWarningsCuPath, false);
            return IsExeWarningPolicyActive(
                HasExpectedExeWarningValue(lmKey, ExeWarningsLmName, 1),
                HasExpectedExeWarningValue(cuKey, ExeWarningsCuName, 0));
        }, ct);

    private static async Task CaptureExeWarningsOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(ExeWarningsReceiptPath))
        {
            _ = await ReadExeWarningsReceiptAsync(ct).ConfigureAwait(false);
            return;
        }

        bool lmExisted, cuExisted;
        using (RegistryKey lm = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey cu = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser, RegistryView.Registry64))
        using (RegistryKey? lmKey = lm.OpenSubKey(ExeWarningsLmPath, false))
        using (RegistryKey? cuKey = cu.OpenSubKey(ExeWarningsCuPath, false))
        {
            lmExisted = lmKey is not null;
            cuExisted = cuKey is not null;
            // An old version may already have lowered a security prompt without
            // an ownership receipt. Do not capture that as a new pristine state.
            if (HasExpectedExeWarningValue(lmKey, ExeWarningsLmName, 1) ||
                HasExpectedExeWarningValue(cuKey, ExeWarningsCuName, 0))
                throw new InvalidOperationException(
                    "Jedna z hodnot EXE varování je již změněná; bez původního " +
                    "záznamu ji nelze bezpečně převzít ani přepsat.");
        }

        await TweakFactory.Backups.SaveOriginalAsync(
            ExeWarningsBackupIds[0], RegistryHive.LocalMachine,
            ExeWarningsLmPath, ExeWarningsLmName, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await TweakFactory.Backups.SaveOriginalAsync(
            ExeWarningsBackupIds[1], RegistryHive.CurrentUser,
            ExeWarningsCuPath, ExeWarningsCuName, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await AtomicFileService.WriteTextAsync(
            ExeWarningsReceiptPath,
            EncodeExeWarningsReceipt(lmExisted, cuExisted), ct).ConfigureAwait(false);
    }

    internal static bool CanDeleteExeWarningsCreatedKey(
        bool originallyExisted, bool keyExists, int valueCount,
        bool hasChildren, bool onlyExpectedValue) =>
        !keyExists || (!originallyExisted && !hasChildren &&
                       valueCount == 1 && onlyExpectedValue);

    private static async Task ApplyExeWarningsDisabledAsync(CancellationToken ct)
    {
        var receipt = await ReadExeWarningsReceiptAsync(ct).ConfigureAwait(false);
        bool[] originallyExisted = [receipt.LmExisted, receipt.CuExisted];
        RegistryHive[] hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
        string[] paths = [ExeWarningsLmPath, ExeWarningsCuPath];
        string[] names = [ExeWarningsLmName, ExeWarningsCuName];
        int[] desired = [1, 0];

        for (int i = 0; i < hives.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(hives[i], RegistryView.Registry64);
            if (!originallyExisted[i])
            {
                using RegistryKey? current = root.OpenSubKey(paths[i], false);
                if (current is not null &&
                    !CanDeleteExeWarningsCreatedKey(false, true,
                        current.GetValueNames().Length,
                        current.GetSubKeyNames().Length != 0,
                        HasExpectedExeWarningValue(current, names[i], desired[i])))
                    throw new InvalidOperationException(
                        "Cizí registry změny EXE varování nesmějí být přepsány.");
            }
            using RegistryKey key = root.CreateSubKey(paths[i], writable: true)
                ?? throw new IOException("Nastavení EXE varování nelze otevřít.");
            key.SetValue(names[i], desired[i], RegistryValueKind.DWord);
            ct.ThrowIfCancellationRequested();
            if (!HasExpectedExeWarningValue(key, names[i], desired[i]))
                throw new IOException("Windows nepotvrdil hodnotu: " + names[i]);
        }
    }

    private static Task RemoveCreatedExeWarningKeyAsync(
        RegistryHive hive, string path, string name, int desired, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            int cut = path.LastIndexOf('\\');
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? parent = root.OpenSubKey(path[..cut], writable: true);
            if (parent is null) return;
            string leaf = path[(cut + 1)..];
            using (RegistryKey? key = parent.OpenSubKey(leaf, false))
            {
                if (key is null) return;
                if (!CanDeleteExeWarningsCreatedKey(false, true,
                        key.GetValueNames().Length, key.GetSubKeyNames().Length != 0,
                        HasExpectedExeWarningValue(key, name, desired)))
                    throw new InvalidOperationException(
                        "Klíč EXE varování obsahuje cizí změny; odstranění je zablokováno.");
            }
            token.ThrowIfCancellationRequested();
            parent.DeleteSubKey(leaf, throwOnMissingSubKey: false);
            using RegistryKey? remaining = parent.OpenSubKey(leaf, false);
            if (remaining is not null)
                throw new IOException("Windows nepotvrdil obnovení nepřítomného klíče.");
        }, token);

    private static async Task RestoreExeWarningsOriginalAsync(CancellationToken ct)
    {
        var receipt = await ReadExeWarningsReceiptAsync(ct).ConfigureAwait(false);
        bool[] originalKeys = [receipt.LmExisted, receipt.CuExisted];
        RegistryHive[] hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
        string[] paths = [ExeWarningsLmPath, ExeWarningsCuPath];
        string[] names = [ExeWarningsLmName, ExeWarningsCuName];
        int[] desired = [1, 0];
        await RestoreExeWarningTargetsAsync(async (i, token) =>
        {
            if (!originalKeys[i])
            {
                await RemoveCreatedExeWarningKeyAsync(
                    hives[i], paths[i], names[i], desired[i], token).ConfigureAwait(false);
                return;
            }
            using RegistryKey root = RegistryKey.OpenBaseKey(hives[i], RegistryView.Registry64);
            using RegistryKey? current = root.OpenSubKey(paths[i], false);
            if (current is null)
                throw new InvalidOperationException(
                    "Původní klíč EXE varování byl odstraněn mimo Optimizer.");
            // If another process changed our target away from the tweak's
            // enabled value, leave it alone rather than overwriting its state.
            if (!HasExpectedExeWarningValue(current, names[i], desired[i]))
                return;
            if (!await TweakFactory.Backups.RestoreAsync(
                    ExeWarningsBackupIds[i], token).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Chybí původní záloha EXE varování: " + ExeWarningsBackupIds[i]);
        }).ConfigureAwait(false);
    }

    internal static async Task RestoreExeWarningTargetsAsync(
        Func<int, CancellationToken, Task> restoreTarget)
    {
        var failures = new List<Exception>();
        for (int i = 0; i < 2; i++)
        {
            try { await restoreTarget(i, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    "Obnovení EXE varování selhalo: " + ExeWarningsBackupIds[i], ex));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException(
                "Některé původní hodnoty EXE varování nebyly obnoveny.", failures);
    }

    internal static async Task ExecuteExeWarningsMutationAsync(
        bool disableWarnings,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!disableWarnings)
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
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Změna EXE varování i její rollback selhaly.",
                    mutationError, restoreError);
            }
            throw;
        }
    }

    private static async Task SetExeWarningsAsync(bool disabled, CancellationToken ct)
    {
        await ExeWarningsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteExeWarningsMutationAsync(
                disabled, CaptureExeWarningsOriginalAsync,
                ApplyExeWarningsDisabledAsync, RestoreExeWarningsOriginalAsync,
                ct).ConfigureAwait(false);
        }
        finally { ExeWarningsGate.Release(); }
    }

    private static TweakDefinition ExeLaunchWarningsOff() =>
        TweakFactory.Command(
            ExeWarningsId, "system", "Security / Compatibility",
            "EXE launch warnings vypnuté",
            "Pokročilá ruční kompatibilita: vypne některá upozornění při spouštění internetových souborů.",
            DetectExeWarningsDisabledAsync, SetExeWarningsAsync,
            false, TweakRisk.Advanced, false, "Vysoký",
            "Bezpečnostní riziko: snižuje varování při spuštění stažených EXE. Tento tweak nedoporučujeme zapínat.",
            19041, "Any", true, false,
            revert: token => SetExeWarningsAsync(false, token));
}
