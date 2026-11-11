using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string StartLayoutCuPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string StartLayoutLmPath =
        @"SOFTWARE\Policies\Microsoft\Windows\Explorer";
    private const string StartLayoutCuBackup = "gt.ui.startlayout.cu";
    private const string StartLayoutLmBackup = "gt.ui.startlayout.lm";

    internal static bool AreStartLayoutTargetsApplied(bool currentUser, bool localMachine) =>
        currentUser && localMachine;

    private static Task<bool> DetectStartLayoutAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using RegistryKey cuRoot = RegistryKey.OpenBaseKey(
                RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey lmRoot = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? cu = cuRoot.OpenSubKey(StartLayoutCuPath, writable: false);
            using RegistryKey? lm = lmRoot.OpenSubKey(StartLayoutLmPath, writable: false);
            static bool IsEnabled(RegistryKey? key, string valueName)
            {
                if (key is null) return false;
                object? actual = key.GetValue(valueName, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                return actual is int value && value == 1 &&
                       key.GetValueKind(valueName) == RegistryValueKind.DWord;
            }
            return AreStartLayoutTargetsApplied(
                IsEnabled(cu, "Start_Layout"),
                IsEnabled(lm, "HideRecommendedSection"));
        }, token);

    private static async Task RestoreStartLayoutOriginalsAsync(
        Func<int, CancellationToken, Task<bool>> restoreOriginal)
    {
        var errors = new List<Exception>();
        for (int i = 0; i < 2; i++)
        {
            try
            {
                // A partial HKCU/HKLM mutation must be rolled back even after
                // the original operation was cancelled.
                if (!await restoreOriginal(i, CancellationToken.None).ConfigureAwait(false))
                    errors.Add(new InvalidOperationException(
                        "Chybí původní záloha Start menu: " +
                        (i == 0 ? StartLayoutCuBackup : StartLayoutLmBackup)));
            }
            catch (Exception ex)
            {
                errors.Add(new InvalidOperationException(
                    "Obnovení původního nastavení Start menu selhalo.", ex));
            }
        }
        if (errors.Count > 0)
            throw new AggregateException(
                "Původní nastavení Start menu nebylo úplně obnoveno.", errors);
    }

    // Shared production transaction and fake-operation fault tests.
    internal static async Task ExecuteStartLayoutMutationAsync(
        bool enabled,
        Func<CancellationToken, Task> saveOriginals,
        Func<CancellationToken, Task> applyAndVerify,
        Func<int, CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enabled)
        {
            await RestoreStartLayoutOriginalsAsync(restoreOriginal).ConfigureAwait(false);
            return;
        }

        // Persist BOTH original values before writing either registry hive.
        await saveOriginals(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await applyAndVerify(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception mutationError)
        {
            try
            {
                await RestoreStartLayoutOriginalsAsync(restoreOriginal).ConfigureAwait(false);
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException(
                    "Změna Start menu i obnova původního nastavení selhaly.",
                    mutationError, recoveryError);
            }
            throw;
        }
    }

    private static Task SetStartLayoutAsync(bool enable, CancellationToken ct) =>
        ExecuteStartLayoutMutationAsync(
            enable,
            async token =>
            {
                await TweakFactory.Backups.SaveOriginalAsync(StartLayoutCuBackup,
                    RegistryHive.CurrentUser, StartLayoutCuPath, "Start_Layout", token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await TweakFactory.Backups.SaveOriginalAsync(StartLayoutLmBackup,
                    RegistryHive.LocalMachine, StartLayoutLmPath, "HideRecommendedSection", token)
                    .ConfigureAwait(false);
            },
            token =>
            {
                token.ThrowIfCancellationRequested();
                using RegistryKey cuRoot = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey lmRoot = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey cu = cuRoot.OpenSubKey(StartLayoutCuPath, writable: true)
                    ?? throw new IOException("Nelze otevřít uživatelské nastavení Start menu.");
                // Policy subtree can be absent; create it only during an explicit ON.
                using RegistryKey lm = lmRoot.CreateSubKey(StartLayoutLmPath, writable: true)
                    ?? throw new IOException("Nelze otevřít zásady Start menu.");

                cu.SetValue("Start_Layout", 1, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                lm.SetValue("HideRecommendedSection", 1, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();

                static bool Verified(RegistryKey key, string valueName)
                {
                    object? actual = key.GetValue(valueName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return actual is int value && value == 1 &&
                           key.GetValueKind(valueName) == RegistryValueKind.DWord;
                }
                if (!AreStartLayoutTargetsApplied(
                    Verified(cu, "Start_Layout"),
                    Verified(lm, "HideRecommendedSection")))
                    throw new IOException("Windows nepotvrdil obě hodnoty Start menu.");
                return Task.CompletedTask;
            },
            (index, token) => TweakFactory.Backups.RestoreAsync(
                index == 0 ? StartLayoutCuBackup : StartLayoutLmBackup, token),
            ct);
}
