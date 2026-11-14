using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using SpankyhoOptimizer.Services;

namespace SpankyhoOptimizer;

public sealed partial class App : Application
{
    private async Task<bool> DispatchIntegrationsAsync(string[] cli)
    {
        if (HasArg(cli, "--authenticode-smoke"))
        {
            try
            {
                LogService.Info("security", AuthenticodeVerifier.RunSmoke());
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("security", "AUTHENTICODE_NATIVE_SMOKE_FAIL", ex);
                Environment.Exit(65);
            }
            return true;
        }

        if (HasArg(cli, "--powershell-status-cache-smoke"))
        {
            try
            {
                PowerShellCenterService.InvalidateStatusCache();
                int before = PowerShellCenterService.StatusProcessLoadsForDiagnostics;
                int nativeBefore = PowerShellCenterService.StatusNativeLoadsForDiagnostics;
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                var statusCalls = Enumerable.Range(0, 4)
                    .Select(_ => PowerShellCenterService.GetStatusAsync(cts.Token)).ToArray();
                var results = await Task.WhenAll(statusCalls);
                if ((PowerShellCenterService.StatusProcessLoadsForDiagnostics - before) +
                    (PowerShellCenterService.StatusNativeLoadsForDiagnostics - nativeBefore) != 1 ||
                    results.Any(x => x.PowerShellVersion is "" or "Missing"))
                    throw new InvalidOperationException("PowerShell status single-flight smoke failed.");
                _ = await PowerShellCenterService.GetStatusAsync(cts.Token);
                if (PowerShellCenterService.StatusProcessLoadsForDiagnostics - before + PowerShellCenterService.StatusNativeLoadsForDiagnostics - nativeBefore != 1)
                    throw new InvalidOperationException("PowerShell status TTL cache missed.");

                int modulesBefore = PowerShellCenterService.ModulesProcessLoadsForDiagnostics;
                var moduleCalls = Enumerable.Range(0, 4)
                    .Select(_ => PowerShellCenterService.GetManagedModulesAsync(cts.Token)).ToArray();
                var moduleResults = await Task.WhenAll(moduleCalls);
                if (PowerShellCenterService.ModulesProcessLoadsForDiagnostics - modulesBefore != 1 ||
                    moduleResults.Any(x => x.Count < 5))
                    throw new InvalidOperationException("PowerShell module inventory single-flight smoke failed.");
                _ = await PowerShellCenterService.GetManagedModulesAsync(cts.Token);
                if (PowerShellCenterService.ModulesProcessLoadsForDiagnostics - modulesBefore != 1)
                    throw new InvalidOperationException("PowerShell modules TTL cache missed.");

                string temp = Path.Combine(Path.GetTempPath(), "SpankyHistory-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    string path = Path.Combine(temp, "test-history.txt");
                    var original = Enumerable.Range(0, 14000)
                        .Select(i => i % 301 == 0 ? "" : "Get-Item -Path C:\\\\Test\\\\Folder" + (i % 13001))
                        .ToArray();
                    File.WriteAllLines(path, original, new System.Text.UTF8Encoding(false));
                    string[] expected = original.TakeLast(10000)
                        .Reverse().Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal).Reverse().ToArray();
                    int result = PowerShellCenterService.CompactHistoryFile(path, minimumBytes: 0);
                    string[] actual = await File.ReadAllLinesAsync(path).ConfigureAwait(false);
                    if (result != expected.Length || !actual.SequenceEqual(expected) ||
                        Directory.GetFiles(temp).Length != 1)
                        throw new InvalidOperationException("Bounded PowerShell history compaction changed semantics.");
                    if (PowerShellCenterService.CompactHistoryFile(path, minimumBytes: long.MaxValue) != 0)
                        throw new InvalidOperationException("PowerShell history size gate failed.");
                }
                finally { Directory.Delete(temp, recursive: true); }
                LogService.Info("performance", "POWERSHELL_STATUS_CACHE_SMOKE_OK requests=5 processStarts=1 boundedHistory=true userHistoryUntouched=true");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("POWERSHELL_STATUS_CACHE_SMOKE_FAIL", ex); Environment.Exit(98); }
            return true;
        }

        if (HasArg(cli, "--chris-titus-auto-policy-smoke"))
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                bool due = ChrisTitusIntegrationService.IsAutoUpdateDue(true, true, now.AddHours(-25), now);
                bool initially = ChrisTitusIntegrationService.IsAutoUpdateDue(true, true, default, now);
                bool disabled = ChrisTitusIntegrationService.IsAutoUpdateDue(false, true, default, now);
                bool noUpdate = ChrisTitusIntegrationService.IsAutoUpdateDue(true, false, default, now);
                bool throttled = ChrisTitusIntegrationService.IsAutoUpdateDue(true, true, now.AddHours(-12), now);
                if (!due || !initially || disabled || noUpdate || throttled ||
                    !ChrisTitusIntegrationService.ShouldStartAutomaticInstaller(true, false) ||
                    ChrisTitusIntegrationService.ShouldStartAutomaticInstaller(false, false) ||
                    ChrisTitusIntegrationService.ShouldStartAutomaticInstaller(true, true))
                    throw new InvalidOperationException("Automatic integration update policy invariants failed.");
                LogService.Info("performance", "CHRIS_TITUS_AUTO_POLICY_SMOKE_OK optIn=true backoff24h=true noWrites=true");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CHRIS_TITUS_AUTO_POLICY_SMOKE_FAIL", ex); Environment.Exit(97); }
            return true;
        }

        if (HasArg(cli, "--chris-titus-transaction-smoke"))
        {
            try
            {
                ChrisTitusIntegrationService.RunStagingTransactionSmoke();
                LogService.Info("performance", "CHRIS_TITUS_TRANSACTION_SMOKE_OK rollback=true commit=true configRollback=true noNetwork=true");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CHRIS_TITUS_TRANSACTION_SMOKE_FAIL", ex); Environment.Exit(96); }
            return true;
        }

        if (HasArg(cli, "--chris-titus-update-smoke"))
        {
            try
            {
                string a = new string('a', 40);
                string b = new string('b', 40);
                if (!ChrisTitusIntegrationService.IsGitHubCommit(a) ||
                    ChrisTitusIntegrationService.IsGitHubCommit("main") ||
                    ChrisTitusIntegrationService.IsGitHubCommit(new string('z', 40)) ||
                    ChrisTitusIntegrationService.HasNewCommit(a, a.ToUpperInvariant()) ||
                    !ChrisTitusIntegrationService.HasNewCommit(a, b) ||
                    ChrisTitusIntegrationService.HasNewCommit("../bad", b) ||
                    !ChrisTitusIntegrationService.IsCurrentManagedInstallation(a, a.ToUpperInvariant(), true) ||
                    ChrisTitusIntegrationService.IsCurrentManagedInstallation(a, a, false) ||
                    ChrisTitusIntegrationService.IsCurrentManagedInstallation(a, b, true))
                    throw new InvalidOperationException("Titus update SHA comparison invariant failed.");
                LogService.Info("performance", "CHRIS_TITUS_UPDATE_SMOKE_OK pinnedSha=true compare=true independentChecks=true network=false");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("CHRIS_TITUS_UPDATE_SMOKE_FAIL", ex); Environment.Exit(95); }
            return true;
        }

        if (HasArg(cli, "--spicetify-pinned-stage-smoke"))
        {
            try
            {
                bool shaValid = SpicetifyService.IsValidGitHubCommit(new string('a', 40)) &&
                    !SpicetifyService.IsValidGitHubCommit("main") &&
                    !SpicetifyService.IsValidGitHubCommit(new string('z', 40)) &&
                    !SpicetifyService.IsValidGitHubCommit("../malicious");
                bool scriptValid = SpicetifyService.IsValidMarketplaceInstaller(
                    System.Text.Encoding.UTF8.GetBytes("# valid sample install.ps1\n" + new string('x', 80))) &&
                    !SpicetifyService.IsValidMarketplaceInstaller(new byte[65]) &&
                    !SpicetifyService.IsValidMarketplaceInstaller(new byte[524289]) &&
                    !SpicetifyService.IsValidMarketplaceInstaller([1, 2, 3]);
                if (!shaValid || !scriptValid)
                    throw new InvalidOperationException("Pinned Marketplace script safety validation failed.");
                LogService.Info("performance", "SPICETIFY_PINNED_STAGE_SMOKE_OK sha=true sizeBounds=true nulReject=true network=false writes=false");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SPICETIFY_PINNED_STAGE_SMOKE_FAIL", ex); Environment.Exit(94); }
            return true;
        }

        if (HasArg(cli, "--spicetify-status-smoke"))
        {
            try
            {
                await RunSpicetifyStatusSmokeCoreAsync();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                LogService.Error("spicetify", "SPICETIFY_STATUS_SMOKE_FAIL", ex);
                Environment.Exit(64);
            }
            return true;
        }

        if (HasArg(cli, "--spicetify-marketplace-smoke"))
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                var status = await SpicetifyService.GetStatusAsync(cts.Token);
                if (!status.SpicetifyInstalled) throw new InvalidOperationException("Spicetify resolver did not find the installed CLI.");
                var result = await SpicetifyService.InstallMarketplaceAsync(cts.Token);
                if (!result.Success) throw new InvalidOperationException($"Marketplace install failed: {result.StdOut} {result.StdErr}");
                var after = await SpicetifyService.GetStatusAsync(cts.Token);
                if (!after.MarketplaceInstalled) throw new InvalidOperationException("Marketplace folder was not detected after install.");
                LogService.Info($"SPICETIFY_MARKETPLACE_SMOKE_OK version={after.Version}");
                Environment.Exit(0);
            }
            catch (Exception ex) { LogService.Error("SPICETIFY_MARKETPLACE_SMOKE_FAIL", ex); Environment.Exit(59); }
            return true;
        }

        return false;
    }
}
