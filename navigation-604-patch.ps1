$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$p='src\SpankyhoOptimizer\MainWindow.AiSystemRuntime.cs'
$s=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $p))
$start=$s.IndexOf('        var dataPerf = System.Diagnostics.Stopwatch.StartNew();')
$end=$s.IndexOf('        var heroActions =', $start)
if($start -lt 0 -or $end -lt 0){throw 'Data block anchor missing'}
$s=$s.Remove($start,$end-$start)
$start=$s.IndexOf('        string agentState =')
$end=$s.IndexOf('    private async Task LoadAiVoiceCloudDeferredAsync', $start)
if($start -lt 0 -or $end -lt 0){throw 'Body anchors missing'}
$old=$s.Substring($start,$end-$start)
$body=$old.Replace('ContentStack.Children.Add(', 'host.Children.Add(')
$tail="        return Task.CompletedTask;"+[Environment]::NewLine+"    }"
if(-not $body.Contains($tail)){
  $tail="        return Task.CompletedTask;"+[char]10+"    }"
}
if(-not $body.Contains($tail)){throw 'Body return anchor missing'}
$body=$body.Replace($tail, "    }")
$replacement=@'
        var bodyHost = new StackPanel { Spacing = ContentStack.Spacing };
        bodyHost.Children.Add(new TextBlock
        {
            Text = "Načítám stav AI runtime…",
            FontSize = 12,
            Foreground = TextSecondaryBrush()
        });
        ContentStack.Children.Add(bodyHost);
        SafeTaskService.Forget(
            LoadAiRuntimeBodyDeferredAsync(bodyHost, _navigationGeneration, token),
            "ai-runtime",
            "Deferred runtime status");
        return Task.CompletedTask;
    }

    private sealed record AiRuntimePageSnapshot(
        FanProfile Profile,
        FanAgentHealth? FanHealth,
        AiSystemRuntimeConfig Config,
        AiSystemRuntimeHealth? SystemHealth,
        AiCloudProfileStatus CloudStatus,
        GoogleDriveAuthStatus GoogleAuth,
        AiSelfOptimizationHealth? SelfHealth);

    private static AiRuntimePageSnapshot ReadAiRuntimePageSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FanProfile profile = FanProfileService.Load();
        FanAgentHealth? fanHealth = FanProfileService.ReadHealthStatus();
        token.ThrowIfCancellationRequested();
        AiSystemRuntimeConfig config = AiSystemResourceManagerService.LoadConfig();
        AiSystemRuntimeHealth? systemHealth = AiSystemResourceManagerService.ReadHealth();
        token.ThrowIfCancellationRequested();
        AiCloudProfileStatus cloud = AiCloudProfileService.ReadStatus();
        GoogleDriveAuthStatus google = GoogleDriveOAuthService.ReadStatus();
        AiSelfOptimizationHealth? self = AiSelfOptimizationService.ReadHealth();
        token.ThrowIfCancellationRequested();
        return new(profile, fanHealth, config, systemHealth, cloud, google, self);
    }

    private async Task LoadAiRuntimeBodyDeferredAsync(
        StackPanel host, int generation, CancellationToken token)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Profile IO and first-use JSON/JIT work must never occupy the dispatcher.
            AiRuntimePageSnapshot snapshot = await Task.Run(
                () => ReadAiRuntimePageSnapshot(token), token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime")
                return;

            long dataMs = perf.ElapsedMilliseconds;
            host.Children.Clear();
            RenderAiRuntimeBody(host, snapshot, token);
            SchedulePageScrollPolicyRefresh();
            LogService.Info("performance",
                $"AI_RUNTIME_BODY_DEFERRED dataMs={dataMs} uiMs={perf.ElapsedMilliseconds - dataMs} generation={generation} workerData=true");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogService.Error("ai-runtime", "Runtime status load failed", ex);
            if (token.IsCancellationRequested || generation != _navigationGeneration || _section != "ai-runtime")
                return;
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Text = "Stav AI runtime se nepodařilo načíst.",
                Foreground = WarningBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            host.Children.Add(QuickButton("Načíst znovu", () =>
                LoadAiRuntimeBodyDeferredAsync(host, generation, token)));
        }
    }

    private void RenderAiRuntimeBody(
        StackPanel host, AiRuntimePageSnapshot snapshot, CancellationToken token)
    {
        FanProfile profile = snapshot.Profile;
        FanAgentHealth? health = snapshot.FanHealth;
        AiSystemRuntimeConfig systemConfig = snapshot.Config;
        AiSystemRuntimeHealth? systemHealth = snapshot.SystemHealth;
        AiCloudProfileStatus cloudStatus = snapshot.CloudStatus;
        GoogleDriveAuthStatus googleAuth = snapshot.GoogleAuth;
        AiSelfOptimizationHealth? selfHealth = snapshot.SelfHealth;
        bool fresh = health != null &&
            DateTime.UtcNow - health.UpdatedUtc <= TimeSpan.FromSeconds(10);
        bool systemFresh = systemHealth != null &&
            DateTime.UtcNow - systemHealth.UpdatedUtc <= TimeSpan.FromMinutes(5);

'@
$s=$s.Remove($start,$end-$start).Insert($start,$replacement+[Environment]::NewLine+$body)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $p),$s,[Text.UTF8Encoding]::new($false))
Write-Output 'AI_RUNTIME_WORKER_PATCH_OK'
