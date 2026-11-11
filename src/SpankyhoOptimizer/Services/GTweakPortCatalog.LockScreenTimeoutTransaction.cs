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
    private const string LockScreenTimeoutId = "gt.sys.lockscreenpower";
    private const string LockScreenTimeoutKey =
        @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\" +
        @"7516b95f-f776-4464-8c53-06167f40cc99\" +
        @"8EC4B3A5-6868-48c2-BE75-4F3044BE88A7";
    private const string LockScreenTimeoutName = "Attributes";
    private static readonly SemaphoreSlim LockScreenTimeoutGate = new(1, 1);

    // No key creation in either the detect or write path: this control only
    // exposes an already-installed Windows power policy setting.
    private static bool IsLockScreenTimeoutExposed(RegistryKey? key)
    {
        if (key is null ||
            !key.GetValueNames().Contains(LockScreenTimeoutName, StringComparer.OrdinalIgnoreCase))
            return false;
        return key.GetValueKind(LockScreenTimeoutName) == RegistryValueKind.DWord &&
            key.GetValue(LockScreenTimeoutName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is int value && value == 2;
    }

    private static Task<bool> DetectLockScreenTimeoutAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using RegistryKey root = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(LockScreenTimeoutKey, writable: false);
            return IsLockScreenTimeoutExposed(key);
        }, ct);

    // Shared operation boundary exercised with fake backup/writer callbacks.
    internal static async Task ExecuteLockScreenTimeoutMutationAsync(
        bool expose,
        Func<CancellationToken, Task> saveOriginal,
        Func<CancellationToken, Task> applyAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!expose)
        {
            if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Chybí záloha původního nastavení Console lock timeout.");
            return;
        }

        await saveOriginal(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await applyAndVerify(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try
            {
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Rollback Console lock timeout nemůže obnovit původní hodnotu.");
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Změna Console lock timeout i její rollback selhaly.",
                    mutationError, rollbackError);
            }
            throw;
        }
    }

    private static async Task SetLockScreenTimeoutAsync(bool expose, CancellationToken ct)
    {
        await LockScreenTimeoutGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExecuteLockScreenTimeoutMutationAsync(
                expose,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    using RegistryKey root = RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine, RegistryView.Registry64);
                    using RegistryKey? key = root.OpenSubKey(LockScreenTimeoutKey, false);
                    if (key is null)
                        throw new InvalidOperationException(
                            "Nastavení Console lock timeout není ve Windows dostupné.");
                    return TweakFactory.Backups.SaveOriginalAsync(
                        LockScreenTimeoutId, RegistryHive.LocalMachine, LockScreenTimeoutKey,
                        LockScreenTimeoutName, token);
                },
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    using RegistryKey root = RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine, RegistryView.Registry64);
                    using RegistryKey key = root.OpenSubKey(LockScreenTimeoutKey, writable: true)
                        ?? throw new IOException(
                            "Nastavení Console lock timeout nelze otevřít k zápisu.");
                    key.SetValue(LockScreenTimeoutName, 2, RegistryValueKind.DWord);
                    token.ThrowIfCancellationRequested();
                    if (!IsLockScreenTimeoutExposed(key))
                        throw new IOException(
                            "Windows nepotvrdil hodnotu Console lock timeout Attributes=2.");
                    return Task.CompletedTask;
                },
                token =>
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine, RegistryView.Registry64);
                    using RegistryKey? key = root.OpenSubKey(
                        LockScreenTimeoutKey, writable: false);
                    if (key is null)
                        throw new InvalidOperationException(
                            "Nastavení Console lock timeout bylo odstraněno mimo Optimizer.");
                    if (!IsLockScreenTimeoutExposed(key))
                        throw new InvalidOperationException(
                            "Console lock timeout byl změněn mimo Optimizer; " +
                            "původní nastavení nepřepíšu.");
                    return TweakFactory.Backups.RestoreAsync(LockScreenTimeoutId, token);
                }, ct).ConfigureAwait(false);
        }
        finally { LockScreenTimeoutGate.Release(); }
    }

    private static TweakDefinition LockScreenTimeoutTweak() => new()
    {
        Id = LockScreenTimeoutId,
        Section = "system",
        Group = "Napájení",
        Name = "Zobrazit Console lock timeout",
        Description = "Odkryje skryté nastavení vypnutí displeje na zamčené obrazovce.",
        Risk = TweakRisk.Optional,
        Recommended = false,
        RestartRequired = false,
        Impact = "Nízký",
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RequiresAdmin = true,
        RegistryHive = RegistryHive.LocalMachine,
        RegistryPath = LockScreenTimeoutKey,
        RegistryValueName = LockScreenTimeoutName,
        RegistryEnabledValue = 2,
        TechnicalTarget = "Registry: LocalMachine\\" + LockScreenTimeoutKey +
            "\\Attributes -> 2",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = DetectLockScreenTimeoutAsync,
        SetAsync = SetLockScreenTimeoutAsync,
        RevertAsync = ct => SetLockScreenTimeoutAsync(false, ct)
    };
}
