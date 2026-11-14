using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class BootstrapFlow
{
    internal static bool DesktopReady() => Program.HasDesktop10(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));

    // The default installer is OS-changing. Never run this from regression tests
    // on the user's Windows instance; test with BootstrapDecision delegates.
    internal static Task<int> LaunchAsync()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem ||
            Environment.OSVersion.Version.Build < 19041)
            return Task.FromResult(50);

        return BootstrapDecision.EnsureThenLaunchAsync(
            DesktopReady, InstallRuntimeAsync, LaunchWpfAsync);
    }

    private static async Task<int> InstallRuntimeAsync()
    {
        using var progress = new BootstrapProgressWindow();
        if (!progress.IsVisible) return 76; // never install silently if native status UI failed
        progress.Status("Připravuji stažení .NET Desktop Runtime…");
        DirectoryInfo temp = Directory.CreateTempSubdirectory("spanky-dotnet-bootstrap-");
        try
        {
            string runtimeInstaller = Path.Combine(temp.FullName, "windowsdesktop-runtime.exe");
            int status = await Program.DownloadRuntimeAsync(
                runtimeInstaller, progress.DownloadProgress, progress.Cancellation);
            if (status != 0) return status == 35 ? 59 : 51;
            progress.Status("Ověřuji stažený .NET Desktop Runtime…");

            // Pin Microsoft release bytes before UAC and keep the file read-only
            // while the elevated runtime process is created and running.
            using var pinned = new FileStream(runtimeInstaller, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            if (!CryptographicOperations.FixedTimeEquals(
                SHA512.HashData(pinned), Convert.FromHexString(Program.DesktopRuntimeSha512)))
                return 51;

            if (!progress.TryBeginInstallation()) return 59;
            progress.Status("Instaluji .NET Desktop Runtime (potvrď Windows UAC)…");
            using Process? installer = Process.Start(new ProcessStartInfo(runtimeInstaller)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/install /quiet /norestart"
            });
            if (installer is null) return 52;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await installer.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try { installer.Kill(entireProcessTree: true); } catch { }
                return 56;
            }
            return BootstrapDecision.ClassifyRuntimeExit(installer.ExitCode);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return 58; }
        catch (Win32Exception) { return 55; }
        catch (IOException) { return 55; }
        catch (UnauthorizedAccessException) { return 55; }
        finally
        {
            try { temp.Delete(recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<int> LaunchWpfAsync()
    {
        DirectoryInfo staging = Directory.CreateTempSubdirectory("spanky-verified-setup-");
        try
        {
            string extracted = Path.Combine(staging.FullName, "Spankyho_Optimizer_Setup.exe");
            int status = PackageOverlay.Extract(extracted);
            if (status != 0) return 70;

            // Read-only sharing prevents ordinary replacement between digest
            // verification and Windows CreateProcess of the elevated installer.
            using var pinned = new FileStream(extracted, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            if (pinned.Length != PackagedInstallerInfo.Size ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(pinned), Convert.FromHexString(PackagedInstallerInfo.Sha256)))
                return 74;

            using Process? setup = Process.Start(new ProcessStartInfo(extracted)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = staging.FullName
            });
            if (setup is null) return 71;
            await setup.WaitForExitAsync();
            return setup.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return 58; }
        catch (Win32Exception) { return 72; }
        catch (IOException) { return 73; }
        catch (UnauthorizedAccessException) { return 73; }
        finally
        {
            try { staging.Delete(recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
