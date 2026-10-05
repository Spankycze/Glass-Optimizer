$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$p=Join-Path $PSScriptRoot 'src\SpankyhoOptimizer\MainWindow.AiSystemRuntime.cs'
$s=[IO.File]::ReadAllText($p)
function Replace-One([string]$old,[string]$new){
  $n=([regex]::Matches($script:s,[regex]::Escape($old))).Count
  if($n -ne 1){throw "Expected one anchor, got $n : $old"}
  $script:s=$script:s.Replace($old,$new)
}
Replace-One '            RenderAiRuntimeBody(host, snapshot, token);' @'
            var render = await RenderAiRuntimeBodyAsync(host, snapshot, generation, token);
            token.ThrowIfCancellationRequested();
            if (generation != _navigationGeneration || _section != "ai-runtime") return;
'@
Replace-One '$"AI_RUNTIME_BODY_DEFERRED dataMs={dataMs} uiMs={perf.ElapsedMilliseconds - dataMs} generation={generation} workerData=true"' '$"AI_RUNTIME_BODY_DEFERRED dataMs={dataMs} uiMs={render.UiMs} maxStageUiMs={render.MaxStageUiMs} renderWallMs={perf.ElapsedMilliseconds - dataMs} generation={generation} workerData=true batched=true"'
Replace-One @'
    private void RenderAiRuntimeBody(
        StackPanel host, AiRuntimePageSnapshot snapshot, CancellationToken token)
    {
        FanProfile profile = snapshot.Profile;
'@ @'
    private async Task<(long UiMs, long MaxStageUiMs)> RenderAiRuntimeBodyAsync(
        StackPanel host, AiRuntimePageSnapshot snapshot, int generation, CancellationToken token)
    {
        var stagePerf = System.Diagnostics.Stopwatch.StartNew();
        long totalUiMs = 0;
        long maxStageUiMs = 0;
        void FinishStage(string name)
        {
            long ms = stagePerf.ElapsedMilliseconds;
            totalUiMs += ms;
            maxStageUiMs = Math.Max(maxStageUiMs, ms);
            LogService.Info("performance", $"AI_RUNTIME_BODY_STAGE stage={name} uiMs={ms}");
        }
        async Task NextStageAsync(string completed)
        {
            FinishStage(completed);
            token.ThrowIfCancellationRequested();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => tcs.TrySetResult(true)))
                throw new OperationCanceledException("Runtime dispatcher is shutting down.", token);
            await tcs.Task.WaitAsync(token);
            ThrowIfNavigationStale(generation, token);
            stagePerf.Restart();
        }

        FanProfile profile = snapshot.Profile;
'@
Replace-One '        var runtimePanel = new StackPanel { Spacing = 10 };' @'
        await NextStageAsync("metrics");
        var runtimePanel = new StackPanel { Spacing = 10 };
'@
Replace-One '        var scope = new StackPanel { Spacing = 8 };' @'
        await NextStageAsync("runtime");
        var scope = new StackPanel { Spacing = 8 };
'@
Replace-One '        var cloud = new StackPanel { Spacing = 8 };' @'
        await NextStageAsync("maintenance");
        var cloud = new StackPanel { Spacing = 8 };
'@
Replace-One '        var self = new StackPanel { Spacing = 8 };' @'
        await NextStageAsync("cloud");
        var self = new StackPanel { Spacing = 8 };
'@
Replace-One '        var safety = new StackPanel { Spacing = 8 };' @'
        await NextStageAsync("self");
        var safety = new StackPanel { Spacing = 8 };
'@
Replace-One @'
        host.Children.Add(dashboard);
        host.Children.Add(actionsHost);

    }
'@ @'
        host.Children.Add(dashboard);
        host.Children.Add(actionsHost);
        FinishStage("safety-attach");
        return (totalUiMs, maxStageUiMs);
    }
'@
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($false))
Write-Output 'RUNTIME605_BATCH_PATCH_OK'
