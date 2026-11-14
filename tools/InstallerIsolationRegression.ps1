param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $Installer -PathType Leaf)){throw 'INSTALLER_ISOLATION_MISSING_SETUP'}

# Only create/delete temporary fixtures. A junction within the allowed TEMP
# directory must be rejected before the elevated installer extracts payload.
$fixture=Join-Path $env:TEMP ('SpankyInstallerIsolation-'+[guid]::NewGuid().ToString('N'))
$parent=Join-Path $fixture 'parent'
$target=Join-Path $fixture 'benign-target'
$link=Join-Path $parent 'escape-junction'
$attempt=Join-Path $link 'should-never-be-created'
$exit=-999
try {
    New-Item -ItemType Directory -Path $parent,$target -Force | Out-Null
    $junction=New-Item -ItemType Junction -Path $link -Target $target -ErrorAction Stop
    if(-not (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)){
        throw 'INSTALLER_ISOLATION_JUNCTION_NOT_CREATED'
    }
    $process=Start-Process -FilePath $Installer -ArgumentList @('--test-install',('"'+$attempt+'"')) -Wait -PassThru
    if($null -eq $process){throw 'INSTALLER_ISOLATION_PROCESS_NOT_STARTED'}
    $exit=$process.ExitCode
    if($exit -ne 32){throw ('INSTALLER_ISOLATION_JUNCTION_NOT_REJECTED exit='+$exit)}
    if(Test-Path -LiteralPath (Join-Path $target 'should-never-be-created')){
        throw 'INSTALLER_ISOLATION_JUNCTION_TARGET_MUTATED'
    }
    Write-Host 'INSTALLER_ISOLATION_JUNCTION_REJECTED_OK exit=32 targetUntouched=true testOnly=true'
}finally{
    # Remove the reparse directory itself, NEVER recursively through it.
    if(Test-Path -LiteralPath $link){
        Remove-Item -LiteralPath $link -Force -ErrorAction Stop
    }
    if(Test-Path -LiteralPath $fixture){
        Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction Stop
    }
}
