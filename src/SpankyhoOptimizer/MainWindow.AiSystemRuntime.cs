using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    // Pure UI-regression assertions: no current or future-dated JSON snapshot
    // may show ONLINE, CPU activity or a valid AI action after clock changes.
    private static string RunAiRuntimeUiFreshnessSmoke()
    {
        DateTime now = new(2026, 10, 22, 7, 0, 0, DateTimeKind.Utc);
        var fanTtl = TimeSpan.FromSeconds(10);
        var systemTtl = TimeSpan.FromMinutes(5);
        var powerPageTtl = TimeSpan.FromSeconds(12);
        bool Fresh(DateTime value, TimeSpan ttl) =>
            AiSystemSmoothnessGuardService.IsFresh(value, now, ttl);
        foreach (var ttl in new[] { fanTtl, systemTtl, powerPageTtl })
        {
            if (!Fresh(now, ttl) || !Fresh(now - ttl, ttl) ||
                Fresh(now - ttl - TimeSpan.FromTicks(1), ttl) ||
                Fresh(now.AddTicks(1), ttl) ||
                Fresh(new DateTime(now.Ticks, DateTimeKind.Unspecified), ttl) ||
                Fresh(default, ttl))
                throw new InvalidOperationException(
                    "AI Runtime current/future status freshness policy regressed.");
        }
        return "AI_RUNTIME_UI_FRESHNESS_SMOKE_OK fan10s=true system5m=true " +
               "power12s=true futureRejected=true utcRequired=true writes=0";
    }

    private readonly SemaphoreSlim _aiRuntimeRestoreGate = new(1, 1);

    private Task BuildAiSystemRuntimePageAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heroActions.Children.Add(PrimaryQuickButton("Ventilátory", () => ShowSectionAsync("lianli")));
        heroActions.Children.Add(QuickButton("Obnovit vše", async () =>
        {
            // Restoration must not be cancelled midway merely because the
            // user navigates away. Reject duplicate presses while a restore
            // transaction is already in flight.
            if (!await _aiRuntimeRestoreGate.WaitAsync(0)) return;
            try
            {
                await Task.Run(() =>
                    AiSystemResourceManagerService.RestoreAllNowAsync(_windowLifetimeCts.Token),
                    _windowLifetimeCts.Token);
                if (_section == "ai-runtime")
                    await RefreshAiRuntimePageAsync();
            }
            catch (OperationCanceledException) when (_windowLifetimeCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                LogService.Error("ai-runtime", "Restore all failed", ex);
                if (!_windowLifetimeCts.IsCancellationRequested)
                    await ShowMessageAsync("Obnova AI nastavení",
                        "Obnova nebyla dokončena. Původní stav se musí ještě ověřit. " + ex.Message);
            }
            finally { _aiRuntimeRestoreGate.Release(); }
        }));

        ContentStack.Children.Add(BuildModernSectionHero(
            "ai-runtime",
            "AI System Runtime",
            "Přehled, řízení a ochrana na jedné stránce.",
            "AI RUNTIME",
            "PŘEHLED • SPRÁVA WINDOWS • CLOUD • OCHRANA",
            heroActions));

        var bodyHost = new StackPanel { Spacing = ContentStack.Spacing, Tag = "AiRuntimeBodyHost" };
        bodyHost.Children.Add(new TextBlock
        {
            Text = "Načítám stav AI runtime…",
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        ContentStack.Children.Add(bodyHost);

        SafeTaskService.Forget(
            LoadAiRuntimeBodyDeferredAsync(bodyHost, _navigationGeneration, token),
            "ai-runtime",
            "Deferred runtime status");
        return Task.CompletedTask;
    }

    private sealed record AiRuntimePageSnapshot(
        FanProfile Profile,
        FanAgentHealth? FanHealth,
        AiSystemRuntimeConfig Config,
        AiSystemRuntimeHealth? SystemHealth,
        AiCloudProfileStatus CloudStatus,
        GoogleDriveAuthStatus GoogleAuth,
        AdaptivePowerRuntimeStatus? PowerStatus);

    // Two independent read-only groups can load concurrently. The cold
    // health/profile JSON and cloud/power/baseline metadata were previously
    // read in series (227 ms in the last cold AI Runtime UI smoke), though
    // their providers share no mutable state. Keep all IO off the UI thread.
    private static async Task<AiRuntimePageSnapshot> ReadAiRuntimePageSnapshotAsync(
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var systemGroup = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            FanProfile profile = FanProfileService.Load();
            FanAgentHealth? fanHealth = FanProfileService.ReadHealthStatus();
            token.ThrowIfCancellationRequested();
            AiSystemRuntimeConfig config = AiSystemResourceManagerService.LoadConfig();
            AiSystemRuntimeHealth? systemHealth = AiSystemResourceManagerService.ReadHealth();
            token.ThrowIfCancellationRequested();
            return (Profile: profile, FanHealth: fanHealth, Config: config,
                Health: systemHealth);
        }, token);
        var externalGroup = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            GoogleDriveAuthStatus google = GoogleDriveOAuthService.ReadStatus();
            AiCloudProfileStatus cloud = AiCloudProfileService.ReadStatus(google);
            token.ThrowIfCancellationRequested();
            AdaptivePowerRuntimeStatus? power = AdaptivePowerRuntimeService.ReadStatus();
            token.ThrowIfCancellationRequested();
            return (Cloud: cloud, Auth: google, Power: power);
        }, token);
        await Task.WhenAll(systemGroup, externalGroup).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var a = await systemGroup.ConfigureAwait(false);
        var b = await externalGroup.ConfigureAwait(false);
        return new(a.Profile, a.FanHealth, a.Config, a.Health,
            b.Cloud, b.Auth, b.Power);
    }

    private async Task LoadAiRuntimeBodyDeferredAsync(
        StackPanel host, int generation, CancellationToken token)
    {
        try
        {
            // Profile IO and first-use JSON/JIT work must never occupy the dispatcher.
            AiRuntimePageSnapshot snapshot =
                await ReadAiRuntimePageSnapshotAsync(token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime")
                return;

            host.Children.Clear();
            await RenderAiRuntimeBodyAsync(host, snapshot, generation, token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime") return;
            SchedulePageScrollPolicyRefresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "Runtime status load failed", ex);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "ai-runtime")
                return;
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Text = "Stav AI runtime se nepodařilo načíst.",
                Foreground = WarningBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            host.Children.Add(QuickButton("Načíst znovu", () =>
                LoadAiRuntimeBodyDeferredAsync(host, generation, token)));
        }
    }
}
