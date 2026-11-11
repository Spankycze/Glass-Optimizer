using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string TypeOverlayId = "gt.ui.typeoverlay";
    private const string TypeOverlayValueName = "ShowTypeOverlay";

    // ON means overlay badges disabled (DWORD 0), OFF means badges enabled
    // (DWORD 1). Revert is a separate action restoring the exact original.
    private static TweakDefinition TypeOverlayBadgesTweak() => new()
    {
        Id = TypeOverlayId,
        Section = "ui",
        Group = "Explorer",
        Name = "Type overlay badges vypnuté",
        Description = "Skryje typové odznaky ikon Exploreru; Obnovit vrátí původní hodnotu.",
        Risk = TweakRisk.Optional,
        Recommended = false,
        RestartRequired = true,
        RequiresAdmin = false,
        Impact = "Nízký",
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RegistryHive = RegistryHive.CurrentUser,
        RegistryPath = ExplorerAdvanced,
        RegistryValueName = TypeOverlayValueName,
        RegistryEnabledValue = 0,
        TechnicalTarget = "Registry: CurrentUser\\" + ExplorerAdvanced + "\\" + TypeOverlayValueName + " -> 0",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = token => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(ExplorerAdvanced, writable: false);
            object? value = key?.GetValue(TypeOverlayValueName);
            return value != null &&
                key!.GetValueKind(TypeOverlayValueName) == RegistryValueKind.DWord &&
                Convert.ToInt64(value) == 0;
        }, token),
        SetAsync = SetTypeOverlayBadgesAsync,
        RevertAsync = async token =>
        {
            if (!await TweakFactory.Backups.RestoreAsync(TypeOverlayId, token).ConfigureAwait(false))
                throw new InvalidOperationException("Původní hodnota Type Overlay Badges není uložená.");
        }
    };

    // Production and fault-injection tests use the same transaction.
    // Persist the original before any write; recover even if the request is cancelled.
    internal static async Task ExecuteTypeOverlayMutationAsync(
        bool turnOffBadges,
        Func<CancellationToken, Task> captureOriginal,
        Func<bool, CancellationToken, Task> setAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await captureOriginal(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            await setAndVerify(turnOffBadges, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception mutationFailure)
        {
            try
            {
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException("Původní hodnota Type Overlay Badges není zálohovaná.");
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("Změna Type Overlay Badges i její obnova selhaly.",
                    mutationFailure, rollbackFailure);
            }
            throw;
        }
    }

    private static Task SetTypeOverlayBadgesAsync(bool on, CancellationToken ct) =>
        ExecuteTypeOverlayMutationAsync(
            on,
            token => TweakFactory.Backups.SaveOriginalAsync(
                TypeOverlayId, RegistryHive.CurrentUser, ExplorerAdvanced, TypeOverlayValueName, token),
            (disable, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = root.CreateSubKey(ExplorerAdvanced, writable: true)
                    ?? throw new IOException("Nelze otevřít nastavení Explorer Type Overlay Badges.");
                int target = disable ? 0 : 1;
                key.SetValue(TypeOverlayValueName, target, RegistryValueKind.DWord);
                token.ThrowIfCancellationRequested();
                if (key.GetValueKind(TypeOverlayValueName) != RegistryValueKind.DWord ||
                    Convert.ToInt64(key.GetValue(TypeOverlayValueName, -1)) != target)
                    throw new IOException("Explorer nepotvrdil hodnotu Type Overlay Badges.");
                return Task.CompletedTask;
            },
            token => TweakFactory.Backups.RestoreAsync(TypeOverlayId, token),
            ct);
}
