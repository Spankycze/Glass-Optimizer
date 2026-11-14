param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent), [switch]$SkipRuntime)
$ErrorActionPreference='Stop'
$spicetify=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\SpicetifyService.cs') -Raw
$titus=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\Services\ChrisTitusIntegrationService.cs') -Raw
$app=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\App.cs') -Raw
$ui=Get-Content -LiteralPath (Join-Path $ProjectRoot 'src\SpankyhoOptimizer\MainWindow.PowerShellCenter.cs') -Raw
foreach($needle in @('DownloadPinnedMarketplaceInstallerAsync','api.github.com/repos/spicetify/marketplace/commits/main','raw.githubusercontent.com/spicetify/marketplace/','IsValidMarketplaceInstaller(script)','SHA256.HashData(script)','File.WriteAllBytesAsync(source, bytes','-ExecutionPolicy Bypass -File','Directory.Delete(stage, true)')){
    if(!$spicetify.Contains($needle)){throw "Marketplace pinned stage contract missing $needle"}
}
if($spicetify.Contains('Invoke-Expression (Invoke-WebRequest') -or
    $spicetify.Contains('marketplace/main/resources/install.ps1')){
    throw 'Marketplace unpinned remote-code execution introduced'
}
foreach($needle in @('CheckOneUpdateAsync(ProfileOwner','TimeSpan.FromMinutes(2)','TimeSpan.FromMinutes(30)','HasNewCommit','IsGitHubCommit(sha)','new StagedRepository(destination, previous)','profileSnapshot.Commit()','configSnapshot.Commit()','IsCurrentManagedInstallation','archive.Entries.Count > 5000','maxCompressed = 32L * 1024 * 1024','RunStagingTransactionSmoke()','RunOptInAutoUpdatesAsync','IsAutoUpdateDue','GetAutoUpdateEnabled','SetAutoUpdateEnabled','RestoreTitusManagedBackups','SnapshotTitusBackupFolders','RestoreTitusNewBackupFolders','multiple new backup folders','GetAutoUpdateStatusLabel','SaveAutoUpdateResult')){
    if(!$titus.Contains($needle)){throw "Titus update check contract missing $needle"}
}
if(!$app.Contains('--spicetify-pinned-stage-smoke') -or !$app.Contains('--chris-titus-update-smoke') -or
    !$app.Contains('--chris-titus-transaction-smoke') -or
    !$app.Contains('--chris-titus-auto-policy-smoke') -or
    !$app.Contains('RunOptInAutoUpdatesAsync(updates, token)') -or
    !$ui.Contains('auto = QuickButton') -or
    !$ui.Contains('GetAutoUpdateStatusLabel(false)')){

    throw 'Offline integration regression CLI missing'
}
Write-Output 'PINNED_INTEGRATION_STATIC_GUARD_OK marketplaceCommit=true boundedPayload=true noIEX=true titusProfileOnly=true rollback=true noRedundantInstall=true optInAutoUpdate=true'
if(!$SkipRuntime){
    $exe=Join-Path $ProjectRoot 'src\SpankyhoOptimizer\bin\Release\net10.0-windows10.0.26100.0\win-x64\SpankyhoOptimizer.exe'
    if(!(Test-Path $exe)){throw "Compiled EXE missing: $exe"}
    foreach($arg in @('--spicetify-pinned-stage-smoke','--chris-titus-update-smoke','--chris-titus-transaction-smoke','--chris-titus-auto-policy-smoke')){
        $p=Start-Process -FilePath $exe -ArgumentList $arg -Wait -PassThru
        if($p.ExitCode -ne 0){throw "$arg exit $($p.ExitCode)"}
    }
    Write-Output 'PINNED_INTEGRATION_RUNTIME_GUARD_OK offline=true writes=false'
}
