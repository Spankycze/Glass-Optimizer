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
    private const string SpeechModelId = "gt.privacy.speechmodel";
    private const string SpeechModelKey = @"SOFTWARE\Policies\Microsoft\Speech";
    private const string SpeechModelValueName = "AllowSpeechModelUpdate";
    private const string SpeechModelReceiptVersion = "speech-model-original-v1";
    private static readonly SemaphoreSlim SpeechModelGate = new(1, 1);
    private static string SpeechModelReceiptPath =>
        Path.Combine(LogService.Root, "speech-model-original-v1.txt");

    internal readonly record struct SpeechModelOriginal(
        bool KeyExisted, bool ValueExisted, int Value);

    internal static string EncodeSpeechModelOriginal(SpeechModelOriginal original)
    {
        if (!original.KeyExisted && original.ValueExisted)
            throw new InvalidDataException("Hodnota řeči nemůže existovat bez klíče.");
        return SpeechModelReceiptVersion + "|" + SpeechModelId + "|" +
            (original.KeyExisted ? "1" : "0") + "|" +
            (original.ValueExisted ? "1" : "0") + "|" +
            (original.ValueExisted
                ? original.Value.ToString(CultureInfo.InvariantCulture) : "-");
    }

    internal static SpeechModelOriginal DecodeSpeechModelOriginal(string text)
    {
        string[] p = text.TrimEnd('\r', '\n').Split('|');
        if (p.Length != 5 || p[0] != SpeechModelReceiptVersion ||
            p[1] != SpeechModelId || p[2] is not ("0" or "1") ||
            p[3] is not ("0" or "1") || (p[2] == "0" && p[3] == "1") ||
            (p[3] == "0" && p[4] != "-") ||
            (p[3] == "1" && !int.TryParse(p[4], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out _)))
            throw new InvalidDataException(
                "Neplatný původní záznam nastavení aktualizací modelu řeči.");
        return new SpeechModelOriginal(p[2] == "1", p[3] == "1",
            p[3] == "1" ? int.Parse(p[4], CultureInfo.InvariantCulture) : 0);
    }

    internal static bool SpeechModelMatches(SpeechModelOriginal original,
        SpeechModelOriginal current) =>
        original.KeyExisted == current.KeyExisted &&
        original.ValueExisted == current.ValueExisted &&
        (!original.ValueExisted || original.Value == current.Value);

    internal static bool CanRemoveSpeechModelOwnedKey(
        bool originallyExisted, bool currentExisted, int valueCount,
        bool hasSubkeys, bool ownedValueIsZero) =>
        !currentExisted || (!originallyExisted && valueCount == 1 &&
            !hasSubkeys && ownedValueIsZero);

    private static SpeechModelOriginal InspectSpeechModel()
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(SpeechModelKey, writable: false);
        if (key is null) return new SpeechModelOriginal(false, false, 0);
        if (!key.GetValueNames().Contains(SpeechModelValueName,
                StringComparer.OrdinalIgnoreCase))
            return new SpeechModelOriginal(true, false, 0);
        if (key.GetValueKind(SpeechModelValueName) != RegistryValueKind.DWord ||
            key.GetValue(SpeechModelValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int v)
            throw new InvalidOperationException(
                "Speech model policy má jiný datový typ; cizí hodnotu nebudu přepisovat.");
        return new SpeechModelOriginal(true, true, v);
    }

    private static Task<bool> DetectSpeechModelOffAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            SpeechModelOriginal value = InspectSpeechModel();
            return value.ValueExisted && value.Value == 0;
        }, ct);

    private static async Task<SpeechModelOriginal> ReadSpeechModelReceiptAsync(
        CancellationToken ct)
    {
        if (!File.Exists(SpeechModelReceiptPath))
            throw new InvalidOperationException(
                "Chybí záloha původní politiky speech modelu. Původní stav nelze odhadnout.");
        return DecodeSpeechModelOriginal(
            await File.ReadAllTextAsync(SpeechModelReceiptPath, ct).ConfigureAwait(false));
    }

    private static async Task CaptureSpeechModelOriginalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(SpeechModelReceiptPath))
        {
            _ = await ReadSpeechModelReceiptAsync(ct).ConfigureAwait(false);
            return;
        }
        SpeechModelOriginal original = InspectSpeechModel();
        if (original.ValueExisted && original.Value == 0)
            throw new InvalidOperationException(
                "Aktualizace speech modelu jsou již zakázány bez původního záznamu; " +
                "dřívější nebo cizí stav nebudu vydávat za nový.");

        // One atomic first-activation record contains BOTH parent existence
        // and original DWORD state. No second backup file can drift from it.
        await AtomicFileService.WriteTextAsync(SpeechModelReceiptPath,
            EncodeSpeechModelOriginal(original), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!SpeechModelMatches(original, InspectSpeechModel()))
            throw new InvalidOperationException(
                "Politika řeči se změnila během zálohování; zásah je zablokován.");
    }

    private static bool HasOnlyOwnedSpeechModelValue(RegistryKey? key)
    {
        if (key is null) return false;
        string[] names = key.GetValueNames();
        bool ownValue = names.Contains(
            SpeechModelValueName, StringComparer.OrdinalIgnoreCase) &&
            key.GetValueKind(SpeechModelValueName) == RegistryValueKind.DWord &&
            key.GetValue(SpeechModelValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is int v && v == 0;
        return CanRemoveSpeechModelOwnedKey(
            false, true, names.Length, key.GetSubKeyNames().Length != 0, ownValue);
    }

    private static async Task ApplySpeechModelOffAsync(CancellationToken ct)
    {
        SpeechModelOriginal original = await ReadSpeechModelReceiptAsync(ct)
            .ConfigureAwait(false);
        SpeechModelOriginal current = InspectSpeechModel();
        if (!SpeechModelMatches(original, current))
        {
            if (!current.ValueExisted || current.Value != 0)
                throw new InvalidOperationException(
                    "Speech model byl mezitím změněn mimo Optimizer; změna je zablokována.");

            if (!original.KeyExisted)
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(SpeechModelKey, false);
                if (!HasOnlyOwnedSpeechModelValue(key))
                    throw new InvalidOperationException(
                        "Klíč řeči obsahuje cizí hodnoty nebo podklíče.");
            }
            return; // Repeat ON: originally recorded state remains immutable.
        }

        ct.ThrowIfCancellationRequested();
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey keyToWrite = baseKey.CreateSubKey(SpeechModelKey, writable: true)
            ?? throw new IOException("Není možné upravit zásadu speech modelu.");
        keyToWrite.SetValue(SpeechModelValueName, 0, RegistryValueKind.DWord);
        ct.ThrowIfCancellationRequested();
        if (keyToWrite.GetValueKind(SpeechModelValueName) != RegistryValueKind.DWord ||
            keyToWrite.GetValue(SpeechModelValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not int value || value != 0)
            throw new IOException("Windows nepotvrdil AllowSpeechModelUpdate DWORD 0.");
    }

    private static Task RemoveOwnedSpeechModelKeyAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? parent = root.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft", writable: true);
            if (parent is null) return;
            using (RegistryKey? key = parent.OpenSubKey("Speech", false))
            {
                if (key is null) return;
                if (!HasOnlyOwnedSpeechModelValue(key))
                    throw new InvalidOperationException(
                        "Původně nepřítomný klíč Speech obsahuje cizí úpravy; nebude odstraněn.");
            }
            ct.ThrowIfCancellationRequested();
            parent.DeleteSubKey("Speech", throwOnMissingSubKey: false);
            using RegistryKey? remaining = parent.OpenSubKey("Speech", false);
            if (remaining is not null)
                throw new IOException("Windows nepotvrdil původní nepřítomnost klíče Speech.");
        }, ct);

    private static async Task RestoreSpeechModelOriginalAsync(CancellationToken ct)
    {
        SpeechModelOriginal original = await ReadSpeechModelReceiptAsync(ct)
            .ConfigureAwait(false);
        SpeechModelOriginal current = InspectSpeechModel();
        if (SpeechModelMatches(original, current)) return;
        if (!current.ValueExisted || current.Value != 0)
            throw new InvalidOperationException(
                "Politika speech modelu byla změněna jiným nástrojem; obnovení je zablokováno.");

        if (!original.KeyExisted)
        {
            await RemoveOwnedSpeechModelKeyAsync(ct).ConfigureAwait(false);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = root.OpenSubKey(SpeechModelKey, writable: true)
                ?? throw new InvalidOperationException(
                    "Původní klíč politiky Speech neexistuje; nebude znovu vytvořen.");
            if (original.ValueExisted)
                key.SetValue(SpeechModelValueName, original.Value, RegistryValueKind.DWord);
            else
                key.DeleteValue(SpeechModelValueName, throwOnMissingValue: false);
        }

        if (!SpeechModelMatches(original, InspectSpeechModel()))
            throw new IOException("Windows nepotvrdil obnovení původní politiky Speech.");
    }

    internal static async Task ExecuteSpeechModelMutationAsync(
        bool disable,
        Func<CancellationToken, Task> capture,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task> restore,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!disable)
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
        catch (Exception writeError)
        {
            try { await restore(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Změna speech modelu ani obnova původní politiky se nedokončily.",
                    writeError, restoreError);
            }
            throw;
        }
    }

    private static async Task SetSpeechModelOffAsync(bool disable, CancellationToken token)
    {
        await SpeechModelGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ExecuteSpeechModelMutationAsync(
                disable, CaptureSpeechModelOriginalAsync, ApplySpeechModelOffAsync,
                RestoreSpeechModelOriginalAsync, token).ConfigureAwait(false);
        }
        finally { SpeechModelGate.Release(); }
    }

    private static TweakDefinition SpeechModelTweak() => new()
    {
        Id = SpeechModelId,
        Section = "privacy",
        Group = "Speech / Input",
        Name = "Aktualizace speech modelu vypnuté",
        Description = "Zakáže automatické aktualizace online speech modelu.",
        Recommended = false,
        Risk = TweakRisk.Optional,
        RestartRequired = false,
        Impact = "Nízký",
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RequiresAdmin = true,
        RegistryHive = RegistryHive.LocalMachine,
        RegistryPath = SpeechModelKey,
        RegistryValueName = SpeechModelValueName,
        RegistryEnabledValue = 0,
        TechnicalTarget = "Registry: LocalMachine\\" + SpeechModelKey +
            "\\AllowSpeechModelUpdate -> 0",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = DetectSpeechModelOffAsync,
        SetAsync = SetSpeechModelOffAsync,
        RevertAsync = ct => SetSpeechModelOffAsync(false, ct)
    };
}
