using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    internal async Task RunNicUiSmokeAsync()
    {
        await RunNicAbSafetySmokeAsync();
        LogService.Info("NIC_UI_SMOKE begin");
        await ShowSectionAsync("network");
        int expectedTweaks = _tweaks.Count;
        for (int i = 0; i < 40 &&
             (CountClassicTweakCards(_networkTweakHost) < expectedTweaks ||
              (_networkTelemetryShellHost?.Children.Count ?? 0) == 0 ||
              _networkDnsChartHost == null); i++)
            await Task.Delay(25);
        int actualTweaks = CountClassicTweakCards(_networkTweakHost);
        if (actualTweaks < expectedTweaks)
            throw new InvalidOperationException($"Network deferred tweak cards incomplete: expected={expectedTweaks} actual={actualTweaks}.");
        if ((_networkTelemetryShellHost?.Children.Count ?? 0) == 0)
            throw new InvalidOperationException("Network telemetry shell did not complete.");
        if (_networkDnsChartHost == null)
            throw new InvalidOperationException("Network DNS dashboard chart did not materialize.");

        var tweakHost = _networkTweakHost;
        var telemetryHost = _networkTelemetryShellHost;
        var dnsChartHost = _networkDnsChartHost;
        await ShowSectionAsync("settings");
        await ShowSectionAsync("network");
        if (!ReferenceEquals(_networkTweakHost, tweakHost) ||
            !ReferenceEquals(_networkTelemetryShellHost, telemetryHost) ||
            !ReferenceEquals(_networkDnsChartHost, dnsChartHost))
            throw new InvalidOperationException("Network cached dashboard hosts were rebuilt instead of reattached.");
        if (CountClassicTweakCards(_networkTweakHost) < expectedTweaks ||
            (_networkTelemetryShellHost?.Children.Count ?? 0) == 0 ||
            _networkDnsChartHost == null)
            throw new InvalidOperationException("Network cached dashboard content was not preserved after reattach.");

        await Task.Delay(100);
        int overlaysBefore = RootGrid.Children.Count;
        // This is an interface lifecycle gate; never tune/restart the user's live NIC.
        int code = await RunNicProgressPopupAsync(true, async token =>
        {
            await Task.Delay(600, token);
            return "NIC_UI_SMOKE_BACKEND_OK";
        });
        if (RootGrid.Children.Count != overlaysBefore)
            throw new InvalidOperationException("NIC UI smoke leaked its progress overlay.");
        if (code != 0) throw new InvalidOperationException("NIC UI smoke failed: " + code);
        LogService.Info("NIC_UI_SMOKE_OK dashboard=true dnsChart=true diagnosticsRemoved=true mutation=false progressLifecycle=true");
    }
}
