using System.Text.RegularExpressions;

namespace Spankyho.QualityTests;

public sealed class SourceArchitectureTests
{
    // Source architecture checks read whole types, including their partial files.
    private static string ReadSource(params string[] parts)
    {
        string full = Path.Combine(parts);
        if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(full);

        string folder = Path.GetDirectoryName(full)!;
        string baseName = Path.GetFileNameWithoutExtension(full);
        IEnumerable<string> patterns;
        if (string.Equals(baseName, "MainWindow", StringComparison.Ordinal))
        {
            patterns = ["MainWindow.cs", "MainWindow.Navigation.cs", "MainWindow.SectionState.cs",
                        "MainWindow.SectionCore.cs", "MainWindow.Render.cs", "MainWindow.TweakCards.cs",
                        "MainWindow.Dialogs.cs"];
        }
        else if (string.Equals(baseName, "App", StringComparison.Ordinal))
        {
            patterns = ["App.cs", "App.Cli.*.cs", "App.Lifecycle.cs", "App.LaunchCore.cs", "App.Smoke.cs"];
        }
        else
        {
            patterns = [baseName + ".cs", baseName + ".*.cs"];
        }

        List<string> files = [];
        foreach (string pattern in patterns)
            files.AddRange(Directory.GetFiles(folder, pattern));
        return string.Concat(files.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText));
    }

    private static readonly Dictionary<string, string[]> ReviewedSyncAccessLines = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ActivityProcessSnapshotService.cs"] =
            [
                "_cached = t.Result;",
                "if (!ReferenceEquals(first.Result, second.Result) ||",
                "!ReferenceEquals(first.Result, await GetAsync().ConfigureAwait(false)))",
                "if (ReferenceEquals(first.Result, refreshed) || refreshed.Length > TopCount)"
            ],
            ["AiAutomaticProcessPriorityService.cs"] =
            [
                "LastTrialResult = _lastCheckpoint.Result;"
            ],
            ["AiForegroundNativeOwnedWindowSmoke.cs"] =
            [
                "if (!ready.Wait(TimeSpan.FromSeconds(3)))"
            ],
            ["AiPowerTelemetryEvidenceSmoke.cs"] =
            [
                "Check(firstProbe.Result.Utc <= secondProbe.Result.Utc,"
            ],
            ["AiPriorityTrialMemoryPolicy.cs"] =
            [
                "!string.IsNullOrWhiteSpace(value.Result) &&",
                "value.Result.Length <= 320 &&",
                "!string.IsNullOrWhiteSpace(value.Result) &&",
                "value.Result.Length <= 320 &&"
            ],
            ["AudioEnhancementsTweakService.cs"] =
            [
                "if (!started.Wait(TimeSpan.FromSeconds(3)))",
                "if (!waiting.Wait(TimeSpan.FromSeconds(2)) || !waiting.Result)",
                "if (!timeout.Wait(TimeSpan.FromSeconds(3)) || !timeout.Result)",
                "if (!ownerReady.Wait(TimeSpan.FromSeconds(3)) ||"
            ],
            ["FanTrayIconService.cs"] =
            [
                "if (!ready.Wait(TimeSpan.FromSeconds(5)))"
            ],
            ["GpuDriverService.cs"] =
            [
                "if (ReferenceEquals(recovered, shared) || recovered.Result?.Name != \"Test GPU\")"
            ],
            ["HardwareTelemetryBridgeService.cs"] =
            [
                "if (!entered.Wait(TimeSpan.FromSeconds(3)))",
                "return ownerRelease.Wait(TimeSpan.FromSeconds(4));",
                "if (!ownerEntered.Wait(TimeSpan.FromSeconds(3)))",
                "if (!waiterStarted.Wait(TimeSpan.FromSeconds(3)))",
                "if (!waiter.Wait(TimeSpan.FromSeconds(2)) ||",
                "_ = owner.Wait(TimeSpan.FromSeconds(5));"
            ],
            ["WindowsDefaultGuardService.cs"] =
            [
                "if (!starting.Wait(TimeSpan.FromSeconds(2)))",
                "if (!started.Wait(TimeSpan.FromSeconds(2)))",
                "if (!started.Wait(TimeSpan.FromSeconds(2)))",
                "if (!entered.Wait(TimeSpan.FromSeconds(2)))"
            ]
        };

    private static string ProjectRoot
    {
        get
        {
            DirectoryInfo? d = new(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "PROJECT_STATE.md")))
                d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Project root not found.");
        }
    }

    [Fact]
    public void Navigation_selection_has_a_single_explicit_geometry_pass()
    {
        string main = ReadSource(ProjectRoot, "src", "SpankyhoOptimizer", "MainWindow.cs");
        string shell = ReadSource(ProjectRoot, "src", "SpankyhoOptimizer", "MainWindow.Shell.cs");
        const string entry = "private void UpdateVisualSelection(string key, string glyph)";
        int start = shell.IndexOf(entry, StringComparison.Ordinal);
        Assert.True(start >= 0, "Navigation selection method not found.");
        string selection = shell[start..];
        Assert.DoesNotContain("UpdateRowlessShellGeometry(", selection, StringComparison.Ordinal);

        const string finalPass = "UpdateRowlessShellGeometry(heroHeightOverride: targetHeaderHeight)";
        int firstPass = main.IndexOf(finalPass, StringComparison.Ordinal);
        Assert.True(firstPass >= 0, "Final navigation geometry call not found.");
        Assert.True(main.IndexOf(finalPass, firstPass + finalPass.Length, StringComparison.Ordinal) < 0,
            "Navigation calculates final geometry more than once.");
        Assert.Contains("UpdateVisualSelection(key, sectionDefinition.IconGlyph)", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_changelog_latest_heading_matches_release_version()
    {
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(ProjectRoot, "Directory.Build.props"));
        string version = props.Descendants("SpankyVersion").Single().Value;
        string first = File.ReadLines(Path.Combine(ProjectRoot, "WORK_PROGRESS.md")).First();
        Assert.StartsWith("## " + version + " ", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_requires_architecture_and_readonly_core_tests_before_staging()
    {
        string release = ReadSource(ProjectRoot, "build-release.ps1");
        string[] contracts =
        [
            "Spankyho.QualityTests.csproj",
            "RELEASE_SOURCE_ARCHITECTURE_GATE_FAILED",
            "RELEASE_SOURCE_ARCHITECTURE_GATE_OK",
            "CoreTests.csproj",
            "RELEASE_READONLY_CORE_GATE_FAILED",
            "RELEASE_READONLY_CORE_GATE_OK"
        ];
        foreach (string contract in contracts)
            Assert.Contains(contract, release, StringComparison.Ordinal);

        int firstGate = release.IndexOf("RELEASE_SOURCE_ARCHITECTURE_GATE_FAILED", StringComparison.Ordinal);
        int secondGate = release.IndexOf("RELEASE_READONLY_CORE_GATE_FAILED", StringComparison.Ordinal);
        int installerBuild = release.IndexOf("$installerUiProject=Join-Path", StringComparison.Ordinal);
        int stagingCleanup = release.IndexOf("Remove-Item $stage,$release", StringComparison.Ordinal);
        Assert.True(firstGate > 0 && secondGate > firstGate &&
            installerBuild > secondGate && stagingCleanup > installerBuild,
            "Architecture and read-only core tests must fail before installer build and staging cleanup.");
        Assert.Contains("if($LASTEXITCODE -ne 0){throw 'RELEASE_SOURCE_ARCHITECTURE_GATE_FAILED'}",
            release, StringComparison.Ordinal);
        Assert.Contains("if($LASTEXITCODE -ne 0){throw 'RELEASE_READONLY_CORE_GATE_FAILED'}",
            release, StringComparison.Ordinal);
        // VSTest may return exit code 0 for a filter that selects no tests.
        // Every release must verify the minimum actual TRX pass count.
        Assert.Contains("Assert-ReleaseUnitResults $qualityReport 43", release, StringComparison.Ordinal);
        Assert.Contains("Assert-ReleaseUnitResults $coreReport 24", release, StringComparison.Ordinal);
        Assert.Contains("if(-not(Test-Path -LiteralPath $path))", release, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_manifest_verifies_actual_files_before_distribution()
    {
        string release = ReadSource(ProjectRoot, "build-release.ps1");
        string verifierPath = Path.Combine(ProjectRoot, "tools", "Verify-OptimizerReleaseArtifactManifest.ps1");
        string fixturePath = Path.Combine(ProjectRoot, "tools", "Test-OptimizerReleaseArtifactManifest.ps1");
        Assert.True(File.Exists(verifierPath), "Release artifact verifier is missing.");
        Assert.True(File.Exists(fixturePath), "Release artifact tampering regression is missing.");
        string verifier = File.ReadAllText(verifierPath);
        string fixtures = File.ReadAllText(fixturePath);
        Assert.Contains("Get-FileHash -LiteralPath $path -Algorithm SHA256", verifier, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations]::FixedTimeEquals", verifier, StringComparison.Ordinal);
        Assert.Contains("Assert-ArtifactHash $InstallerPath", verifier, StringComparison.Ordinal);
        Assert.Contains("Assert-ArtifactHash $PayloadPath", verifier, StringComparison.Ordinal);
        Assert.Contains("tampered-payload", fixtures, StringComparison.Ordinal);
        Assert.Contains("tampered-installer", fixtures, StringComparison.Ordinal);
        Assert.Contains("RELEASE_MANIFEST_FIXTURE_GATE_FAILED", release, StringComparison.Ordinal);
        Assert.Contains("RELEASE_MANIFEST_VERIFIER_MISSING", release, StringComparison.Ordinal);
        const string invocation = "& $manifestVerifier -ManifestPath $manifestPath -InstallerPath $compactFinal -PayloadPath $payload -ExpectedVersion $releaseVersion";
        int verification = release.IndexOf(invocation, StringComparison.Ordinal);
        int delivery = release.IndexOf("Assert-ReleaseStillCurrent 'before-desktop-delivery'", StringComparison.Ordinal);
        Assert.True(verification >= 0 && delivery > verification,
            "Artifact SHA-256 verification must precede desktop publication.");
        string publisherPath = Path.Combine(ProjectRoot, "tools", "Publish-Optimizer-DesktopInstaller.ps1");
        string desktopFixturePath = Path.Combine(ProjectRoot, "tools", "Test-OptimizerDesktopExpectedHash.ps1");
        Assert.True(File.Exists(desktopFixturePath), "Pinned desktop hash fixture is missing.");
        string publisher = File.ReadAllText(publisherPath);
        string desktopFixture = File.ReadAllText(desktopFixturePath);
        Assert.Contains("$ExpectedSha256", publisher, StringComparison.Ordinal);
        Assert.Contains("DESKTOP_SETUP_EXPECTED_HASH_MISMATCH", publisher, StringComparison.Ordinal);
        Assert.Contains("-InstallerPath $compactFinal -ExpectedSha256 $compactHash", release, StringComparison.Ordinal);
        Assert.Contains("RELEASE_DESKTOP_PINNED_HASH_FIXTURE_GATE_FAILED", release, StringComparison.Ordinal);
        Assert.Contains("changed-source-after-manifest", desktopFixture, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_installer_embeds_exact_payload_and_pins_desktop_hash()
    {
        string builder = ReadSource(ProjectRoot, "build-installer-compact.ps1");
        string installer = ReadSource(ProjectRoot, "installer", "InstallerMain.cs");
        string tests = ReadSource(ProjectRoot, "tests", "InstallerUiTests", "Program.cs");
        Assert.Contains("--verify-embedded-payload-sha256", builder, StringComparison.Ordinal);
        Assert.Contains("COMPACT_EMBEDDED_PAYLOAD_HASH_MISMATCH", builder, StringComparison.Ordinal);
        Assert.Contains("COMPACT_EMBEDDED_PAYLOAD_HASH_OK", builder, StringComparison.Ordinal);
        Assert.Contains("& dotnet $managed --verify-embedded-payload-sha256 $payloadSha256", builder, StringComparison.Ordinal);
        Assert.Contains("COMPACT_EMBEDDED_PAYLOAD_MANAGED_DLL_MISSING", builder, StringComparison.Ordinal);
        Assert.Contains("([version]$Version).ToString(2)", builder, StringComparison.Ordinal);
        Assert.Contains("MatchesEmbeddedPayloadSha256(embedded, args[1])", installer, StringComparison.Ordinal);
        Assert.Contains("MatchesEmbeddedPayloadSha256(stream, args[1])", installer, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals(actual, expected)", installer, StringComparison.Ordinal);
        Assert.Contains("embedded payload hash rejects altered same-length bytes", tests, StringComparison.Ordinal);
        int embeddedHash = builder.IndexOf("COMPACT_EMBEDDED_PAYLOAD_HASH_OK", StringComparison.Ordinal);
        int outputCopy = builder.IndexOf("Copy-Item $exe.FullName $final", StringComparison.Ordinal);
        Assert.True(embeddedHash > 0 && outputCopy > embeddedHash,
            "Embedded hash verification must occur before distributing the setup.");
        Assert.Contains("-InstallerPath $final -ExpectedSha256 $sha", builder, StringComparison.Ordinal);
        string release = ReadSource(ProjectRoot, "build-release.ps1");
        Assert.Contains("$installerUiEvidence.count -lt 65", release, StringComparison.Ordinal);
        Assert.Contains("@('--release-installer-smoke-bundle',$payloadHash)", release, StringComparison.Ordinal);
        Assert.Contains("--test-transaction-scoped-post-move-rollback $scopedRollbackOriginal", release, StringComparison.Ordinal);
        Assert.Contains("RELEASE_SCOPED_ROLLBACK_GATE_FAILED", release, StringComparison.Ordinal);
        int rollbackGate = release.IndexOf("RELEASE_SCOPED_ROLLBACK_GATE_OK", StringComparison.Ordinal);
        int artifactCacheCopy = release.IndexOf("Copy-FileWithRetry $compactFinal $cachedCompactSetup", StringComparison.Ordinal);
        Assert.True(rollbackGate > 0 && artifactCacheCopy > rollbackGate,
            "Scoped interrupted-upgrade rollback must pass before caching the installer.");
    }

    [Fact]
    public void Compact_builder_stages_privately_without_deleting_another_build()
    {
        string builder = ReadSource(ProjectRoot, "build-installer-compact.ps1");
        Assert.Contains("'artifacts\\.cache\\compact-build-'+[guid]::NewGuid().ToString('N')",
            builder, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::CreateDirectory($work)|Out-Null", builder,
            StringComparison.Ordinal);
        Assert.Contains("} finally {", builder, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop",
            builder, StringComparison.Ordinal);
        Assert.DoesNotContain("$work=Join-Path $root 'artifacts\\_compact-installer'",
            builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item $work -Recurse -Force",
            builder, StringComparison.Ordinal);
        int stage = builder.IndexOf("compact-build-", StringComparison.Ordinal);
        int sourcePublish = builder.IndexOf("'publish',$proj", StringComparison.Ordinal);
        int cleanup = builder.IndexOf("Remove-Item -LiteralPath $work -Recurse -Force",
            StringComparison.Ordinal);
        Assert.True(stage >= 0 && sourcePublish > stage && cleanup > sourcePublish,
            "Each compact build must use and later clean only its own staging directory.");
        string verifier = ReadSource(ProjectRoot,
            "tools", "Verify-OptimizerCompactBundle.ps1");
        string tamper = ReadSource(ProjectRoot,
            "tools", "Test-OptimizerCompactBundle.ps1");
        string release = ReadSource(ProjectRoot, "build-release.ps1");
        Assert.Contains("GetMethod('IsBundle'", verifier, StringComparison.Ordinal);
        Assert.Contains("COMPACT_BUNDLE_MANAGED_DLL_HASH_MISMATCH", verifier, StringComparison.Ordinal);
        Assert.Contains("COMPACT_BUNDLE_REJECT_PASS", tamper, StringComparison.Ordinal);
        Assert.Contains("COMPACT_SINGLEFILE_BUNDLE_INTEGRITY_FAILED", builder, StringComparison.Ordinal);
        Assert.Contains("RELEASE_COMPACT_BUNDLE_TAMPER_GATE_FAILED", release, StringComparison.Ordinal);
        int verifiedBundle = builder.IndexOf("COMPACT_SINGLEFILE_BUNDLE_INTEGRITY_OK", StringComparison.Ordinal);
        int publishedCopy = builder.IndexOf("Copy-Item $exe.FullName $final", StringComparison.Ordinal);
        Assert.True(verifiedBundle >= 0 && publishedCopy > verifiedBundle,
            "The actual EXE's embedded DLL must be verified before release copying.");
        int releaseTamper = release.IndexOf("RELEASE_COMPACT_BUNDLE_TAMPER_GATE_OK", StringComparison.Ordinal);
        int releaseCopy = release.IndexOf("Copy-FileWithRetry $compactFinal $cachedCompactSetup", StringComparison.Ordinal);
        Assert.True(releaseTamper >= 0 && releaseCopy > releaseTamper,
            "The tampered Single-File bundle must be rejected before release cache copying.");
    }

    [Fact]
    public void Installer_recovery_and_pruning_respect_destination_ownership()
    {
        string installer = ReadSource(ProjectRoot, "installer", "InstallerMain.cs");
        string tests = ReadSource(ProjectRoot, "tests", "InstallerUiTests", "Program.cs");
        Assert.Contains("IsFreshIsolatedReleaseBundleDestination(args[1])", installer, StringComparison.Ordinal);
        Assert.Contains("bool displacedPreserved = GetDisplacedInstallations(recovery)", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("if (Directory.Exists(root)) Directory.Delete(root, true);", installer, StringComparison.Ordinal);
        Assert.Contains("existing TEMP directory is never cleared by release bundle", tests, StringComparison.Ordinal);
        Assert.Contains("existing TEMP file is never accepted as release bundle root", tests, StringComparison.Ordinal);
        Assert.Contains("synthetic recovery never deletes another installer staging directory", tests, StringComparison.Ordinal);
        Assert.Contains("recovery never adopts a valid-looking backup from another target", tests, StringComparison.Ordinal);
        Assert.Contains("destination-bound backup recovery restores only its rightful target", tests, StringComparison.Ordinal);
        Assert.Contains("recovery permits predecessor release within same major but rejects downgrade or foreign major", tests, StringComparison.Ordinal);
        Assert.Contains("IsCompatibleRecoveryVersion(prior, currentVersion)", installer, StringComparison.Ordinal);
        Assert.Contains("backup pruning never selects side-by-side or legacy backups", tests, StringComparison.Ordinal);
        Assert.Contains("string[] valid = GetOwnedRecoveryBackups(target)", installer, StringComparison.Ordinal);
        Assert.Contains("GetPrunableBackups(target, true).Length == 2", installer, StringComparison.Ordinal);
        Assert.Contains("GetOwnedRecoveryBackups(text)", installer, StringComparison.Ordinal);
        Assert.Contains("RECOVERY retained unbound/other-target backups=", installer, StringComparison.Ordinal);
        Assert.Contains("BackupDirectoryForTarget(text, Guid.NewGuid().ToString(\"N\"))", installer, StringComparison.Ordinal);
        string restorationCandidates = installer.Split("string[] array = ownedBackups", StringSplitOptions.None)[1]
            .Split("bool flag = Directory.Exists(text)", StringSplitOptions.None)[0];
        Assert.DoesNotContain("Directory.GetDirectories(path", restorationCandidates, StringComparison.Ordinal);
        Assert.Contains("RECOVERY retained unowned staging", installer, StringComparison.Ordinal);
        Assert.Contains("string.Equals(args[0], \"--test-recovery\"", installer, StringComparison.Ordinal);
        string recoveryBody = installer.Split("internal static void RecoverInterruptedInstall(", StringSplitOptions.None)[1]
            .Split("// Preserve the previous installed payload", StringSplitOptions.None)[0];
        Assert.DoesNotContain("Directory.Delete(staging", recoveryBody, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(text, recursive: true); // Only the fixture's own staging.", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Readonly_service_policy_gate_never_runs_mutating_service_bundle()
    {
        string app = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "App.cs");
        string policy = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "App.ServicePolicySmoke.cs");
        string release = ReadSource(ProjectRoot, "build-release.ps1");
        Assert.Contains("Environment.Exit(RunReadOnlyServicePolicySmoke())", app, StringComparison.Ordinal);
        Assert.Contains("--release-service-policy-smoke", app, StringComparison.Ordinal);
        Assert.Contains("NativeNicTuningService.ValidateProviderReadStateClassificationForSmoke()", policy, StringComparison.Ordinal);
        Assert.Contains("AiCloudProfileService.CanReuseRecentUpload(", policy, StringComparison.Ordinal);
        Assert.Contains("FanAgentStageMetrics.RunPolicySmoke()", policy, StringComparison.Ordinal);
        Assert.Contains("READONLY_SERVICE_POLICY_SMOKE_OK checks=3", policy, StringComparison.Ordinal);
        foreach (string forbidden in new[]
        {
            "RunReleaseServiceSmokeBundleAsync", "WindowsBuildRevalidationService",
            "TweakDriftWatchdogService", "ProcessRunner", "Registry.",
            "Process.Start", "Start-Process", "PowerShell", "File.Write",
            "Directory.Delete", "ServiceController", "SetNic", "DnsBenchmarkService"
        })
            Assert.DoesNotContain(forbidden, policy, StringComparison.Ordinal);
        int safeCli = release.IndexOf("'--release-service-policy-smoke'", StringComparison.Ordinal);
        int statefulCli = release.IndexOf("'--release-service-smoke'", safeCli + 1, StringComparison.Ordinal);
        Assert.True(safeCli > 0 && statefulCli > safeCli,
            "Pure service policy tests must precede stateful service smoke.");
        Assert.Contains("RELEASE_READONLY_SERVICE_POLICY_OK", release, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_readiness_probe_bypasses_crashloop_and_postready_maintenance()
    {
        string source = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "App.cs");
        string startup = source.Split("private async Task LaunchNormalStartupAsync(", StringSplitOptions.None)[1]
            .Split("private async Task RunPostReadyStartupMaintenanceAsync(", StringSplitOptions.None)[0];
        Assert.Contains("!smokeMode && !runtimeReadyProbe", startup, StringComparison.Ordinal);
        Assert.Contains("IsSmokeUi = smokeMode || runtimeReadyProbe;", startup, StringComparison.Ordinal);
        Assert.Contains("if (IsSmokeUi) ShowSmokeWindowWithoutActivation(_window);", startup, StringComparison.Ordinal);
        Assert.Contains("if (!runtimeReadyProbe)", startup, StringComparison.Ordinal);
        string guarded = startup.Split("if (!runtimeReadyProbe)", StringSplitOptions.None)[1]
            .Split("string? runtimeReadyFile", StringSplitOptions.None)[0];
        Assert.Contains("CrashLoopGuardService.CompleteStartup(AppVersion);", guarded, StringComparison.Ordinal);
        Assert.Contains("RunPostReadyStartupMaintenanceAsync(", guarded, StringComparison.Ordinal);
        Assert.Contains("RUNTIME_READY_SIGNAL_OK", startup, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(0);", startup, StringComparison.Ordinal);
        string release = ReadSource(ProjectRoot, "build-release.ps1");
        Assert.Contains("RUNTIME_READY_PROBE_SELF_EXIT_OK", release, StringComparison.Ordinal);
        Assert.Contains("RELEASE_RUNTIME_READY_MARKER_ALREADY_EXISTS", release, StringComparison.Ordinal);
        Assert.Contains("Test-OptimizerRuntimeReadyIsolation.ps1", release, StringComparison.Ordinal);
        Assert.Contains("RELEASE_RUNTIME_READY_ISOLATION_FAILED", release, StringComparison.Ordinal);
        string readyFixture = ReadSource(ProjectRoot,
            "tools", "Test-OptimizerRuntimeReadyIsolation.ps1");
        Assert.Contains("READY_ISOLATION_SUITE_PASS valid=1 rejected=3", readyFixture, StringComparison.Ordinal);
        Assert.Contains("READY_ISOLATION_EXISTING_FILE_CHANGED", readyFixture, StringComparison.Ordinal);
        Assert.Contains("READY_ISOLATION_FOREIGN_FILE_CHANGED", readyFixture, StringComparison.Ordinal);
        int readyFixtureGate = release.IndexOf("RELEASE_RUNTIME_READY_ISOLATION_OK", StringComparison.Ordinal);
        int payloadFingerprint = release.IndexOf("$payloadState=Join-Path $cache", StringComparison.Ordinal);
        Assert.True(readyFixtureGate > 0 && payloadFingerprint > readyFixtureGate,
            "Actual readiness isolation must be verified before packaging the release payload.");
        Assert.Contains("TryValidateIsolatedReadinessMarkerPath(runtimeReadyFile, out isolatedMarker)", startup, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", startup, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(74);", startup, StringComparison.Ordinal);
        string pathPolicy = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "App.RuntimeReadinessPath.cs");
        Assert.Contains("SpankyhoOptimizer-ReleaseGate-", pathPolicy, StringComparison.Ordinal);
        Assert.Contains("SpankyhoOptimizer-ReadyTest-", pathPolicy, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact(guidSuffix, \"N\"", pathPolicy, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", pathPolicy, StringComparison.Ordinal);
        Assert.Contains("File.Exists(file) || Directory.Exists(file)", pathPolicy, StringComparison.Ordinal);
        Assert.Contains("$p.WaitForExit(12000)", release, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process -Id $runtimePid", release, StringComparison.Ordinal);
        Assert.DoesNotContain("'/PID',\"$runtimePid\"", release, StringComparison.Ordinal);
    }

    [Fact]
    public void Themes_wallpaper_state_is_read_once_per_render()
    {
        string source = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "MainWindow.Themes.cs");
        Assert.Equal(1, source.Split("WallpaperThemeService.CurrentTheme",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("bool active = currentWallpaperTheme == theme", source, StringComparison.Ordinal);
        Assert.Contains("previewImage.Source = new BitmapImage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Themes_cached_tree_has_bounded_lifetime_and_refresh_guards()
    {
        string cache = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "MainWindow.ThemesPageCache.cs");
        string lifecycle = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "MainWindow.Lifecycle.cs");
        string theme = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "MainWindow.Theme.cs");
        Assert.Contains("TimeSpan.FromSeconds(8)", cache, StringComparison.Ordinal);
        Assert.Contains("AttachCachedElement(element, deferHostInvalidation: true)", cache, StringComparison.Ordinal);
        Assert.Contains("RunThemesPageCacheSmokeAsync()", theme, StringComparison.Ordinal);
        Assert.Contains("InvalidateThemesPageCache();", lifecycle, StringComparison.Ordinal);
        Assert.Contains("InvalidateThemesPageCache();", theme, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(ThemesPageCacheTtl, token)", cache, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCts.Token)", cache, StringComparison.Ordinal);
        Assert.Contains("_themesCacheExpiryCts?.Cancel();", cache, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", cache, StringComparison.Ordinal);
        Assert.Contains("ThemesCacheLifetimePolicy.CanExpire(", cache, StringComparison.Ordinal);
        Assert.Contains("ThemesCacheLifetimePolicy.IsFresh(", cache, StringComparison.Ordinal);
        Assert.Contains("Inactive Themes cache retained its tree or expiry token after deadline", cache, StringComparison.Ordinal);
        Assert.Contains("Rebuilding Themes failed to cancel its previous deadline", cache, StringComparison.Ordinal);
        Assert.Contains("_themesCacheExpiryCts.IsCancellationRequested", cache, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_ProcessStart_does_not_expand_beyond_known_debt()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        string[] knownDebt =
        [
            "AppUpdateService.cs",
            // Background updater launches the isolated automatic-update popup
            // with a verified manifest and bounded lifecycle wait.
            "BackgroundUpdateService.cs",
            // Reviewed one-off external launches: backup Explorer and Explorer
            // recovery after its explicit termination. A separate test
            // below pins the allowed direct-start count and target per file.
            "ChrisTitusIntegrationService.cs",
            // Early Explorer verbs run before WinUI startup; see the strict
            // action-specific launch and confirmation contract below.
            "ContextMenuNativeActions.cs",
            "GTweakMaintenanceService.cs",
            "GpuDriverService.cs",
            "NativeNicTuningService.cs",
            "PowerShellTweakerService.cs",
            "ProcessRunner.cs",
            "SafeModeLauncherService.cs",
            "WindowsActions.cs",
            "WindowsModernizerService.cs"
        ];

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(services, "*.cs"))
        {
            string text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"\bProcess\s*\.\s*Start\s*\(",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                offenders.Add(Path.GetFileName(file));
        }

        string[] newDebt = offenders.Except(knownDebt, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(newDebt.Length == 0,
            "New direct Process.Start call sites: " + string.Join(", ", newDebt));
    }

    [Fact]
    public void Reviewed_direct_process_starts_remain_single_purpose()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        var expected = new (string File, string Target)[]
        {
            ("ChrisTitusIntegrationService.cs", "explorer.exe"),
            ("GTweakMaintenanceService.cs", "explorer.exe"),
            ("BackgroundUpdateService.cs", "ProcessStartInfo(executable)")
        };
        foreach (var (file, target) in expected)
        {
            string source = ReadSource(services, file);
            int first = source.IndexOf("Process.Start(", StringComparison.Ordinal);
            Assert.True(first >= 0, $"Reviewed process start disappeared: {file}");
            Assert.True(source.IndexOf("Process.Start(", first + "Process.Start(".Length, StringComparison.Ordinal) < 0,
                $"Unexpected additional direct Process.Start in {file}");
            Assert.Contains(target, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Native_context_menu_process_launches_remain_bounded_and_explicit()
    {
        string source = ReadSource(ProjectRoot,
            "src", "SpankyhoOptimizer", "Services", "ContextMenuNativeActions.cs");
        int directStarts = Regex.Matches(source, @"\bProcess\s*\.\s*Start\s*\(",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Count;
        Assert.Equal(2, directStarts);
        // The advanced-startup reboot requires a positive confirmation before execution.
        Assert.Contains("case \"--context-advanced-startup\":", source, StringComparison.Ordinal);
        Assert.Contains("if (MessageBoxW(IntPtr.Zero,", source, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), \"shutdown.exe\")", source, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false, CreateNoWindow = true", source, StringComparison.Ordinal);
        // Elevated terminal requires ShellExecute's explicit UAC verb and validated directory.
        Assert.Contains("Verb = \"runas\"", source, StringComparison.Ordinal);
        Assert.Contains("if (!Directory.Exists(workingDirectory))", source, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory = workingDirectory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_over_async_does_not_expand_beyond_known_debt()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        var baselineMax = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["AdvancedCenterService.cs"] = 1,
            ["DirectXGlobalSettingsService.cs"] = 1,
            ["DnsBenchmarkService.cs"] = 4,
            ["NativeNicTuningService.cs"] = 1,
            ["ServiceCatalogService.cs"] = 1,
            ["SpicetifyService.cs"] = 1,
            ["V10PortServices.cs"] = 2,
            ["WindowsActions.cs"] = 3
        };

        // The original text-only scan conflated Task<T>.Result with serialized
        // Checkpoint.Result and Task.Wait with ManualResetEventSlim.Wait. Every
        // reviewed non-blocking/synthetic site is pinned by its EXACT source line:
        // a new call or changed context still fails the gate for review.
        var reviewed = ReviewedSyncAccessLines;

        foreach (string file in reviewed.Keys)
            Assert.True(File.Exists(Path.Combine(services, file)), "Reviewed source missing: " + file);

        var actual = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(services, "*.cs"))
        {
            string name = Path.GetFileName(file);
            string[] matches = File.ReadLines(file).Select(line => line.Trim())
                .Where(line => Regex.IsMatch(line, @"\.Result\b|\.Wait\s*\(",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                .ToArray();
            if (reviewed.TryGetValue(name, out string[]? expected))
            {
                Assert.Equal(expected, matches);
                continue;
            }
            if (matches.Length > 0) actual[name] = matches.Length;
        }

        var regressions = actual
            .Where(x => !baselineMax.TryGetValue(x.Key, out int max) || x.Value > max)
            .Select(x => $"{x.Key}:{x.Value}>{(baselineMax.TryGetValue(x.Key, out int max) ? max : 0)}")
            .ToArray();
        Assert.True(regressions.Length == 0,
            "New sync-over-async debt: " + string.Join(", ", regressions));
    }

    [Fact]
    public void Reviewed_sync_sites_retain_completion_and_thread_safety_guards()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        string Read(string name) => ReadSource(root, name);

        string activity = Read("ActivityProcessSnapshotService.cs");
        Assert.Contains("t.IsCompletedSuccessfully", activity, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(first, second)", activity, StringComparison.Ordinal);
        string power = Read("AiPowerTelemetryEvidenceSmoke.cs");
        Assert.Contains("await Task.WhenAll(firstProbe, secondProbe)", power, StringComparison.Ordinal);
        string priority = Read("AiPriorityTrialMemoryPolicy.cs");
        Assert.Contains("string ProcessName, string Result, DateTime MeasuredUtc", priority, StringComparison.Ordinal);
        string ai = Read("AiAutomaticProcessPriorityService.cs");
        Assert.Contains("AiPriorityTrialMemoryPolicy.Accept(_lastCheckpoint, now)", ai, StringComparison.Ordinal);
        string audio = Read("AudioEnhancementsTweakService.cs");
        Assert.Contains("RunAudioMutexCancellationSmoke()", audio, StringComparison.Ordinal);
        Assert.Contains("new ManualResetEventSlim(false)", audio, StringComparison.Ordinal);
        string fan = Read("FanTrayIconService.cs");
        Assert.Contains("StartOnMessageThread()", fan, StringComparison.Ordinal);
        Assert.Contains("new ManualResetEventSlim()", fan, StringComparison.Ordinal);
        string gpu = Read("GpuDriverService.cs");
        Assert.Contains("RunGpuPersistentWarmFlightPolicySmoke()", gpu, StringComparison.Ordinal);
        Assert.Contains("Task.FromResult<GpuDriverInfo?>(", gpu, StringComparison.Ordinal);
        string hardware = Read("HardwareTelemetryBridgeService.cs");
        Assert.Contains("RunBridgeMutexCancellationSmoke()", hardware, StringComparison.Ordinal);
        Assert.Contains("RunBridgeMutexThreadAffinitySmoke()", hardware, StringComparison.Ordinal);
        string defaults = Read("WindowsDefaultGuardService.cs");
        Assert.Contains("RunJournalContentionSmoke()", defaults, StringComparison.Ordinal);
        Assert.Contains("new ManualResetEventSlim(false)", defaults, StringComparison.Ordinal);
        string diagnostics = Read("SystemRepairDiagnosticsService.cs");
        string fanProfile = Read("FanProfileService.cs");
        Assert.Contains("_ = Task.Run(async () =>", fanProfile, StringComparison.Ordinal);
        Assert.Contains("await EnsureStartupAgentRegisteredCoreAsync().ConfigureAwait(false)", fanProfile, StringComparison.Ordinal);
        Assert.Contains("await CleanupLegacyWatchdogTasksBestEffortAsync().ConfigureAwait(false)", fanProfile, StringComparison.Ordinal);
        Assert.Contains("await EnsureFanAgentTaskRegisteredBestEffortAsync(exe).ConfigureAwait(false)", fanProfile, StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref _startupRegistrationCheckRunning, 0)", fanProfile, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", fanProfile, StringComparison.Ordinal);
        Assert.Contains("RepairDiagnostic[] completed = await Task.WhenAll(", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("service.Result", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void Synchronous_GetAwaiter_GetResult_calls_do_not_expand()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["DriverInventoryService.cs"] = 1,
            ["DnsProfileService.cs"] = 2,
            ["FaultInjectionService.cs"] = 1,
            ["HardwareTelemetryBridgeService.cs"] = 10,
            ["NativeNicTuningService.cs"] = 6,
            ["WindowsDefaultGuardService.cs"] = 6
        };
        foreach (string file in Directory.EnumerateFiles(services, "*.cs"))
        {
            int count = Regex.Matches(File.ReadAllText(file),
                @"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(\s*\)",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Count;
            int max = known.TryGetValue(Path.GetFileName(file), out int value) ? value : 0;
            Assert.True(count <= max,
                $"Unexpected blocking GetAwaiter/GetResult: {Path.GetFileName(file)} {count}>{max}");
        }
    }

    [Fact]
    public void Privileged_process_broker_stays_fail_closed()
    {
        string file = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services", "PrivilegedProcessRunner.cs");
        string text = File.ReadAllText(file);

        Assert.Contains("ValidateRequest(file, arguments)", text, StringComparison.Ordinal);
        Assert.Contains("WorkerIpcSecurity", ReadSource(
            ProjectRoot, "src", "SpankyhoOptimizer", "Services", "WorkerClientService.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"reg.exe\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sc.exe\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"bcdedit.exe\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"fsutil.exe\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"schtasks.exe\"", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migrated_privileged_mutations_do_not_bypass_broker()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        var checks = new (string File, string Pattern)[]
        {
            ("DnsProfileService.cs", """(?<![A-Za-z])ProcessRunner\.RunAsync\s*\(\s*["](?:netsh|ipconfig)\.exe["]"""),
            ("MtuOptimizerService.cs", """(?<![A-Za-z])ProcessRunner\.RunAsync\s*\(\s*["]netsh\.exe["]"""),
            ("DriverInventoryService.cs", """(?<![A-Za-z])ProcessRunner\.RunAsync\s*\(\s*["]pnputil\.exe["]\s*,\s*[$]?["]/export-driver"""),
            ("StartupAndSystemServices.cs", """(?<![A-Za-z])ProcessRunner\.RunAsync\s*\(\s*["]pnputil\.exe["]\s*,\s*[$]?["]/remove-device"""),
            ("AdaptivePowerRuntimeService.cs", """(?<![A-Za-z])ProcessRunner\.RunAsync\s*\(\s*["]powercfg\.exe["]\s*,\s*[$]?["]/(?:set|delete|duplicate|change)"""),
            ("WindowsActions.cs", """(?<![A-Za-z])ProcessRunner\.RunAsync\s*\(\s*["]powercfg\.exe["]\s*,\s*[$]?["]/(?:set|delete|duplicate|change)"""),
            ("PerformanceProfilesService.cs", """(?<![A-Za-z])ProcessRunner\.RunAsync\s*\(\s*["]powercfg\.exe["]\s*,\s*[$]?["]/(?:set|delete|duplicate|change)""")
        };

        var offenders = new List<string>();
        foreach (var check in checks)
        {
            string text = ReadSource(services, check.File);
            if (Regex.IsMatch(text, check.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(250)))
                offenders.Add(check.File);
        }

        Assert.True(offenders.Count == 0,
            "Privileged mutation bypasses broker: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Performance_smoke_warms_navigation_wrapper_without_warming_sections()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string smoke = ReadSource(root, "MainWindow.PerformanceSmoke.cs");

        Assert.Contains("for (int i = 0; i < 2; i++)", smoke, StringComparison.Ordinal);
        Assert.Contains("await ShowSectionAsync(\"activity\");", smoke, StringComparison.Ordinal);
        Assert.Contains("await ShowSectionAsync(\"overview\");", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("PERF_COMPONENT_COLD_BREAKDOWN", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("PERF_COMPONENT_MEASURE", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Lianli_action_callbacks_do_not_update_detached_navigation()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string lianLi = ReadSource(root, "MainWindow.LianLi.cs");

        Assert.Contains("int actionGeneration = _navigationGeneration;", lianLi, StringComparison.Ordinal);
        Assert.Contains("auto65.Parent == null", lianLi, StringComparison.Ordinal);
        Assert.Contains("aiSmart.Parent != null", lianLi, StringComparison.Ordinal);
        Assert.Contains("auto.Parent != null", lianLi, StringComparison.Ordinal);
        Assert.Contains("actionGeneration == _navigationGeneration", lianLi, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_catalog_and_tweak_ui_are_deferred_after_foreground_shell()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string main = ReadSource(root, "MainWindow.cs");
        string gpu = ReadSource(root, "MainWindow.GpuDeferred.cs");

        Assert.Contains("CompleteGpuTweaksAfterFirstFrameAsync", main, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(35, token)", gpu, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => CatalogService.GetTweaks(\"gpu\")", gpu, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", gpu, StringComparison.Ordinal);
    }

    [Fact]
    public void Components_shell_uses_local_definition_without_sidebar_catalog_entry()
    {
        string main = ReadSource(ProjectRoot, "src", "SpankyhoOptimizer", "MainWindow.cs");
        Assert.Contains("SectionDefinition sectionDefinition = key == \"overview\"", main, StringComparison.Ordinal);
        Assert.Contains(": key == \"components\"", main, StringComparison.Ordinal);
        Assert.Contains("? new SectionDefinition(", main, StringComparison.Ordinal);
        Assert.Contains(": CatalogService.GetSection(key);", main, StringComparison.Ordinal);
        Assert.Contains("Task Scheduler\" => \"Plánované úlohy", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_components_uses_dedicated_fast_path_before_profiler_router()
    {
        string main = ReadSource(ProjectRoot, "src", "SpankyhoOptimizer", "MainWindow.cs");
        int service = main.IndexOf("string.Equals(componentSub, \"Služby\"", StringComparison.Ordinal);
        int profiler = main.IndexOf("var componentPerf = Stopwatch.StartNew();", StringComparison.Ordinal);
        Assert.True(service >= 0 && profiler > service);
        Assert.Contains("_tweaks = EmptyComponentTweaks;", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Cold_startup_and_debloat_heavy_inventory_waits_for_stable_navigation()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string startup = ReadSource(root, "MainWindow.Startup.cs");
        string debloat = ReadSource(root, "MainWindow.Debloat.cs");

        Assert.Contains("HydrateStartupManagerAfterFirstFrameAsync", startup, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(150, token)", startup, StringComparison.Ordinal);
        Assert.Contains("LoadDebloatAfterFirstFrameAsync", debloat, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", debloat, StringComparison.Ordinal);
        Assert.Contains("CompleteDeferredServiceChromeAsync", ReadSource(root, "MainWindow.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Detached_navigation_cleanup_starts_after_first_frame()
    {
        string main = ReadSource(ProjectRoot, "src", "SpankyhoOptimizer", "MainWindow.cs");
        Assert.Contains("ScheduleDetachedNavigationHostCleanup(delayFirstFrame: true)", main, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(140)", main, StringComparison.Ordinal);
        Assert.Contains("DateTime.UtcNow - _lastNavigationUtc", main, StringComparison.Ordinal);
        Assert.Contains("Deferred detached host cleanup", main, StringComparison.Ordinal);
        string cleanupPolicy = ReadSource(ProjectRoot, "src", "SpankyhoOptimizer", "Services", "DetachedNavigationCleanupPolicy.cs");
        Assert.Contains("DetachedNavigationCleanupPolicy.RemoveCount(", main, StringComparison.Ordinal);
        Assert.Contains("int budget = pendingHosts >= 2 ? 16 : 8;", cleanupPolicy, StringComparison.Ordinal);
        Assert.Contains("Math.Min(childCount, budget)", cleanupPolicy, StringComparison.Ordinal);
        Assert.Contains("Keep detach O(1)", main, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (UIElement child in host.Children)\n            _detachedNavigationOwners[child] = host;", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Idle_trim_debloat_and_usb_deferred_paths_avoid_stale_or_eager_work()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string lifecycle = ReadSource(root, "MainWindow.Lifecycle.cs");
        string debloat = ReadSource(root, "MainWindow.Debloat.cs");
        string usb = ReadSource(root, "MainWindow.LegacyTweaks.cs");

        Assert.Contains("List<string>? expiredComponents = null;", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(x => x.Key).ToArray()", lifecycle, StringComparison.Ordinal);
        Assert.Contains("QueueDebloatRerender(CancellationToken token, int generation", debloat, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", debloat, StringComparison.Ordinal);
        Assert.Contains("HydrateUsbToolsAsync(shellHost, loadingSurface, loading, token, generation)", usb, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", usb, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_inner_batches_and_gpu_status_setters_are_generation_guarded()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string overview = ReadSource(root, "MainWindow.OverviewDashboard.cs");
        string gpu = ReadSource(root, "MainWindow.GpuDriverCenter.cs");

        Assert.Contains("generation != _navigationGeneration", overview, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_overviewGaugeHost, host)", overview, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_gpuDriverStatus, status)", gpu, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_gpuUpdateBadge, badge)", gpu, StringComparison.Ordinal);
        Assert.Contains("status.Parent != null", gpu, StringComparison.Ordinal);
        Assert.Contains("badge.Parent == null", gpu, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_virtual_tweak_cleanup_has_a_no_allocation_fast_path()
    {
        string virtualization = ReadSource(ProjectRoot, "src", "SpankyhoOptimizer", "MainWindow.TweakVirtualization.cs");
        Assert.Contains("if (!hasRows && !hasDeferred) return;", virtualization, StringComparison.Ordinal);
        Assert.Contains("_virtualTweakRowMapStore?.Count", virtualization, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_apps_and_overview_deferred_batches_are_bound_to_generation_and_hosts()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string gpu = ReadSource(root, "MainWindow.GpuDriverCenter.cs");
        string apps = ReadSource(root, "MainWindow.ExtraSections.cs");
        string overview = ReadSource(root, "MainWindow.OverviewDashboard.cs");

        Assert.Contains("ScheduleGpuStatsShell(gpuStatsHost, token, gpuGeneration)", gpu, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", gpu, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_gpuDriverCardsHost, cardsHost)", gpu, StringComparison.Ordinal);
        Assert.Contains("host.Parent == null", gpu, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", apps, StringComparison.Ordinal);
        Assert.Contains("host.Parent == null", apps, StringComparison.Ordinal);
        Assert.Contains("int generation = _navigationGeneration;", overview, StringComparison.Ordinal);
        Assert.Contains("host.Parent == null", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_overview_apps_and_storage_deferred_work_is_generation_guarded()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string main = ReadSource(root, "MainWindow.cs");
        string overview = ReadSource(root, "MainWindow.OverviewDashboard.cs");
        string apps = ReadSource(root, "MainWindow.ExtraSections.cs");
        string storage = ReadSource(root, "MainWindow.HardwareInsights.cs");

        Assert.Contains("CaptureStartupPageCache();\n            long captureMs", main, StringComparison.Ordinal);
        Assert.Contains("BuildWindowsModernizerStartupPanel(token);\n                CaptureStartupPageCache();", main, StringComparison.Ordinal);
        Assert.Contains("CompleteOverviewDashboardAsync(cachedSnapshotTask", overview, StringComparison.Ordinal);
        Assert.Contains("token, generation)", overview, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", apps, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_appsDeferredBody, body)", apps, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_storageHealthBody, body)", storage, StringComparison.Ordinal);
        Assert.Contains("body.Parent == null", storage, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_fixer_and_extensions_callbacks_are_bound_to_live_hosts()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string contextMenu = ReadSource(root, "MainWindow.ContextMenu.cs");
        string fixer = ReadSource(root, "MainWindow.FixerPageCache.cs");
        string extensions = ReadSource(root, "MainWindow.Extensions.cs");

        Assert.Contains("ReferenceEquals(_contextMenuUiCache, state)", contextMenu, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_fixerAppHealthHost, appHealthHost)", fixer, StringComparison.Ordinal);
        Assert.Contains("LoadAppHealthAsync(appHealthHost, token)", fixer, StringComparison.Ordinal);
        Assert.Contains("LoadExtensionsBodyAsync(body, token, generation)", extensions, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", extensions, StringComparison.Ordinal);
        Assert.Contains("body.Parent == null", extensions, StringComparison.Ordinal);
    }

    [Fact]
    public void Remaining_hardware_ui_callbacks_are_generation_guarded()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string network = ReadSource(root, "MainWindow.PowerNetwork.cs");
        string lianLi = ReadSource(root, "MainWindow.LianLi.cs");
        string gpu = ReadSource(root, "MainWindow.GpuDriverCenter.cs");
        string cursor = ReadSource(root, "MainWindow.Cursor.cs");

        Assert.Contains("LoadNetworkAdvancedControlsAsync(tcp, token, generation)", network, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_networkAdvancedBody, tcp)", network, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_lianLiStatus, status)", lianLi, StringComparison.Ordinal);
        Assert.Contains("HydrateGpuDriverCenterAsync(deferredDetection, detectionGeneration, token, navigationGeneration)", gpu, StringComparison.Ordinal);
        Assert.Contains("navigationGeneration != _navigationGeneration", gpu, StringComparison.Ordinal);
        Assert.Contains("activePackText.Parent == null", cursor, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_lianli_and_power_cache_callbacks_are_generation_guarded()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string network = ReadSource(root, "MainWindow.PowerNetwork.cs");
        string lianLi = ReadSource(root, "MainWindow.LianLi.cs");
        string powerCache = ReadSource(root, "MainWindow.PowerPageCache.cs");

        Assert.Contains("BuildMtuOptimizerPanelCore(host, generation)", network, StringComparison.Ordinal);
        Assert.Contains("RefreshMtuStatusAsync(status, generation)", network, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", network, StringComparison.Ordinal);
        Assert.Contains("RunFanTelemetryLoopAsync(token, generation)", lianLi, StringComparison.Ordinal);
        Assert.Contains("RunRgbPreviewLoopAsync(token, generation)", lianLi, StringComparison.Ordinal);
        Assert.Contains("RefreshLianLiStatusAsync(generation)", lianLi, StringComparison.Ordinal);
        Assert.Contains("LoadAiPowerControlsAsync(aiHost, generation, token)", powerCache, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_components_catalog_is_lazy_on_cold_navigation()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string main = ReadSource(root, "MainWindow.cs");
        string legacy = ReadSource(root, "MainWindow.ComponentsLegacy.cs");

        Assert.Contains("string.Equals(componentSub, \"Služby\"", main, StringComparison.Ordinal);
        Assert.Contains("_tweaks = EmptyComponentTweaks;", main, StringComparison.Ordinal);
        Assert.Contains("CatalogService.GetTweaks(\"components\", componentSub)", legacy, StringComparison.Ordinal);
        Assert.Contains("_tweaks = contextTweaks;", legacy, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_readback_and_power_guard_are_bound_to_current_navigation()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string components = ReadSource(root, "MainWindow.ComponentCenter.cs");
        string power = ReadSource(root, "MainWindow.PowerPlan.cs");

        Assert.Contains("LoadManagedServiceStatusAsync(statusHost, token, generation)", components, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", components, StringComparison.Ordinal);
        Assert.Contains("host.Parent == null", components, StringComparison.Ordinal);
        Assert.Contains("VerifyAndRepairPowerGuardAsync(snapshot, statusText, generation)", power, StringComparison.Ordinal);
        Assert.Contains("statusText.Parent == null", power, StringComparison.Ordinal);
        Assert.Contains("generation == _navigationGeneration", power, StringComparison.Ordinal);
    }

    [Fact]
    public void Power_and_component_deferred_refreshes_are_navigation_generation_guarded()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string power = ReadSource(root, "MainWindow.PowerPlan.cs");
        string components = ReadSource(root, "MainWindow.ComponentsPageCache.cs");

        Assert.Contains("LoadPowerSnapshotAndRefreshAsync(token, generation)", power, StringComparison.Ordinal);
        Assert.Contains("QueuePowerRecommendedBatch(token, generation)", power, StringComparison.Ordinal);
        Assert.Contains("QueuePowerAdvancedBatch(token, generation)", power, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", power, StringComparison.Ordinal);
        Assert.Contains("RefreshDriverCacheAfterAttachAsync(sub, generation, token)", components, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", components, StringComparison.Ordinal);
    }

    [Fact]
    public void Deferred_section_renderers_are_navigation_generation_guarded()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string networkPower = ReadSource(root, "MainWindow.PowerNetwork.cs");
        string lianLi = ReadSource(root, "MainWindow.LianLi.cs");

        Assert.Contains("generation != _navigationGeneration", networkPower, StringComparison.Ordinal);
        Assert.Contains("ContentStack.Children.Contains(heroHost)", networkPower, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_networkAdvancedBody, tcp)", networkPower, StringComparison.Ordinal);
        Assert.Contains("LoadLianLiRgbStudioDeferredAsync(rgbHost, present, profile, token, generation)", lianLi, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", lianLi, StringComparison.Ordinal);
        Assert.Contains("ContentStack.Children.Contains(host)", lianLi, StringComparison.Ordinal);
    }

    [Fact]
    public void Deferred_dashboard_updates_are_navigation_generation_guarded()
    {
        string root = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer");
        string network = ReadSource(root, "MainWindow.NetworkDashboard.cs");
        string overview = ReadSource(root, "MainWindow.OverviewDashboard.cs");

        Assert.Contains("int generation = _navigationGeneration;", network, StringComparison.Ordinal);
        Assert.Contains("generation != _navigationGeneration", network, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(_networkHeroState, heroState)", network, StringComparison.Ordinal);
        Assert.Contains("CompleteOverviewDriverAsync(driver, token, generation)", overview, StringComparison.Ordinal);
        Assert.Contains("generation == _navigationGeneration", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_detection_is_centralized_through_worker_privilege_policy()
    {
        string services = Path.Combine(ProjectRoot, "src", "SpankyhoOptimizer", "Services");
        string preflight = ReadSource(services, "TweakPreflightService.cs");
        string engine = ReadSource(services, "TweakEngine.cs");

        Assert.Contains("WorkerPrivilegePolicy.IsAdministrator", preflight, StringComparison.Ordinal);
        Assert.Contains("WorkerPrivilegePolicy.IsAdministrator", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsIdentity.GetCurrent", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsIdentity.GetCurrent", engine, StringComparison.Ordinal);
        Assert.Contains("Při použití změny bude vyžádáno potvrzení UAC.", preflight, StringComparison.Ordinal);
    }
}
