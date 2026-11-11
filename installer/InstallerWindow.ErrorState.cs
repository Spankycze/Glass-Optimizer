using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpankyhoOptimizer.ModernSetup;

internal sealed partial class InstallerWindow
{
    private int _attemptGeneration;
    private bool _windowClosed;
    private bool _errorAllowsRetry;

    private void InitializeErrorSurface()
    {
        Find<Button>("ErrorClose").Click += (_, _) => DismissError();
        Find<Button>("ErrorDismiss").Click += (_, _) => DismissError();
        Find<Button>("ErrorRetry").Click += async (_, _) => { if (_errorAllowsRetry && !_busy) await RunInstallAsync(); };
        Find<Button>("ErrorLog").Click += (_, _) => OpenInstallerLog();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Find<Grid>("ErrorOverlay").Visibility == Visibility.Visible)
            { DismissError(); e.Handled = true; }
        };
        Closed += (_, _) => { _windowClosed = true; _attemptGeneration++; };
    }

    private void SetHeader(string title, string detail, string badge)
    {
        Find<TextBlock>("InstallHeading").Text = title;
        Find<TextBlock>("InstallDescription").Text = detail;
        Find<TextBlock>("InstallBadge").Text = badge;
    }

    private void SetInstallationBusy(bool busy)
    {
        _busy = busy;
        _cancelButton.IsEnabled = !busy;
        _close.IsEnabled = !busy;
        _browse.IsEnabled = !busy;
        _path.IsEnabled = !busy;
        _desktop.IsEnabled = !busy;
        _launch.IsEnabled = !busy;
        _install.IsEnabled = !busy;
    }

    // The optional operation is an in-process test seam. Normal UI calls always use
    // the existing transactional installer; no release command can inject operations.
    private async Task RunInstallAsync(Func<string, bool, Action<int, string>, Task>? operation = null)
    {
        if (_busy || _windowClosed) return;
        int attempt = ++_attemptGeneration;
        string target = string.Empty;
        DismissError();
        _installedTarget = null;
        _finalActions.Visibility = Visibility.Collapsed;
        _install.Visibility = Visibility.Visible;
        try
        {
            target = Path.GetFullPath(_path.Text.Trim()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(target), "Spankyho Optimizer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cílová složka musí končit názvem ‚Spankyho Optimizer‘.");
            SetInstallationBusy(true);
            _install.Content = "Instaluji…";
            SetHeader("Instaluji Glass Optimizer", "Počkej na dokončení instalace.", "PROBÍHÁ");
            _footerHint.Text = "Probíhá instalace…";
            SetStepState(1);
            SetProgress(3, "PŘÍPRAVA", "Kontroluji potřebné součásti…");
            AppendLiveLog("Instalace spuštěna do: " + target);
            if (operation == null) await EnsurePrerequisites();
            bool desktop = _desktop.IsChecked == true;
            SetProgress(52, "INSTALACE", "Připravuji novou verzi…");
            // The extraction callback can fire thousands of times on a worker.
            // Do not enqueue one UI action and one file log for each TAR entry:
            // the existing SetProgress coalescer should be the only UI dispatch.
            // Preserve useful setup.log milestones at most once per 5% interval.
            int lastLoggedBucket = 10; // The preceding reported state was 52%.
            void Report(int percent, string text)
            {
                if (attempt != System.Threading.Volatile.Read(ref _attemptGeneration))
                    return;
                SetInstallProgress(percent, "INSTALACE", text, attempt);

                int bucket = Math.Clamp(percent, 0, 99) / 5;
                int previous;
                while ((previous = System.Threading.Volatile.Read(ref lastLoggedBucket)) < bucket)
                {
                    if (System.Threading.Interlocked.CompareExchange(
                            ref lastLoggedBucket, bucket, previous) == previous)
                    {
                        Program.Log($"INSTALL_PROGRESS {percent}% {text}");
                        break;
                    }
                }
            }
            if (operation != null) await operation(target, desktop, Report);
            else await Task.Run(() => Program.InstallPayloadTransactional(target, desktop, Report));
            CompleteInstall(target);
        }
        catch (Exception ex)
        {
            Program.Log("INSTALL ERROR " + ex);
            AppendLiveLog("CHYBA: " + ex.Message);
            _attemptGeneration++; // Reject queued or retained callbacks from this attempt.
            SetInstallationBusy(false);
            SetStepState(0);
            _install.Content = "Zkusit znovu";
            SetHeader("Instalace se nezdařila", "Příčinu a další postup najdeš níže.", "CHYBA");
            _footerHint.Text = "Instalace nebyla dokončena. Můžeš ji opakovat.";
            SetProgress(0, "CHYBA", "Instalace nebyla dokončena.");
            Environment.ExitCode = 1;
            ShowError(ex, target, true);
        }
    }

    private void CompleteInstall(string target)
    {
        SetProgress(100, "HOTOVO", $"Glass Optimizer V{Program.DisplayVersion} je připraven.");
        _installedTarget = target;
        _attemptGeneration++;
        SetInstallationBusy(false);
        Environment.ExitCode = 0;
        SetStepState(2);
        SetHeader("Instalace je hotová", $"Glass Optimizer V{Program.DisplayVersion} je připraven ke spuštění.", "HOTOVO");
        _install.Visibility = Visibility.Collapsed;
        _cancelButton.Visibility = Visibility.Collapsed;
        _finalActions.Visibility = Visibility.Visible;
        _footerHint.Text = "Instalace dokončena.";
        Program.Log("INSTALL OK");
        AppendLiveLog("Instalace úspěšně dokončena.");
        if (_launch.IsChecked == true && TryLaunchInstalledApp()) Close();
    }

    private static (string Title, string Guidance) DescribeError(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            int code = current.HResult & 0xFFFF;
            if (current is UnauthorizedAccessException || code == 5)
                return ("Přístup do složky byl odepřen", "Ověř oprávnění k cílové složce, případně vyber jinou. Potom instalaci opakuj.");
            if (code is 32 or 33)
                return ("Soubory právě používá jiný program", "Zavři program, který tyto soubory používá. Potom můžeš instalaci opakovat.");
            if (code is 39 or 112)
                return ("Na disku není dost místa", "Uvolni místo na cílovém disku nebo vyber jinou složku. Potom instalaci opakuj.");
            if (current is HttpRequestException)
                return ("Stažení se nezdařilo", "Zkontroluj připojení k internetu a zkus to znovu. Podrobnosti jsou v protokolu instalace.");
            if (current is TimeoutException)
                return ("Operace trvala příliš dlouho", "Zkontroluj připojení a dostupnost cílové složky. Potom instalaci opakuj.");
        }
        return ("Instalaci se nepodařilo dokončit", "Zkontroluj zadanou cestu a podrobnosti chyby. Pak můžeš upravit nastavení nebo instalaci zopakovat.");
    }

    private void ShowError(Exception error, string target, bool allowRetry)
    {
        _errorAllowsRetry = allowRetry;
        var copy = DescribeError(error);
        Find<TextBlock>("ErrorHeading").Text = allowRetry ? copy.Title : "Aplikaci se nepodařilo spustit";
        Find<TextBlock>("ErrorGuidance").Text = allowRetry ? copy.Guidance : "Instalace je hotová. Zkus aplikaci spustit znovu nebo otevři její složku.";
        Find<TextBlock>("ErrorSummary").Text = error.GetBaseException().Message;
        Find<TextBox>("ErrorDetails").Text = (string.IsNullOrWhiteSpace(target) ? "" : "Cílová složka: " + target + Environment.NewLine) + error;
        Find<Expander>("ErrorMore").IsExpanded = false;
        Find<Button>("ErrorRetry").Visibility = allowRetry ? Visibility.Visible : Visibility.Collapsed;
        Find<Grid>("ErrorOverlay").Visibility = Visibility.Visible;
        Find<Grid>("InstallerContent").IsHitTestVisible = false;
        Keyboard.Focus(Find<Button>("ErrorDismiss"));
    }

    private void DismissError()
    {
        Find<Grid>("ErrorOverlay").Visibility = Visibility.Collapsed;
        Find<Grid>("InstallerContent").IsHitTestVisible = true;
        if (!_busy && !_windowClosed) Keyboard.Focus(_installedTarget == null ? _install : _runButton);
    }

    private void OpenInstallerLog()
    {
        try
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpankyhoOptimizer", "setup.log");
            if (!File.Exists(log)) throw new FileNotFoundException("Protokol instalace zatím nebyl vytvořen.", log);
            using Process? process = Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
            if (process == null) throw new InvalidOperationException("Protokol se nepodařilo otevřít.");
        }
        catch (Exception ex) { Find<TextBlock>("ErrorSummary").Text = "Protokol se nepodařilo otevřít: " + ex.Message; }
    }
}
