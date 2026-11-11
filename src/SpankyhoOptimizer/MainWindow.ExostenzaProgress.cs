using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class MainWindow
{
    private async Task RunExostenzaInstallPopupAsync(bool dryRun = false)
    {
        using var cts = new CancellationTokenSource();
        var overlay = CreateDialogOverlay(12500);
        var card = CreateDialogCard(600, 680, z: 36);
        var root = new StackPanel { Spacing = 16 };
        var head = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 0, 48, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(14),
            Background = AccentSoftBrush(),
            Child = SidebarIcon("gpu", 22)
        }); var titles = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Exostenza Edition", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush() });
        titles.Children.Add(new TextBlock { Text = "Community NVIDIA driver — one-click instalace", FontSize = 12, Foreground = TextSecondaryBrush() });
        Grid.SetColumn(titles, 1); head.Children.Add(titles); root.Children.Add(head);
        root.Children.Add(new Border
        {
            Background = DialogInnerSurfaceBrush(),
            BorderBrush = UiBrush("SpankyWarningBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Child = new TextBlock
            {
                Text = "Neoficiální de-bloated balík z Guru3D. Optimizer ho stáhne, rozbalí, vytvoří bod obnovení a spustí setup.exe.",
                Foreground = TextPrimaryBrush(),
                TextWrapping = TextWrapping.Wrap
            }
        });
        var status = new TextBlock { Text = "Kontroluji nejnovější Exostenza verzi…", FontSize = 13.5, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        var percent = new TextBlock { Text = "0 %", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextPrimaryBrush(), HorizontalAlignment = HorizontalAlignment.Right };
        root.Children.Add(status); root.Children.Add(percent);
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = DialogInnerSurfaceBrush(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var fill = new Border { Width = 0, Height = 8, CornerRadius = new CornerRadius(4), Background = ProgressBrush(), HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = fill; root.Children.Add(track);
        var detail = new TextBlock { Text = "", FontSize = 12, Foreground = TextSecondaryBrush(), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(detail);
        bool popupClosed = false;
        void ClosePopup()
        {
            if (popupClosed) return;
            popupClosed = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            UnmountDialog(overlay);
        }

        card.Child = root;
        var topClose = MountDialog(overlay, card, ClosePopup);

        int previousPercent = -1;
        string previousStatus = string.Empty;
        void SetProgress(int value, string text)
        {
            if (popupClosed) return;
            value = Math.Clamp(value, 0, 100);
            if (value != previousPercent)
            {
                double width = track.ActualWidth > 0 ? track.ActualWidth : 520;
                fill.Width = width * value / 100d;
                percent.Text = value + " %";
                previousPercent = value;
            }
            if (!string.Equals(previousStatus, text, StringComparison.Ordinal))
            {
                status.Text = text;
                previousStatus = text;
            }
        }
        try
        {
            if (dryRun)
            {
                SetProgress(12, "Kontroluji nejnovější verzi…"); await Task.Delay(80, cts.Token);
                SetProgress(25, "Hledám přímý download…"); await Task.Delay(80, cts.Token);
                SetProgress(65, "Stahuji Exostenza…"); await Task.Delay(80, cts.Token);
                SetProgress(82, "Rozbaluji balík…"); await Task.Delay(80, cts.Token);
                SetProgress(92, "Vytvářím bod obnovení…"); await Task.Delay(80, cts.Token);
                SetProgress(100, "Hotovo — instalační tok je připraven."); return;
            }
            string? pendingState = await GpuDriverService.ReconcilePendingExostenzaAsync(cts.Token);
            string? pendingVersion = GpuDriverService.TryGetPendingExostenzaVersion();
            if (!string.IsNullOrWhiteSpace(pendingVersion))
            {
                SetProgress(100, $"Exostenza {pendingVersion} už byla spuštěna.");
                if (!popupClosed)
                    detail.Text = string.IsNullOrWhiteSpace(pendingState)
                        ? "GPU instalátor už běží nebo čekám na potvrzení instalace. Další download ani druhou instanci nespouštím."
                        : pendingState;
                return;
            }

            if (_gpuInfo == null) _gpuInfo = await GpuDriverService.DetectPrimaryGpuAsync();
            cts.Token.ThrowIfCancellationRequested();
            if (!GpuDriverService.IsExostenzaCompatible(_gpuInfo)) throw new InvalidOperationException("Exostenza je dostupná jen pro podporované NVIDIA RTX GPU.");
            _gpuExostenzaRelease = await GpuDriverService.CheckExostenzaAsync();
            cts.Token.ThrowIfCancellationRequested();
            if (_gpuExostenzaRelease == null) throw new InvalidOperationException("Aktuální Exostenza release se nepodařilo zjistit.");
            if (!popupClosed) detail.Text = "Verze: " + _gpuExostenzaRelease.Version;
            // Keep only the package/partial for the release we are about to install.
            // This removes stale Exostenza downloads and extracted older releases automatically.
            GpuDriverService.CleanupExostenzaCache(_gpuExostenzaRelease.Version);
            SetProgress(12, "Hledám přímý download…");
            _gpuExostenzaRelease = await GpuDriverService.ResolveExostenzaDownloadAsync(_gpuExostenzaRelease, cts.Token);
            if (string.IsNullOrWhiteSpace(_gpuExostenzaRelease.DownloadUrl)) throw new InvalidOperationException("Přímý Exostenza download nebyl nalezen.");
            string? cachedPackage = GpuDriverService.TryGetCachedPackage(_gpuExostenzaRelease);
            if (!string.IsNullOrWhiteSpace(cachedPackage))
            {
                SetProgress(78, "Používám už stažený balík Exostenza " + _gpuExostenzaRelease.Version + "…");
                if (!popupClosed) detail.Text = Path.GetFileName(cachedPackage);
            }
            else
            {
                SetProgress(18, "Začínám stahování Exostenza " + _gpuExostenzaRelease.Version + "…");
            }
            var progress = new Progress<DriverDownloadProgress>(p =>
            {
                if (popupClosed) return;
                int downloadPercent = p.Percent < 0 ? 18 : 18 + (int)Math.Round(p.Percent * 0.62);
                SetProgress(downloadPercent, p.Percent >= 0 ? $"Stahuji Exostenza {_gpuExostenzaRelease.Version}… {p.Percent} %" : "Stahuji Exostenza…");
                if (!popupClosed && p.Total.HasValue)
                    detail.Text = $"{p.Received / 1048576d:0.0} / {p.Total.Value / 1048576d:0.0} MB";
            });
            _gpuDownloadedPackage = await GpuDriverService.DownloadAsync(_gpuExostenzaRelease, progress, cts.Token);
            SetProgress(82, "Rozbaluji driver balík…");
            if (!popupClosed) detail.Text = Path.GetFileName(_gpuDownloadedPackage);
            string installer = await GpuDriverService.ExtractArchiveAndFindSetupAsync(_gpuDownloadedPackage, cts.Token);
            SetProgress(92, "Vytvářím bod obnovení…");
            await WindowsActions.CreateRestorePointAsync("Before Exostenza GPU driver");
            cts.Token.ThrowIfCancellationRequested();
            SetProgress(95, "Ověřuji instalační soubor…");
            var security = await FileSecurityService.InspectAsync(installer);
            cts.Token.ThrowIfCancellationRequested();
            LogService.Info("drivers", $"Exostenza setup sha256={security.Sha256} status={security.SignatureStatus} signer={security.Signer}");
            if (!security.IsTrustedSignature) throw new InvalidOperationException("Setup uvnitř Exostenza balíku nemá platný digitální podpis.");
            SetProgress(98, "Spouštím NVIDIA instalátor…");
            if (!popupClosed) detail.Text = Path.GetFileName(installer);
            cts.Token.ThrowIfCancellationRequested();
            GpuDriverService.LaunchInstaller(installer);
            SetProgress(100, "Instalátor Exostenza byl spuštěn. Dočasné soubory budou odstraněny po dokončení instalace.");
            string installedVersion = _gpuExostenzaRelease.Version;
            // Persist cleanup intent so closing Optimizer does not cancel cleanup forever.
            // The next app start removes the archive/extracted payload safely.
            GpuDriverService.MarkExostenzaCleanupPending(installedVersion);
            SafeTaskService.Forget(
                GpuDriverService.MonitorPendingExostenzaAsync(installedVersion),
                "drivers",
                "Exostenza install read-back monitor");
            await Task.Delay(700, cts.Token);
            if (!popupClosed) UnmountDialog(overlay);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            LogService.Info("drivers", "Exostenza install popup cancelled by user.");
        }
        catch (Exception ex)
        {
            LogService.Error("Exostenza one-click install failed", ex);
            if (!popupClosed)
            {
                status.Text = "Instalace Exostenza selhala.";
                detail.Text = ex.Message;
                percent.Text = "CHYBA";
            }
        }
    }
}
public sealed partial class MainWindow
{
    public async Task RunExostenzaUiSmokeAsync()
    {
        await RunExostenzaInstallPopupAsync(true);
        await Task.Delay(250);
    }
}
