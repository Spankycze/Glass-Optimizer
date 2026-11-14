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
    private static string RunAiActionsSnapshotSmoke()
    {
        // Exercise all three Windows component catalogs with synthetic Windows
        // 23H2/26H2 snapshots before the AI activity rendering regression.
        LogService.Info("release-ui-smoke", WindowsComponentCatalogPolicy.RunFunctionalSmoke());
        AiSystemRuntimeItemState[] fixture = Enumerable.Range(0, 96)
            .Select(i => new AiSystemRuntimeItemState(
                i % 3 == 0 ? "Service" : i % 3 == 1 ? "Task" : "Driver",
                $"Fixture-{i:D3}", i % 5 == 0, i % 7 == 0,
                i % 11 == 0 ? "Blocked" : "",
                null, null, null, null, false, 0, "Observed"))
            .ToArray();
        AiSystemActionHistoryEntry[] history = [];
        var actual = BuildAiActionsSnapshot(fixture, history);
        var expected = fixture.OrderByDescending(x => x.Managed || x.Sleeping)
            .ThenByDescending(AiOptionalFeatureSummaryPolicy.CountsAsBlocked)
            .ThenBy(x => x.Kind).ThenBy(x => x.Id).Take(8).ToArray();
        if (actual.ItemCount != fixture.Length ||
            actual.Managed != fixture.Count(x => x.Managed) ||
            actual.Sleeping != fixture.Count(x => x.Sleeping) ||
            actual.Blocked != fixture.Count(x => !string.IsNullOrWhiteSpace(x.BlockedReason)) ||
            actual.BlockBreakdown.Total != actual.Blocked ||
            actual.BlockBreakdown.Other != fixture.Count(x => !string.IsNullOrWhiteSpace(x.BlockedReason)) ||
            actual.BlockBreakdown.Unavailable != 0 ||
            actual.BlockBreakdown.Protected != 0 ||
            !expected.SequenceEqual(actual.VisibleItems) ||
            !fixture.Select(x => x.Id).SequenceEqual(Enumerable.Range(0, 96).Select(i => $"Fixture-{i:D3}")))
            throw new InvalidOperationException("AI Actions order/count/source immutability regression.");

        // Windows 26H2 must not treat discontinued AllJoyn as a mysteriously
        // removed user service; similarly don't pretend a legacy optional
        // feature like Fax is mandatory, nor overstate why Diagnostics Hub
        // is absent. These explanations are read-only and version-aware.
        if (!ExplainMissingWindowsComponent("service", "AJRouter", 26300).Contains("24H2") ||
            ExplainMissingWindowsComponent("service", "AJRouter", 22621).Contains("24H2") ||
            !ExplainMissingWindowsComponent("service", "Fax", 26300).Contains("volitelná") ||
            !ExplainMissingWindowsComponent("service", "SharedRealitySvc", 26300).Contains("Mixed Reality") ||
            !ExplainMissingWindowsComponent("service", "WMPNetworkSvc", 26300).Contains("Media Playeru") ||
            !ExplainMissingWindowsComponent("service", "diagnosticshub.standardcollector.service", 26300).Contains("nelze potvrdit") ||
            !ExplainMissingWindowsComponent("driver", "parvdm", 26300).Contains("není to důkaz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI missing Windows components explanations are inaccurate.");

        // Preserve all absent/unknown items in the inventory, without
        // misreporting missing optional resources as failed AI changes.
        // Real owned items must lead the eight visible cards.
        AiSystemRuntimeItemState[] missingFixture = Enumerable.Range(0, 24)
            .Select(i => new AiSystemRuntimeItemState("service", $"Missing-{i:D2}",
                false, false, i % 2 == 0 ? "not installed" : "not installed/unreadable",
                null, null, null, null, false, 0, "none"))
            .Append(new AiSystemRuntimeItemState("service", "ProtectedReal",
                false, false, "critical denylist",
                null, null, null, null, false, 0, "none"))
            .Append(new AiSystemRuntimeItemState("driver", "MissingOwned",
                true, true, "not installed",
                null, null, 3, null, false, 0, "managed"))
            .ToArray();
        var absent = BuildAiActionsSnapshot(missingFixture, history);
        if (absent.Blocked != 2 || absent.BlockBreakdown.Unavailable != 12 ||
            absent.BlockBreakdown.Unreadable != 12 ||
            absent.BlockBreakdown.Protected != 2 || absent.BlockBreakdown.Other != 0 ||
            absent.ItemCount != 26 || absent.VisibleItems.Length != 8 ||
            absent.VisibleItems[0].Id != "MissingOwned" ||
            absent.VisibleItems[1].Id != "ProtectedReal" ||
            !absent.VisibleItems.Skip(2).All(x => x.Id.StartsWith("Missing-", StringComparison.Ordinal)) ||
            missingFixture.Length != 26 ||
            AiRuntimeBlockedSummaryPolicy.Classify("not installed/unreadable") !=
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable ||
            AiRuntimeBlockedSummaryPolicy.Classify("task status unreadable") !=
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable ||
            AiRuntimeBlockedSummaryPolicy.Classify("service status unreadable") !=
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable ||
            AiOptionalFeatureSummaryPolicy.CountsAsBlocked(missingFixture[0]) ||
            AiOptionalFeatureSummaryPolicy.CountsAsBlocked(missingFixture[1]) ||
            !AiOptionalFeatureSummaryPolicy.CountsAsBlocked(missingFixture[^1]) ||
            !TaskSchedulerComService.IsMissingTaskError(unchecked((int)0x80070002)) ||
            !TaskSchedulerComService.IsMissingTaskError(unchecked((int)0x8004130F)) ||
            TaskSchedulerComService.IsMissingTaskError(unchecked((int)0x80070005)))
            throw new InvalidOperationException(
                "AI activity miscounted absent components as blockers or hid a real managed resource.");

        // Read-only optional-feature observations can greatly outnumber
        // actual vetoes. The limited panel must show real blocked candidates
        // first, and never mislabel passive inventory as attempted actions.
        var passiveInventory = Enumerable.Range(0, 32).Select(i =>
            new AiSystemRuntimeItemState("service", $"Passive-{i:D2}",
                false, false, "Sledování [Wi-Fi]: stav není ověřen",
                null, null, null, null, false, 0, "none",
                FeatureObservationSamples: 4))
            .Append(new AiSystemRuntimeItemState("service", "ProtectedImportant",
                false, false, "critical denylist",
                null, null, null, null, false, 0, "none"))
            .ToArray();
        var ranked = BuildAiActionsSnapshot(passiveInventory, history);
        if (ranked.Blocked != 1 || ranked.BlockBreakdown.Protected != 1 ||
            ranked.BlockBreakdown.Unavailable != 0 ||
            ranked.BlockBreakdown.Other != 0 ||
            ranked.VisibleItems[0].Id != "ProtectedImportant" ||
            !AiOptionalFeatureSummaryPolicy.IsPassiveObservation(ranked.VisibleItems[1]))
            throw new InvalidOperationException(
                "AI Actions passive observation counted as blocker or hid a real safety veto.");

        // Two owned, stopped Windows resources needing MANUAL restoration
        // cannot disappear behind a crowded 8-row preview, nor be presented
        // as safely stopped or restored. All fixtures remain in memory only.
        const string manualCz = "cesta spravované služby se změnila; ruční obnova nutná, AI záznam zachován";
        const string manualEn = "missing safe original start value; manual recovery required";
        AiSystemRuntimeItemState[] recoveryFixture = Enumerable.Range(0, 24)
            .Select(i => new AiSystemRuntimeItemState(
                "service", $"Owned-{i:D2}", true, i % 2 == 0,
                string.Empty, null, null, 3, null, false, 0, "managed"))
            .Append(new AiSystemRuntimeItemState(
                "service", "RestoreService", true, true, manualCz,
                null, null, 3, null, false, 0, "manual"))
            .Append(new AiSystemRuntimeItemState(
                "driver", "RestoreDriver", true, true, manualEn,
                null, null, 3, null, false, 0, "manual"))
            .ToArray();
        var recoveries = BuildAiActionsSnapshot(recoveryFixture, history);
        if (recoveries.ManualRecoveryRequired != 2 ||
            recoveries.BlockBreakdown.Protected != 2 ||
            recoveries.Blocked != 2 ||
            recoveries.VisibleItems.Length != 8 ||
            recoveries.VisibleItems[0].Id != "RestoreDriver" ||
            recoveries.VisibleItems[1].Id != "RestoreService" ||
            !AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(
                recoveries.VisibleItems[0].BlockedReason) ||
            !AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(
                recoveries.VisibleItems[1].BlockedReason) ||
            AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery("not installed") ||
            recoveryFixture.Length != 26)
            throw new InvalidOperationException(
                "AI manual-recovery warning was hidden, miscounted or mistaken for a routine stop.");

        static int Legacy(AiSystemRuntimeItemState[] items)
        {
            var all = items.ToArray();
            var visible = all.OrderByDescending(x => x.Managed || x.Sleeping)
                .ThenBy(x => x.Kind).ThenBy(x => x.Id).Take(24).ToArray();
            return all.Count(x => x.Managed) + all.Count(x => x.Sleeping) +
                all.Count(x => !string.IsNullOrWhiteSpace(x.BlockedReason)) +
                Math.Min(8, visible.Length);
        }
        _ = Legacy(fixture);
        _ = BuildAiActionsSnapshot(fixture, history);
        const int iterations = 1024;
        long oldBytes = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int oldChecksum = 0;
        for (int i = 0; i < iterations; i++) oldChecksum += Legacy(fixture);
        double oldMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        oldBytes = GC.GetAllocatedBytesForCurrentThread() - oldBytes;

        long newBytes = GC.GetAllocatedBytesForCurrentThread();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        int newChecksum = 0;
        for (int i = 0; i < iterations; i++)
        {
            var snapshot = BuildAiActionsSnapshot(fixture, history);
            newChecksum += snapshot.Managed + snapshot.Sleeping + snapshot.Blocked + snapshot.VisibleItems.Length;
        }
        double newMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        newBytes = GC.GetAllocatedBytesForCurrentThread() - newBytes;
        if (oldChecksum != newChecksum || newBytes >= oldBytes)
            throw new InvalidOperationException(
                $"AI Actions data preparation regression oldChecksum={oldChecksum} newChecksum={newChecksum} oldAllocB={oldBytes} newAllocB={newBytes}.");

        return $"AI_ACTIONS_SNAPSHOT_BENCH_OK rows={fixture.Length} iterations={iterations} " +
            $"oldMs={oldMs:F3} newMs={newMs:F3} oldAllocB={oldBytes} newAllocB={newBytes} " +
            "orderIdentical=true countsIdentical=true missingInventoryVisible=true " +
            "taskReadFailureDistinguished=true serviceReadFailureDistinguished=true " +
            "missingOwnedRetained=true sourceUnchanged=true writes=0";
    }
    private async Task LoadAiRuntimeActionsDeferredAsync(
        Grid host,
        int generation,
        CancellationToken token)
    {
        try
        {
        var loaded = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return (
                Actions: BuildAiActionsSnapshot(
                    AiSystemResourceManagerService.ReadItems(),
                    AiSystemResourceManagerService.ReadActionHistory()),
                Feedback: AiThirdPartyIdleAutomationService.ReadFeedbackForUi());
        }, token);
        var snapshot = loaded.Actions;

        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "ai-runtime")
            return;

        var actions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());

        Border shell = BuildAiRuntimeFlatCard(
            "Aktivita AI",
            $"Spravované {snapshot.Managed} • pozastavené {snapshot.Sleeping} • blokované {snapshot.Blocked}" +
            (snapshot.BlockBreakdown.Unavailable > 0
                ? $" • nenalezené {snapshot.BlockBreakdown.Unavailable}" : string.Empty) +
            (snapshot.BlockBreakdown.Unreadable > 0
                ? $" • stav neověřen {snapshot.BlockBreakdown.Unreadable}" : string.Empty) +
            (snapshot.ManualRecoveryRequired > 0
                ? $" • RUČNÍ OBNOVA {snapshot.ManualRecoveryRequired}" : string.Empty),
            actions,
            expanded: true);

        host.Children.Clear();
        host.Children.Add(shell);

        if (snapshot.VisibleItems.Length == 0)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            actions.Children.Add(new TextBlock
            {
                Text = "AI zatím neprovedla ani nezablokovala žádný zásah.",
                FontSize = 12,
                Foreground = TextSecondaryBrush()
            });
        }

        const int batchSize = 4;
        int actionIndex = 0;

        // Compact activity preview: totals still cover the full inventory;
        // rendering dozens of full cards inflated the entire always-open page.
        foreach (AiSystemRuntimeItemState item in snapshot.VisibleItems)
        {
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime")
                return;

            string status = AiRuntimeBlockedSummaryPolicy.RequiresManualRecovery(item.BlockedReason)
                ? "RUČNÍ OBNOVA"
                : item.Sleeping
                    ? "ZASTAVENO"
                : AiOptionalFeatureSummaryPolicy.IsPassiveObservation(item)
                    ? "SLEDOVÁNO"
                : AiRuntimeBlockedSummaryPolicy.Classify(item.BlockedReason) switch
                {
                    AiRuntimeBlockedSummaryPolicy.Category.Unavailable => "NENÍ REGISTROVÁNO",
                    AiRuntimeBlockedSummaryPolicy.Category.Unreadable => "NEOVĚŘENO",
                    AiRuntimeBlockedSummaryPolicy.Category.Protected => "CHRÁNĚNÉ",
                    AiRuntimeBlockedSummaryPolicy.Category.Other => "BLOKOVÁNO",
                    _ => item.Managed ? "SPRAVOVÁNO" : "SLEDOVÁNO"
                };
            string detail = AiRuntimeBlockedSummaryPolicy.Classify(item.BlockedReason) switch
            {
                AiRuntimeBlockedSummaryPolicy.Category.Unavailable =>
                    ExplainMissingWindowsComponent(item.Kind, item.Id,
                        Environment.OSVersion.Version.Build),
                AiRuntimeBlockedSummaryPolicy.Category.Unreadable =>
                    "Stav se nepodařilo spolehlivě načíst. Nelze potvrdit, zda je položka přítomná.",
                _ => !string.IsNullOrWhiteSpace(item.BlockedReason)
                    ? item.BlockedReason : item.LastAction
            };
            if (item.LastChangedUtc is DateTime changed)
                detail += $" • {changed.ToLocalTime():g}";
            if (actionIndex % 2 == 0)
                actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border actionCard = BuildRuntimeScopeRow(
                $"{item.Kind.ToUpperInvariant()} • {item.Id}",
                string.IsNullOrWhiteSpace(detail) ? "Bez zásahu." : detail,
                status);
            Grid.SetColumn(actionCard, actionIndex % 2);
            Grid.SetRow(actionCard, actionIndex / 2);
            actions.Children.Add(actionCard);
            actionIndex++;

            if (actionIndex % batchSize == 0)
                await Task.Yield();
        }

        token.ThrowIfCancellationRequested();
        if (generation != _navigationGeneration || _section != "ai-runtime")
            return;

        if (snapshot.ItemCount > snapshot.VisibleItems.Length)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var more = new TextBlock
            {
                Text = $"Zobrazeno 8 nejdůležitějších z {snapshot.ItemCount} sledovaných položek.",
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetColumnSpan(more, 2);
            Grid.SetRow(more, actions.RowDefinitions.Count - 1);
            actions.Children.Add(more);
        }

        // Honest live automatic-program outcomes, read from the existing
        // atomic ownership journal ONCE per page navigation. Display only
        // the latest two lines so Dark Glass stays compact and scroll-free.
        if (loaded.Feedback.Length > 0)
        {
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var feedbackLabel = new TextBlock
            {
                Text = "AI programy • poslední výsledky (neprokazují příčinu): " +
                    string.Join("  |  ", loaded.Feedback.Take(2).Select(x =>
                        $"{x.AppKey}: {x.Detail}")),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetColumnSpan(feedbackLabel, 2);
            Grid.SetRow(feedbackLabel, actions.RowDefinitions.Count - 1);
            actions.Children.Add(feedbackLabel);
        }

        if (snapshot.History.Length > 0)
        {
            AiSystemActionHistoryEntry latest = snapshot.History[^1];
            actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var latestText = new TextBlock
            {
                Text = $"Poslední zásah: {latest.Kind.ToUpperInvariant()} • {latest.Id} • {latest.Action} • {latest.TimestampUtc.ToLocalTime():g}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = TextSecondaryBrush()
            };
            Grid.SetColumnSpan(latestText, 2);
            Grid.SetRow(latestText, actions.RowDefinitions.Count - 1);
            actions.Children.Add(latestText);
        }

        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "AI Actions snapshot or render failed", ex);
            if (token.IsCancellationRequested ||
                generation != _navigationGeneration || _section != "ai-runtime")
                return;
            host.Children.Clear();
            var error = new StackPanel { Spacing = 8 };
            error.Children.Add(new TextBlock
            {
                Text = "Akce AI se nepodařilo načíst. Ostatní AI Runtime zůstává funkční.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = WarningBrush()
            });
            error.Children.Add(QuickButton("Zkusit znovu", () =>
                LoadAiRuntimeActionsDeferredAsync(host, generation, token)));
            host.Children.Add(error);
        }
    }

    private static async Task SaveAiRuntimeConfigVerified(
        Func<AiSystemRuntimeConfig, AiSystemRuntimeConfig> update,
        Func<AiSystemRuntimeConfig, bool> readBack,
        bool requested,
        bool restartAgent = false)
    {
        await AiScopeSaveGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                AiSystemRuntimeConfig? original = null;
                AiSystemRuntimeConfig actualConfig = AiSystemResourceManagerService.UpdateConfig(current =>
                {
                    original = current;
                    return update(current);
                });
                bool actual = readBack(actualConfig);
                if (actual != requested)
                    throw new InvalidOperationException(
                        $"AI Runtime config read-back nesouhlasí. Požadováno={requested}, skutečnost={actual}.");
                if (actualConfig == original)
                    return; // Idempotent: neither JSON rewrite nor agent startup.
                if (original!.AutomaticProcessPriorityEnabled &&
                    !actualConfig.AutomaticProcessPriorityEnabled)
                    AiSystemResourceManagerService.RestoreProcessPriorityNow();
                if (restartAgent) FanProfileService.StartAgentIfNeeded();
            });
        }
        finally { AiScopeSaveGate.Release(); }
    }

    // No collapsible panels anywhere on the AI Runtime page. Flat headings,
    // slim padding and always-visible content match the modern Windows toggles.
    private Border BuildAiRuntimeFlatCard(string title, string subtitle,
        UIElement content, bool expanded = false)
    {
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            body.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush()
            });
        body.Children.Add(content);
        return BuildDashboardCard(body, new Thickness(12, 10, 12, 10));
    }

    private Border BuildRuntimeToggleRow(
        string title,
        string description,
        bool initialValue,
        Func<bool, Task> onChanged)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = TextSecondaryBrush()
        });
        grid.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            IsOn = initialValue,
            OnContent = "AUTO",
            OffContent = "OFF",
            VerticalAlignment = VerticalAlignment.Center
        };
        NormalizeToggleAppearance(toggle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, title);
        bool suppressToggle = false;
        bool busy = false;
        bool lastAccepted = initialValue;
        toggle.Toggled += (_, _) =>
        {
            if (suppressToggle) return;
            if (busy)
            {
                suppressToggle = true;
                try { toggle.IsOn = lastAccepted; }
                finally { suppressToggle = false; }
                return;
            }

            bool requested = toggle.IsOn;
            SafeTaskService.Forget(ApplyAsync(), "ai-runtime", "Verified runtime toggle " + title);

            async Task ApplyAsync()
            {
                busy = true;
                toggle.IsEnabled = false;
                try
                {
                    await onChanged(requested);
                    lastAccepted = requested;
                    LogService.Info("ai-runtime", $"RUNTIME_TOGGLE_OK title={title} requested={requested}");
                }
                catch (Exception ex)
                {
                    suppressToggle = true;
                    try { toggle.IsOn = lastAccepted; }
                    finally { suppressToggle = false; }
                    LogService.Error("ai-runtime", $"RUNTIME_TOGGLE_FAIL title={title} requested={requested}", ex);
                    try
                    {
                        await ShowMessageAsync(title, ex.Message);
                    }
                    finally
                    {
                        // The write may have succeeded before a restore/agent failure.
                        // Reload persisted state even if the error dialog itself fails.
                        if (_section == "ai-runtime")
                            await RefreshAiRuntimePageAsync();
                    }
                }
                finally
                {
                    toggle.IsEnabled = true;
                    busy = false;
                }
            }
        };
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = null,
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(4, 7, 4, 7),
            Child = grid
        };
    }

    private Border BuildRuntimeScopeRow(string title, string description, string badgeText)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        grid.Children.Add(text);

        var badge = Badge(badgeText, badgeText == "GUARDED" ? "SpankyWarningBrush" : "SpankySuccessBrush");
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        return new Border
        {
            Background = SectionCardBackgroundBrush(),
            BorderBrush = SectionBorderBrush(),
            BorderThickness = new Thickness(_darkTheme ? 1.15 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid
        };
    }
}
