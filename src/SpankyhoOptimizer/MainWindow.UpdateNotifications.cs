using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private readonly List<DesktopUpdateNotificationWindow> _desktopUpdateNotifications = new();

    private async Task CheckDesktopUpdateNotificationsAsync()
    {
        if (Environment.GetCommandLineArgs().Any(x =>
                x.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("benchmark", StringComparison.OrdinalIgnoreCase)))
            return;

        CancellationToken windowToken = _windowLifetimeCts.Token;
        try
        {
            await Task.Delay(2200, windowToken);

            int stackIndex = 0;
            AppUpdateCheck appUpdate = await AppUpdateService.CheckAsync(windowToken);
            windowToken.ThrowIfCancellationRequested();
            if (appUpdate.Available && appUpdate.Manifest != null)
            {
                ShowAppUpdateDesktopNotification(appUpdate.Manifest, stackIndex++);
            }

            await Task.Delay(350, windowToken);

            GpuDriverInfo? gpu = await GpuDriverService.DetectPrimaryGpuAsync(windowToken);
            if (!GpuDriverService.IsExostenzaCompatible(gpu))
                return;

            DriverReleaseInfo? exostenza = await GpuDriverService.CheckExostenzaAsync(windowToken);
            windowToken.ThrowIfCancellationRequested();
            if (exostenza == null ||
                string.IsNullOrWhiteSpace(exostenza.Version) ||
                exostenza.Version == "?" ||
                gpu == null ||
                !GpuDriverService.IsNewer(exostenza.Version, gpu.DisplayVersion))
                return;

            ShowExostenzaDesktopNotification(gpu, exostenza, stackIndex);
        }
        catch (OperationCanceledException) when (windowToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("update", "Desktop update notification check failed", ex);
        }
    }

    private void ShowAppUpdateDesktopNotification(AppUpdateManifest manifest, int stackIndex)
    {
        var window = new DesktopUpdateNotificationWindow(
            "Spanky Update",
            "Glass Optimizer",
            "Nová verze " + manifest.Version,
            string.IsNullOrWhiteSpace(manifest.Changelog)
                ? "Je dostupná nová verze Optimizeru. Installer se před spuštěním kryptograficky ověří."
                : manifest.Changelog!,
            async (uiProgress, token) =>
            {
                var progress = new Progress<AppUpdateDownloadProgress>(p =>
                {
                    int percent = p.Percent < 0 ? 3 : Math.Clamp(p.Percent, 0, 48);
                    string detail = p.Total.HasValue
                        ? $"{p.Received / 1048576d:0.0} / {p.Total.Value / 1048576d:0.0} MB"
                        : $"{p.Received / 1048576d:0.0} MB";
                    uiProgress.Report(new DesktopUpdateProgress(
                        percent,
                        $"Stahuji Glass Optimizer {RuntimeVersionInfo.PublicVersion(manifest.Version)}…",
                        detail));
                });

                string path = await AppUpdateService.DownloadAsync(manifest, progress, token);
                uiProgress.Report(new DesktopUpdateProgress(50, "Installer je stažený a ověřený.", Path.GetFileName(path)));

                return new DesktopUpdateCompletion(
                    "Aktualizace je připravená k instalaci.",
                    "Nainstalovat",
                     null,
                     "SHA-256 + podpis manifestu ověřen.",
                      (installProgress, installToken) =>
                      {
                          installToken.ThrowIfCancellationRequested();
                          installProgress.Report(new DesktopUpdateProgress(98, "Spouštím tichou instalaci…", Path.GetFileName(path)));
                          AppUpdateService.LaunchInstallerForUpdate(path, manifest.Sha256, silent: true);
                          installProgress.Report(new DesktopUpdateProgress(100, "Installer převzal aktualizaci.", "Aplikace se nyní bezpečně nahradí."));
                          return Task.CompletedTask;
                      });
            },
            stackIndex);

        TrackDesktopUpdateWindow(window);
        window.ShowWithoutActivation();
    }

    private void ShowExostenzaDesktopNotification(GpuDriverInfo gpu, DriverReleaseInfo release, int stackIndex)
    {
        var window = new DesktopUpdateNotificationWindow(
            "GPU Driver",
            "Exostenza Edition",
            $"RTX {release.Version} • aktuálně {gpu.DisplayVersion}",
            "Je dostupná novější community NVIDIA Exostenza verze. Optimizer balík stáhne, rozbalí a ověří setup.",
            async (uiProgress, token) =>
            {
                DriverReleaseInfo resolved = release;
                GpuDriverService.CleanupExostenzaCache(release.Version);
                uiProgress.Report(new DesktopUpdateProgress(6, "Hledám přímý Exostenza download…"));

                resolved = await GpuDriverService.ResolveExostenzaDownloadAsync(resolved, token);
                if (string.IsNullOrWhiteSpace(resolved.DownloadUrl))
                    throw new InvalidOperationException("Přímý Exostenza download nebyl nalezen.");

                string? package = GpuDriverService.TryGetCachedPackage(resolved);
                if (string.IsNullOrWhiteSpace(package))
                {
                    var progress = new Progress<DriverDownloadProgress>(p =>
                    {
                        int sourcePercent = p.Percent < 0 ? 0 : Math.Clamp(p.Percent, 0, 100);
                        int percent = 10 + (int)Math.Round(sourcePercent * 0.65);
                        string detail = p.Total.HasValue
                            ? $"{p.Received / 1048576d:0.0} / {p.Total.Value / 1048576d:0.0} MB"
                            : $"{p.Received / 1048576d:0.0} MB";
                        uiProgress.Report(new DesktopUpdateProgress(
                            percent,
                            $"Stahuji Exostenza {release.Version}…",
                            detail));
                    });
                    package = await GpuDriverService.DownloadAsync(resolved, progress, token);
                }
                else
                {
                    uiProgress.Report(new DesktopUpdateProgress(75, "Používám už stažený Exostenza balík.", Path.GetFileName(package)));
                }

                uiProgress.Report(new DesktopUpdateProgress(82, "Rozbaluji driver balík…", Path.GetFileName(package)));
                string installer = await GpuDriverService.ExtractArchiveAndFindSetupAsync(package, token);

                uiProgress.Report(new DesktopUpdateProgress(91, "Ověřuji NVIDIA setup…", Path.GetFileName(installer)));
                var security = await FileSecurityService.InspectAsync(installer, token);
                token.ThrowIfCancellationRequested();
                if (!security.IsTrustedSignature)
                    throw new InvalidOperationException("Setup uvnitř Exostenza balíku nemá platný digitální podpis.");

                uiProgress.Report(new DesktopUpdateProgress(96, "Připravuji bezpečnou instalaci…", security.Signer));
                await WindowsActions.CreateRestorePointAsync("Before Exostenza GPU driver");
                token.ThrowIfCancellationRequested();

                uiProgress.Report(new DesktopUpdateProgress(100, "Exostenza je připravená.", $"Verze {release.Version}"));

                return new DesktopUpdateCompletion(
                    "Driver je stažený, rozbalený a ověřený.",
                    "Nainstalovat",
                     null,
                     "Před instalací byl vytvořen bod obnovení.",
                     async (installProgress, installToken) =>
                     {
                         installToken.ThrowIfCancellationRequested();
                         installProgress.Report(new DesktopUpdateProgress(97, "Spouštím instalaci driveru…", release.Version));
                         GpuDriverService.LaunchInstaller(installer);
                         GpuDriverService.MarkExostenzaCleanupPending(release.Version);
                         for (int attempt = 0; attempt < 36; attempt++)
                         {
                             installToken.ThrowIfCancellationRequested();
                              if (GpuDriverService.IsKnownGpuInstallerRunning())
                              {
                                  installProgress.Report(new DesktopUpdateProgress(
                                      97, "Instaluji Exostenza driver…", release.Version));
                             }
                             else
                             {
                                 string? state = await GpuDriverService.ReconcilePendingExostenzaAsync(installToken);
                                 if (state?.Contains("nainstalována a ověřena", StringComparison.OrdinalIgnoreCase) == true)
                                 {
                                     installProgress.Report(new DesktopUpdateProgress(100, "Driver je nainstalovaný a ověřený.", release.Version));
                                     return;
                                 }
                                 installProgress.Report(new DesktopUpdateProgress(99, "Ověřuji výsledek instalace driveru…", state ?? release.Version));
                             }
                             await Task.Delay(TimeSpan.FromSeconds(5), installToken);
                         }
                         throw new TimeoutException("Instalaci driveru se nepodařilo včas ověřit.");
                     });
            },
            stackIndex);

        TrackDesktopUpdateWindow(window);
        window.ShowWithoutActivation();
    }

    private void TrackDesktopUpdateWindow(DesktopUpdateNotificationWindow window)
    {
        _desktopUpdateNotifications.Add(window);
        window.Closed += (_, _) => _desktopUpdateNotifications.Remove(window);
    }

    public async Task RunDesktopUpdateNotificationSmokeAsync()
    {
        var window = new DesktopUpdateNotificationWindow(
            "Spanky Update",
            "Desktop update popup",
            "Smoke 15.4",
            "Glass upozornění s reálným průběhem a tlačítky Stáhnout / Později.",
            async (progress, token) =>
            {
                for (int i = 0; i <= 100; i += 20)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Report(new DesktopUpdateProgress(i, $"Testuji progress… {i} %", $"{i * 1.7:0.0} MB"));
                    await Task.Delay(80, token);
                }
                return new DesktopUpdateCompletion("Smoke hotový.", "Zavřít", () => Task.CompletedTask, "Desktop glass updater OK");
            });

        TrackDesktopUpdateWindow(window);
        IntPtr foregroundBefore = GetForegroundWindow();
        window.ShowWithoutActivation();
        await Task.Delay(180);
        IntPtr foregroundAfter = GetForegroundWindow();
        if (foregroundBefore != IntPtr.Zero && foregroundAfter != foregroundBefore)
            throw new InvalidOperationException("Updater popup changed the foreground window.");

        if (UseGlassChrome)
        {
            static void RequireTransparent(Microsoft.UI.Xaml.Media.Brush? brush, string name)
            {
                if (brush is not Microsoft.UI.Xaml.Media.SolidColorBrush solid || solid.Color.A != 0)
                    throw new InvalidOperationException($"{name} became opaque while updater popup was visible.");
            }

            RequireTransparent(RootGrid.Background, "RootGrid");
            RequireTransparent(_pageScroll.Background, "PageScroll");
            RequireTransparent(PageHost.Background, "PageHost");
            if (SystemBackdrop is not OptimizerGlassBackdrop backdrop || !backdrop.IsControllerReady)
                throw new InvalidOperationException("Main Optimizer acrylic controller dropped while updater popup was visible.");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            var inactiveTitle = appWindow.TitleBar.InactiveBackgroundColor;
            var inactiveButton = appWindow.TitleBar.ButtonInactiveBackgroundColor;
            if ((inactiveTitle.HasValue && inactiveTitle.Value.A != 0) ||
                (inactiveButton.HasValue && inactiveButton.Value.A != 0))
                throw new InvalidOperationException("Inactive titlebar became opaque while updater popup was visible.");
        }

        await Task.Delay(470);
        window.Close();
    }
}
