using System;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    public async Task RunDriversUiSmokeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ShowSectionAsync("drivers");

        if (_lastNavigationErrorId != null)
            throw new InvalidOperationException("Drivers -> AI Runtime navigation error: " + _lastNavigationErrorId);
        if (!string.Equals(_section, "ai-runtime", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Legacy Drivers route did not redirect to AI System Runtime.");
        if (ContentStack.Children.Count == 0)
            throw new InvalidOperationException("AI System Runtime rendered no content for legacy Drivers route.");

        AiSystemRuntimeConfig cfg = AiSystemResourceManagerService.LoadConfig();
        if (!cfg.OptionalDevicesEnabled)
            LogService.Info("drivers", "DRIVERS_AI_RUNTIME_REDIRECT_OK optionalDevices=off");
        else
            LogService.Info("drivers", "DRIVERS_AI_RUNTIME_REDIRECT_OK optionalDevices=on");

        LogService.Info("drivers", $"DRIVERS_UI_SMOKE_OK replacedBy=ai-runtime ms={sw.ElapsedMilliseconds}");
    }
}
