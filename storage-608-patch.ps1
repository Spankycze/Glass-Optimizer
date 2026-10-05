$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
function Replace-One([string]$s,[string]$old,[string]$new) {
 if($s.IndexOf($old) -lt 0 -or $s.IndexOf($old) -ne $s.LastIndexOf($old)){throw ('Missing/nonunique patch: '+$old)}
 return $s.Replace($old,$new)
}
function Replace-Range([string]$s,[string]$start,[string]$end,[string]$new) {
 $a=$s.IndexOf($start); $b=$s.IndexOf($end,$a+1)
 if($a -lt 0 -or $b -lt 0){throw ('Missing range: '+$start)}
 return $s.Substring(0,$a)+$new+$s.Substring($b)
}
function Read-Source([string]$p){return [IO.File]::ReadAllText((Join-Path $PSScriptRoot $p))}
function Save-Source([string]$p,[string]$s){[IO.File]::WriteAllText((Join-Path $PSScriptRoot $p),$s,[Text.UTF8Encoding]::new($true))}
$p='src\SpankyhoOptimizer\MainWindow.StoragePageCache.cs'
$s=Read-Source $p
foreach($name in 'StaticShell','SnapshotShell','Tweak'){
 $s=$s.Replace('_storage'+$name+'Queued','_storage'+$name+'QueuedGeneration')
 $s=$s.Replace('private bool _storage'+$name+'QueuedGeneration;','private int _storage'+$name+'QueuedGeneration;')
 $s=$s.Replace('_storage'+$name+'QueuedGeneration = false;','_storage'+$name+'QueuedGeneration = 0;')
}
$s=Replace-One $s '    private int _storageTweakQueuedGeneration;' @'
    private int _storageTweakQueuedGeneration;
    private int _storageTweakNextIndex;
'@
$s=Replace-Range $s '    private void ScheduleStorageStaticShell(' '    private void InvalidateStorageStaticShellCache()' @'
    private void ScheduleStorageStaticShell(StackPanel host, CancellationToken token)
    {
        int generation = _navigationGeneration;
        if (_storageStaticShellQueuedGeneration == generation || host.Children.Count > 0)
            return;

        _storageStaticShellQueuedGeneration = generation;
        void ReleaseQueueTicket()
        {
            if (_storageStaticShellQueuedGeneration == generation)
                _storageStaticShellQueuedGeneration = 0;
        }
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseQueueTicket();
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageStaticShellHost, host) ||
                host.Children.Count > 0)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            EnsureStorageStaticShell();
            long buildMs = sw.ElapsedMilliseconds;
            host.Children.Add(_storageStaticHero!);
            long heroMs = sw.ElapsedMilliseconds - buildMs;
            host.Children.Add(_storageStaticMetrics!);
            long metricsMs = sw.ElapsedMilliseconds - buildMs - heroMs;
            SchedulePageScrollPolicyRefresh();
            LogService.Info("performance",
                $"STORAGE_STATIC_SHELL_DEFERRED buildMs={buildMs} heroAttachMs={heroMs} metricsAttachMs={metricsMs} totalMs={sw.ElapsedMilliseconds}");
        }))
            ReleaseQueueTicket();
    }

'@
$s=Replace-One $s '        if (_storageTweakHost is { Children.Count: 0 }) ScheduleStorageTweakCards(token);' @'
        if (_storageTweakHost != null) ScheduleStorageTweakCards(token);
        ScheduleStorageTools(token);
'@
$s=Replace-Range $s '    private void ScheduleStorageTweakCards(' '    private void CaptureStoragePageCache()' @'
    private void ScheduleStorageTweakCards(CancellationToken token)
    {
        int generation = _navigationGeneration;
        if (_storageTweakQueuedGeneration == generation || _storageTweakHost == null) return;

        var host = _storageTweakHost;
        var rows = _tweaks
            .GroupBy(t => t.Group)
            .SelectMany(group => group.Select((tweak, index) => new
            {
                Group = group.Key,
                GroupCount = group.Count(),
                Tweak = tweak,
                FirstInGroup = index == 0
            }))
            .ToArray();
        if (_storageTweakNextIndex >= rows.Length) return;

        _storageTweakQueuedGeneration = generation;
        long totalMs = 0;
        void ReleaseQueueTicket()
        {
            if (_storageTweakQueuedGeneration == generation)
                _storageTweakQueuedGeneration = 0;
        }
        void QueueNext(int index)
        {
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (token.IsCancellationRequested || generation != _navigationGeneration ||
                    _section != "storage" || !ReferenceEquals(_storageTweakHost, host))
                {
                    ReleaseQueueTicket();
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var row = rows[index];
                if (row.FirstInGroup)
                    host.Children.Add(CreateClassicTweakGroupHeader(row.Group, row.GroupCount));
                host.Children.Add(CreateClassicTweakCard(row.Tweak));
                _storageTweakNextIndex = index + 1;
                long batchMs = sw.ElapsedMilliseconds;
                totalMs += batchMs;
                LogService.Info("performance",
                    $"STORAGE_TWEAK_BATCH index={index} ms={batchMs} completed={_storageTweakNextIndex}/{rows.Length}");
                if (_storageTweakNextIndex < rows.Length)
                    QueueNext(_storageTweakNextIndex);
                else
                {
                    ReleaseQueueTicket();
                    SchedulePageScrollPolicyRefresh();
                    LogService.Info("performance",
                        $"STORAGE_TWEAKS_DEFERRED totalMs={totalMs} maxPerTick=batched count={rows.Length}");
                }
            }))
                ReleaseQueueTicket();
        }
        QueueNext(_storageTweakNextIndex);
    }

'@
$s=Replace-One $s @'
        _storageTweakHost = null;
        _storageTweakQueuedGeneration = 0;
'@ @'
        _storageTweakHost = null;
        _storageTweakQueuedGeneration = 0;
        _storageTweakNextIndex = 0;
'@
$s=$s.Replace('_storageToolsQueued = false;','_storageToolsQueuedGeneration = 0;')
Save-Source $p $s

$p='src\SpankyhoOptimizer\MainWindow.SystemTools.cs'
$s=Read-Source $p
$s=Replace-One $s @'
        if (_storageSnapshotShellQueued || _storageSnapshotShellHost == null || _storageSnapshotShellHost.Children.Count > 0) return;
        _storageSnapshotShellQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _storageSnapshotShellQueued = false;
            if (token.IsCancellationRequested || _section != "storage" || _storageSnapshotShellHost == null || _storageSnapshotShellHost.Children.Count > 0) return;
'@ @'
        int generation = _navigationGeneration;
        var host = _storageSnapshotShellHost;
        if (_storageSnapshotShellQueuedGeneration == generation || host == null || host.Children.Count > 0) return;
        _storageSnapshotShellQueuedGeneration = generation;
        void ReleaseQueueTicket()
        {
            if (_storageSnapshotShellQueuedGeneration == generation)
                _storageSnapshotShellQueuedGeneration = 0;
        }
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseQueueTicket();
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || !ReferenceEquals(_storageSnapshotShellHost, host) ||
                host.Children.Count > 0) return;
'@
$s=Replace-One $s '            _storageSnapshotShellHost.Children.Add(BuildSectionSurface(' '            host.Children.Add(BuildSectionSurface('
$s=Replace-One $s @'
            SafeTaskService.Forget(LoadStorageSnapshotAsync(stack, token), "storage", "DriveInfo snapshot");
        });
'@ @'
            SafeTaskService.Forget(LoadStorageSnapshotAsync(stack, token), "storage", "DriveInfo snapshot");
            SchedulePageScrollPolicyRefresh();
        }))
            ReleaseQueueTicket();
'@
# Prevent a result already queued on the dispatcher from modifying a superseded load.
$s=Replace-One $s @'
                if (_section != "storage") return;
                host.Children.Clear();
'@ @'
                if (token.IsCancellationRequested || _section != "storage") return;
                host.Children.Clear();
'@
Save-Source $p $s

$p='src\SpankyhoOptimizer\MainWindow.LegacyTweaks.cs'
$s=Read-Source $p
$s=Replace-One $s '    private bool _storageToolsQueued;' '    private int _storageToolsQueuedGeneration;'
$s=Replace-Range $s '    private void ScheduleStorageTools(' '    private void BuildStorageTools()' @'
    private void ScheduleStorageTools(CancellationToken token)
    {
        if (_storageMaintenanceCard != null)
        {
            if (!ContentStack.Children.Contains(_storageMaintenanceCard))
                AttachCachedElement(_storageMaintenanceCard);
            return;
        }
        int generation = _navigationGeneration;
        if (_storageToolsQueuedGeneration == generation) return;
        _storageToolsQueuedGeneration = generation;
        void ReleaseQueueTicket()
        {
            if (_storageToolsQueuedGeneration == generation)
                _storageToolsQueuedGeneration = 0;
        }
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ReleaseQueueTicket();
            if (token.IsCancellationRequested || generation != _navigationGeneration ||
                _section != "storage" || _storageMaintenanceCard != null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BuildStorageTools();
            if (_storagePageCache != null && _storageMaintenanceCard != null &&
                !_storagePageCache.Contains(_storageMaintenanceCard))
                _storagePageCache.Add(_storageMaintenanceCard);
            SchedulePageScrollPolicyRefresh();
            LogService.Info("performance", $"STORAGE_TOOLS_DEFERRED ms={sw.ElapsedMilliseconds}");
        }))
            ReleaseQueueTicket();
    }

'@
Save-Source $p $s
Write-Output 'STORAGE608_PATCH_OK'
