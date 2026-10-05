$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
function Replace-One([string]$text,[string]$old,[string]$new) {
 if (-not $text.Contains($old)) {throw ('Missing patch: '+$old)}
 if ($text.IndexOf($old) -ne $text.LastIndexOf($old)) {throw ('Nonunique patch: '+$old)}
 return $text.Replace($old,$new)
}
$p='src\SpankyhoOptimizer\MainWindow.cs'
$s=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $p))
$s=Replace-One $s '            BuildStorageActions();' '            BuildStorageActions(token);'
$s=Replace-One $s '    private Border? _storageToolsCard;' @'
    private Border? _storageToolsCard;
    private StackPanel? _storageActionsHost;
    private StackPanel? _storageActionsQueuedHost;
'@
$s=Replace-One $s @'
    private void BuildStorageActions()
    {
        ContentStack.Children.Add(EnsureStorageActionsCard());
    }
'@ @'
    private void BuildStorageActions(CancellationToken token)
    {
        _storageActionsHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        ContentStack.Children.Add(_storageActionsHost);
        ScheduleStorageActions(_storageActionsHost, token);
    }

    private void ScheduleStorageActions(StackPanel host, CancellationToken token)
    {
        if (ReferenceEquals(_storageActionsQueuedHost, host) || host.Children.Count > 0) return;
        _storageActionsQueuedHost = host;
        int generation = _navigationGeneration;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(_storageActionsQueuedHost, host))
                _storageActionsQueuedHost = null;
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageActionsHost, host) ||
                host.Children.Count > 0) return;

            var sw = Stopwatch.StartNew();
            host.Children.Add(EnsureStorageActionsCard());
            SchedulePageScrollPolicyRefresh();
            LogService.Info("performance", $"STORAGE_ACTIONS_DEFERRED ms={sw.ElapsedMilliseconds} generation={generation}");
        }))
        {
            if (ReferenceEquals(_storageActionsQueuedHost, host))
                _storageActionsQueuedHost = null;
        }
    }
'@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $p),$s,[Text.UTF8Encoding]::new($true))
$p='src\SpankyhoOptimizer\MainWindow.StoragePageCache.cs'
$s=[IO.File]::ReadAllText((Join-Path $PSScriptRoot $p))
$s=Replace-One $s '        if (_storageSnapshotShellHost is { Children.Count: 0 }) ScheduleStorageSnapshotShell(token);' @'
        if (_storageSnapshotShellHost is { Children.Count: 0 }) ScheduleStorageSnapshotShell(token);
        if (_storageActionsHost is { Children.Count: 0 } actionsHost) ScheduleStorageActions(actionsHost, token);
'@
$s=Replace-One $s '        _storageToolsCard = null;' @'
        _storageToolsCard = null;
        _storageActionsHost = null;
        _storageActionsQueuedHost = null;
'@
$s=Replace-One $s '             (_storageToolsRow == null ||' @'
             (_storageToolsRow == null ||
              _storageActionsHost?.Children.Count != 1 ||
'@
$s=Replace-One $s @'
        bool toolsCardReattached = toolsCard != null &&
            ContentStack.Children.Cast<UIElement>().Any(x => ReferenceEquals(x, toolsCard));
'@ @'
        bool toolsCardReattached = toolsCard != null && _storageActionsHost != null &&
            ContentStack.Children.Contains(_storageActionsHost) &&
            _storageActionsHost.Children.Contains(toolsCard);
'@
$s=Replace-One $s '        var row = _storageToolsRow;' @'
        if (_storageActionsHost?.Children.Count != 1 || _storageToolsCard == null ||
            !_storageActionsHost.Children.Contains(_storageToolsCard))
            throw new InvalidOperationException("Storage deferred actions did not materialize exactly once.");

        var row = _storageToolsRow;
'@
$s=Replace-One $s @'
        LogService.Info("performance",
            $"STORAGE_TOOLS_CACHE_SMOKE_OK foregroundMs={foregroundMs} cacheAttachMs={cacheAttachMs} navigationMs={navigationMs} tweaks={actualTweaks} reattached=true");
'@ @'
        // Supersede a queued batch before the dispatcher can run it, then create
        // a new host. The old callback must neither populate it nor clear its ticket.
        await ShowSectionAsync("overview");
        InvalidateStoragePageCache();
        await ShowSectionAsync("storage");
        var staleActionsHost = _storageActionsHost!;
        await ShowSectionAsync("overview");
        InvalidateStoragePageCache();
        await ShowSectionAsync("storage");
        var freshActionsHost = _storageActionsHost!;
        for (int i = 0; i < 80 && freshActionsHost.Children.Count == 0; i++)
            await Task.Delay(25);
        if (staleActionsHost.Children.Count != 0 || freshActionsHost.Children.Count != 1 ||
            !ReferenceEquals(_storageActionsHost, freshActionsHost) ||
            _storageToolsCard == null || !freshActionsHost.Children.Contains(_storageToolsCard))
            throw new InvalidOperationException("Storage actions stale-load guard or fresh scheduling failed.");

        LogService.Info("performance",
            $"STORAGE_TOOLS_CACHE_SMOKE_OK foregroundMs={foregroundMs} cacheAttachMs={cacheAttachMs} navigationMs={navigationMs} tweaks={actualTweaks} reattached=true deferredActions=true staleActionsGuard=true");
'@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $p),$s,[Text.UTF8Encoding]::new($true))
Write-Output 'STORAGE606_PATCH_OK'
