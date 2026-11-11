using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SpankyhoOptimizer.Models;

namespace SpankyhoOptimizer.Services;

internal static partial class GTweakPortCatalog
{
    private const string DesktopLabelShadowId = "gt.ui.desktoplabelshadow";

    // Use the exact same registry target and ID as the original catalog entry.
    // ON/OFF changes the setting explicitly; Revert restores the user's original.
    private static TweakDefinition DesktopLabelShadowTweak() => new()
    {
        Id = DesktopLabelShadowId,
        Section = "ui",
        Group = "Explorer",
        Name = "Stíny popisků ikon na ploše",
        Description = "Přepíná stín textu ikon na ploše; Obnovit vrátí původní hodnotu.",
        Risk = TweakRisk.Optional,
        Recommended = false,
        RestartRequired = false,
        RequiresAdmin = false,
        Impact = "Nízký",
        MinWindowsBuild = 19041,
        HardwareScope = "Any",
        RegistryHive = Microsoft.Win32.RegistryHive.CurrentUser,
        RegistryPath = ExplorerAdvanced,
        RegistryValueName = "ListviewShadow",
        RegistryEnabledValue = 1,
        TechnicalTarget = "Registry: CurrentUser\\" + ExplorerAdvanced + "\\ListviewShadow -> 1",
        RevertMode = "OriginalRegistryValue",
        DetectAsync = token => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(ExplorerAdvanced, writable: false);
            object? value = key?.GetValue("ListviewShadow");
            return value != null && key!.GetValueKind("ListviewShadow") == RegistryValueKind.DWord
                && Convert.ToInt64(value) == 1;
        }, token),
        SetAsync = SetDesktopLabelShadowAsync,
        RevertAsync = async token =>
        {
            if (!await TweakFactory.Backups.RestoreAsync(DesktopLabelShadowId, token).ConfigureAwait(false))
                throw new InvalidOperationException("Původní hodnota stínů popisků ikon není uložená.");
        }
    };

    // Deterministic seam shared by production and tests; failure always restores
    // the saved original, independently of request cancellation.
    internal static async Task ExecuteDesktopLabelShadowMutationAsync(
        bool on,
        Func<CancellationToken, Task> captureOriginal,
        Func<bool, CancellationToken, Task> writeAndVerify,
        Func<CancellationToken, Task<bool>> restoreOriginal,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await captureOriginal(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            await writeAndVerify(on, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception writeError)
        {
            try
            {
                if (!await restoreOriginal(CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException("Záloha původní hodnoty stínu ikon chybí.");
            }
            catch (Exception restoreError)
            {
                throw new AggregateException("Nastavení stínu ikon i jeho rollback selhaly.",
                    writeError, restoreError);
            }
            throw;
        }
    }

    private static Task SetDesktopLabelShadowAsync(bool on, CancellationToken token) =>
        ExecuteDesktopLabelShadowMutationAsync(
            on,
            ct => TweakFactory.Backups.SaveOriginalAsync(DesktopLabelShadowId,
                RegistryHive.CurrentUser, ExplorerAdvanced, "ListviewShadow", ct),
            (enabled, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = root.CreateSubKey(ExplorerAdvanced, writable: true)
                    ?? throw new IOException("Nelze změnit nastavení stínu ikon v Exploreru.");
                int desired = enabled ? 1 : 0;
                key.SetValue("ListviewShadow", desired, RegistryValueKind.DWord);
                ct.ThrowIfCancellationRequested();
                if (key.GetValueKind("ListviewShadow") != RegistryValueKind.DWord ||
                    Convert.ToInt64(key.GetValue("ListviewShadow", -1)) != desired)
                    throw new IOException("Explorer nepotvrdil nastavení stínu ikon.");
                return Task.CompletedTask;
            },
            ct => TweakFactory.Backups.RestoreAsync(DesktopLabelShadowId, ct),
            token);
}
