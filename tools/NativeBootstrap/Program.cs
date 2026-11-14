using System;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Threading;

internal static class Program
{
    internal static bool HasDesktop10(string dotnetRoot) => RuntimePresence.HasDesktop10(dotnetRoot);

    internal const string DesktopRuntimeUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe";
    internal const string DesktopRuntimeSha512 = "0b907e9312867172a4eb82f4b5ab3f7c2d25e27d8349546d77eeb5d5b8cbecab9ebebfed189b13e9669dc578d892756c548366da539d47b3ddac5efcb7ae72fe";
    internal const long MaxRuntimeDownload = 250L * 1024 * 1024;

    internal static async System.Threading.Tasks.Task<int> DownloadRuntimeAsync(
        string destination, Action<long, long?>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        return await RuntimeDownloader.DownloadAsync(client, new Uri(DesktopRuntimeUrl),
            destination, Convert.FromHexString(DesktopRuntimeSha512), MaxRuntimeDownload,
            onProgress, cancellationToken);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(nint hwnd, string message, string title, uint flags);

    private static string ErrorDescription(int code) => code switch
    {
        57 => "Instalace .NET vyžaduje restart Windows. Po restartu spusť instalátor znovu.",
        58 => "Instalace byla zrušena v potvrzení Windows UAC.",
        59 => "Stahování .NET Desktop Runtime bylo zrušeno.",
        76 => "Nepodařilo se zobrazit přípravu instalace. Bez zobrazeného průběhu nebude .NET instalován.",
        51 => "Nepodařilo se bezpečně stáhnout a ověřit .NET Desktop Runtime.",
        53 or 54 or 55 or 56 => "Instalace požadovaného .NET Desktop Runtime se nezdařila.",
        70 or 73 or 74 => "Nepodařilo se bezpečně rozbalit a ověřit instalační balíček.",
        _ => "Spuštění instalátoru selhalo. Kód chyby: " + code
    };

    private static int RunPackagedInstaller(bool interactive)
    {
        if (!PackageOverlay.HasPinnedPayload) return 40;
        using var singleInstance = new BootstrapSessionGuard();
        if (!singleInstance.Acquired)
        {
            if (interactive)
                MessageBox(0, "Instalátor Spankyho Optimizeru už běží. Dokonči předchozí instalaci.",
                    "Spankyho Optimizer – instalátor", 0x40);
            return 60;
        }
        int code;
        try { code = BootstrapFlow.LaunchAsync().GetAwaiter().GetResult(); }
        catch (Exception) { code = 75; }
        if (code != 0 && interactive)
            MessageBox(0, ErrorDescription(code), "Spankyho Optimizer – instalátor", 0x10);
        return code;
    }

    private static int Main(string[] args)
    {
        // The only double-click path for a properly packaged binary. Diagnostic
        // CLI probes never install runtimes or alter Windows.
        if (args.Length == 0) return RunPackagedInstaller(interactive: true);
        if (args.Length == 1 && args[0] == "--smoke-native-instance-hold")
        {
            using var guard = new BootstrapSessionGuard();
            if (!guard.Acquired) return 60;
            // Diagnostics can only write a fixed-name readiness marker in TEMP.
            string marker = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "spanky-native-hold-" + Environment.ProcessId + ".ready");
            try
            {
                using (var file = new System.IO.FileStream(marker,
                    System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None))
                    file.WriteByte(1);
                System.Threading.Thread.Sleep(1700);
                return 0;
            }
            finally
            {
                try { System.IO.File.Delete(marker); } catch (System.IO.IOException) { }
            }
        }
        if (args.Length == 1 && args[0] == "--smoke-native-instance-probe")
        {
            using var guard = new BootstrapSessionGuard();
            return guard.Acquired ? 0 : 60;
        }
        if (args.Length == 1 && args[0] == "--smoke-pinned-shell-launch")
        {
            string? path = Environment.ProcessPath;
            if (path is null) return 81;
            using var pinned = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.Read);
            byte[] initial = System.Security.Cryptography.SHA256.HashData(pinned);
            // Harmless preflight of the exact native executable held open;
            // no runas, no UAC, no Windows installation.
            try
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path)
                    {
                        UseShellExecute = true,
                        Arguments = "--bootstrap-preflight"
                    });
                if (process is null || !process.WaitForExit(15000) ||
                    (process.ExitCode != 0 && process.ExitCode != 10)) return 82;
                pinned.Position = 0;
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    initial, System.Security.Cryptography.SHA256.HashData(pinned)) ? 0 : 83;
            }
            catch (System.ComponentModel.Win32Exception) { return 84; }
            catch (System.IO.IOException) { return 84; }
        }
        if (args.Length == 1 && args[0] == "--smoke-native-status")
        {
            using var status = new BootstrapProgressWindow();
            if (!status.IsVisible) return 76;
            status.Status("Ověření nativního průběhu instalace…");
            status.DownloadProgress(524288, 1048576);
            System.Threading.Thread.Sleep(400);
            return status.IsVisible ? 0 : 76;
        }
        if (args.Length == 1 && args[0] == "--smoke-native-cancel")
        {
            using var status = new BootstrapProgressWindow();
            if (!status.IsVisible || !status.SimulateCloseForTest()) return 76;
            for (int attempt = 0; attempt < 20 && !status.Cancellation.IsCancellationRequested; attempt++)
                System.Threading.Thread.Sleep(25);
            return status.Cancellation.IsCancellationRequested ? 0 : 77;
        }
        if (args.Length == 1 && args[0] == "--smoke-native-update-stress")
        {
            using var status = new BootstrapProgressWindow();
            if (!status.IsVisible) return 76;
            System.Threading.Tasks.Parallel.For(0, 4000, i =>
            {
                if ((i & 1) == 0) status.Status("Test aktualizace UI " + i);
                else status.DownloadProgress(i * 1024L, 4096000L);
            });
            System.Threading.Thread.Sleep(200);
            return status.IsVisible && status.UpdateBatches > 0 &&
                status.UpdateBatches < 2000 ? 0 : 78;
        }
        if (args.Length == 1 && args[0] == "--smoke-native-guard-close")
        {
            using var status = new BootstrapProgressWindow();
            if (!status.IsVisible) return 76;
            if (!status.TryBeginInstallation()) return 79;
            if (!status.SimulateCloseForTest()) return 79;
            System.Threading.Thread.Sleep(160);
            return status.IsVisible && !status.Cancellation.IsCancellationRequested ? 0 : 79;
        }
        if (args.Length == 1 && args[0] == "--smoke-native-cancel-before-handoff")
        {
            using var status = new BootstrapProgressWindow();
            if (!status.IsVisible || !status.SimulateCloseForTest()) return 76;
            for (int attempt = 0; attempt < 20 && !status.Cancellation.IsCancellationRequested; attempt++)
                System.Threading.Thread.Sleep(25);
            return status.Cancellation.IsCancellationRequested &&
                !status.TryBeginInstallation() ? 0 : 79;
        }
        if (args.Length == 1 && args[0] == "--bootstrap-preflight")
        {
            return BootstrapFlow.DesktopReady() ? 0 : 10;
        }
        if (args.Length == 1 && args[0] == "--launch-packaged-installer")
            return RunPackagedInstaller(interactive: false);
        if (args.Length == 1 && args[0] == "--preflight-package-ready")
        {
            int verified = PackageOverlay.Verify();
            if (verified != 0) return verified;
            return BootstrapFlow.DesktopReady() ? 0 : 10;
        }
        if (args.Length == 1 && args[0] == "--verify-package")
        {
            return PackageOverlay.Verify();
        }
        if (args.Length == 2 && args[0] == "--extract-package")
        {
            return PackageOverlay.Extract(args[1]);
        }
        if (args.Length == 1 && args[0] == "--probe")
        {
            Console.WriteLine("NATIVE_BOOTSTRAP_OK arch=" + RuntimeInformation.ProcessArchitecture);
            return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 0 : 2;
        }
        if (args.Length == 2 && args[0] == "--probe-root")
        {
            var available = HasDesktop10(args[1]);
            Console.WriteLine(available ? "DESKTOP_RUNTIME_READY" : "DESKTOP_RUNTIME_MISSING");
            return available ? 0 : 10;
        }
        Console.Error.WriteLine("Native bootstrap prototype; production installer is not integrated.");
        return 3;
    }
}
