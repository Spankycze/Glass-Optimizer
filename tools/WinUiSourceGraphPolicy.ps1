# Build-only, source-only WinUI/XAML graph. No WinUI process or Windows settings touched.
# Dot-source this from build-release.ps1. -SelfTest is a small isolated fixture.
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'

function Get-WinUiGraphSourceFiles([string]$projectDirectory) {
    if(-not(Test-Path -LiteralPath $projectDirectory -PathType Container)){
        throw 'WINUI_GRAPH_PROJECT_MISSING'
    }
    return @(Get-ChildItem -LiteralPath $projectDirectory -Recurse -File -Include *.xaml,*.csproj,*.resw |
        Where-Object {
            $_.FullName -notmatch '(?i)[\\/](?:obj|bin)[\\/]'
        } | Sort-Object FullName)
}
function Test-WinUiGraphRebuild(
    [string]$savedToken,
    [string]$currentHash,
    [System.IO.FileInfo[]]$sourceFiles,
    [string]$xbfPath
) {
    if([string]::IsNullOrWhiteSpace($currentHash) -or $sourceFiles.Count -eq 0) {
        throw 'WINUI_GRAPH_INPUTS_INVALID'
    }
    $currentToken='source-v2:'+ $currentHash
    if(-not(Test-Path -LiteralPath $xbfPath -PathType Leaf)){return $true}
    if($savedToken.StartsWith('source-v2:',[StringComparison]::Ordinal)) {
        return -not [string]::Equals($savedToken,$currentToken,[StringComparison]::Ordinal)
    }
    # Legacy fingerprints contained generated obj App.xaml. Migrate without
    # destroying a valid XBF only when every real input is older than that XBF.
    $xbfTime=(Get-Item -LiteralPath $xbfPath).LastWriteTimeUtc
    foreach($file in $sourceFiles) {
        # FileInfo caches metadata; refresh it before the migration comparison.
        $file.Refresh()
        if(-not $file.Exists -or $file.LastWriteTimeUtc -gt $xbfTime){return $true}
    }
    return $false
}
function Test-WinUiReleaseReuseSafe(
    [bool]$graphChanged,
    [System.IO.FileInfo[]]$sourceFiles,
    [string]$xbfPath,
    [string]$intermediateAssemblyPath,
    [string]$targetsPath
) {
    # Fail closed: never skip XAML precompile when the input graph changed,
    # any XAML-generated artifact disappeared, or real XAML source is unsafe.
    if($graphChanged -or $sourceFiles.Count -eq 0){return $false}
    # This fast path was proven for App.xaml only. Any additional XAML
    # page/control must use the standard generated-code path until separately
    # validated, regardless of unrecognized event or markup extensions.
    $xamlInputs=@($sourceFiles | Where-Object { $_.Extension -ieq '.xaml' })
    if($xamlInputs.Count -ne 1 -or $xamlInputs[0].Name -cne 'App.xaml'){return $false}
    foreach($p in @($xbfPath,$intermediateAssemblyPath,$targetsPath)){
        if(-not(Test-Path -LiteralPath $p -PathType Leaf)){return $false}
        if((Get-Item -LiteralPath $p).Length -eq 0){return $false}
    }
    $compiledAt=(Get-Item -LiteralPath $intermediateAssemblyPath).LastWriteTimeUtc
    $xbfAt=(Get-Item -LiteralPath $xbfPath).LastWriteTimeUtc
    $xamlCount=0
    foreach($file in $sourceFiles){
        $file.Refresh()
        if(-not $file.Exists){return $false}
        if($file.Extension -ne '.xaml'){continue}
        $xamlCount++
        if($file.LastWriteTimeUtc -gt $compiledAt -or $file.LastWriteTimeUtc -gt $xbfAt){return $false}
        $content=[IO.File]::ReadAllText($file.FullName)
        # Generated C# can reference application types or event handlers.
        # Reuse is permitted only when XAML has no source-bound code.
        if($content -match '\bx:Bind\b|\bx:DataType\s*=|xmlns:\w+\s*=\s*["'']\s*using:SpankyhoOptimizer(?:\.|["''])'){return $false}
        if($content -match '\b(Click|Loaded|Unloaded|SelectionChanged|Toggled|Checked|Unchecked|TextChanged|KeyDown|KeyUp|PointerPressed|PointerReleased|PointerMoved|Tapped|DoubleTapped|RightTapped|Holding|SizeChanged|Navigated|Submitted|ItemClick)\s*=\s*["'']'){return $false}
    }
    if($xamlCount -eq 0){return $false}
    $targetText=Get-Content -LiteralPath $targetsPath -Raw -ErrorAction Stop
    if(-not $targetText.Contains('BeforeTargets="XamlPreCompile"') -or
       -not $targetText.Contains('<SkipCompilerExecution>true</SkipCompilerExecution>')){return $false}
    return $true
}
function Test-WinUiGeneratedArtifactIntegrity(
    [string]$objDirectory,
    [string]$publishDirectory
) {
    $required=@('App.xbf','App.g.cs','App.g.i.cs','XamlTypeInfo.g.cs')
    foreach($name in $required) {
        $p=Join-Path $objDirectory $name
        if(-not(Test-Path -LiteralPath $p -PathType Leaf)){return $false}
        if((Get-Item -LiteralPath $p).Length -le 0){return $false}
    }
    $publishedXbf=Join-Path $publishDirectory 'App.xbf'
    if(-not(Test-Path -LiteralPath $publishedXbf -PathType Leaf)){return $false}
    if((Get-Item -LiteralPath $publishedXbf).Length -le 0){return $false}
    if(-not([IO.File]::ReadAllText((Join-Path $objDirectory 'App.g.i.cs')).Contains('void InitializeComponent()'))) {
        return $false
    }
    # Prevent stale XBF silently making it into the installer when the
    # generated objects and copied publish payload disagree.
    $xbfHash=(Get-FileHash -LiteralPath (Join-Path $objDirectory 'App.xbf') -Algorithm SHA256).Hash
    $pubHash=(Get-FileHash -LiteralPath $publishedXbf -Algorithm SHA256).Hash
    return [string]::Equals($xbfHash,$pubHash,[StringComparison]::Ordinal)
}
function Repair-WinUiPublishedXbf(
    [string]$objDirectory,
    [string]$binDirectory,
    [string]$publishDirectory
) {
    $obj=Join-Path $objDirectory 'App.xbf'
    $bin=Join-Path $binDirectory 'App.xbf'
    $pub=Join-Path $publishDirectory 'App.xbf'
    foreach($source in @($obj,$bin)){
        if(-not(Test-Path -LiteralPath $source -PathType Leaf) -or
           (Get-Item -LiteralPath $source).Length -le 0){
            throw ('WINUI_XBF_RECOVERY_SOURCE_MISSING '+$source)
        }
    }
    $expected=(Get-FileHash -LiteralPath $obj -Algorithm SHA256).Hash
    $actualBin=(Get-FileHash -LiteralPath $bin -Algorithm SHA256).Hash
    if($expected -ne $actualBin){throw 'WINUI_XBF_RECOVERY_BIN_MISMATCH'}
    if(Test-Path -LiteralPath $pub -PathType Leaf){
        if((Get-FileHash -LiteralPath $pub -Algorithm SHA256).Hash -ne $expected){
            throw 'WINUI_XBF_RECOVERY_PUBLISH_MISMATCH'
        }
        return 'verified-existing'
    }
    [IO.Directory]::CreateDirectory($publishDirectory)|Out-Null
    Copy-Item -LiteralPath $bin -Destination $pub -ErrorAction Stop
    if((Get-FileHash -LiteralPath $pub -Algorithm SHA256).Hash -ne $expected){
        throw 'WINUI_XBF_RECOVERY_COPY_MISMATCH'
    }
    return 'copied-verified-build-output'
}
if($SelfTest) {
    $fixture=Join-Path ([IO.Path]::GetTempPath()) ('spanky-xaml-policy-'+[guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory((Join-Path $fixture 'obj\Release')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $fixture 'bin\Debug')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture 'App.xaml'),'<App />')
        [IO.File]::WriteAllText((Join-Path $fixture 'Gui.csproj'),'<Project />')
        [IO.File]::WriteAllText((Join-Path $fixture 'obj\Release\App.xaml'),'generated')
        [IO.File]::WriteAllText((Join-Path $fixture 'bin\Debug\App.xaml'),'generated')
        $source=@(Get-WinUiGraphSourceFiles $fixture)
        if($source.Count -ne 2){throw ('WINUI_GRAPH_SOURCE_COUNT_FAIL '+$source.Count)}
        $before=@($source | ForEach-Object {(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}) -join '|'
        [IO.File]::WriteAllText((Join-Path $fixture 'obj\Release\App.xaml'),'generated again')
        $after=@(Get-WinUiGraphSourceFiles $fixture | ForEach-Object {(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}) -join '|'
        if($before -ne $after){throw 'WINUI_GRAPH_GENERATED_INPUT_POLLUTION'}
        $xbf=Join-Path $fixture 'obj\Release\App.xbf'
        [IO.File]::WriteAllText($xbf,'compiled')
        foreach($file in $source){[IO.File]::SetLastWriteTimeUtc($file.FullName,[DateTime]::UtcNow.AddDays(-2))}
        [IO.File]::SetLastWriteTimeUtc($xbf,[DateTime]::UtcNow.AddDays(-1))
        if(Test-WinUiGraphRebuild 'legacyhash' 'newhash' $source $xbf){throw 'WINUI_GRAPH_LEGACY_MIGRATION_FAIL'}
        if(Test-WinUiGraphRebuild 'source-v2:newhash' 'newhash' $source $xbf){throw 'WINUI_GRAPH_STABLE_FAIL'}
        $lib=Join-Path $fixture 'obj\Release\intermediatexaml\Gui.dll'
        [IO.Directory]::CreateDirectory((Split-Path -Parent $lib)) | Out-Null
        [IO.File]::WriteAllText($lib,'cached-assembly')
        [IO.File]::SetLastWriteTimeUtc($lib,[DateTime]::UtcNow.AddHours(-1))
        $targets=Join-Path $fixture 'reuse.targets'
        [IO.File]::WriteAllText($targets,'<Target BeforeTargets="XamlPreCompile"><SkipCompilerExecution>true</SkipCompilerExecution></Target>')
        if(-not(Test-WinUiReleaseReuseSafe $false $source $xbf $lib $targets)){throw 'WINUI_REUSE_ELIGIBLE_FALSE_NEGATIVE'}
        $extraXaml=Join-Path $fixture 'AddedPage.xaml'
        [IO.File]::WriteAllText($extraXaml,'<Page />')
        [IO.File]::SetLastWriteTimeUtc($extraXaml,[DateTime]::UtcNow.AddDays(-2))
        if(Test-WinUiReleaseReuseSafe $false @(Get-WinUiGraphSourceFiles $fixture) $xbf $lib $targets){throw 'WINUI_REUSE_NEW_XAML_PAGE_NOT_BLOCKED'}
        Remove-Item -LiteralPath $extraXaml -Force
        if(Test-WinUiReleaseReuseSafe $true $source $xbf $lib $targets){throw 'WINUI_REUSE_GRAPH_CHANGED_UNSAFE'}
        [IO.File]::WriteAllText($source[0].FullName,'<App x:Bind="Unsafe"/>')
        [IO.File]::SetLastWriteTimeUtc($source[0].FullName,[DateTime]::UtcNow.AddDays(-2))
        if(Test-WinUiReleaseReuseSafe $false $source $xbf $lib $targets){throw 'WINUI_REUSE_XBIND_UNSAFE'}
        [IO.File]::WriteAllText($source[0].FullName,'<App Click="Unsafe"/>')
        [IO.File]::SetLastWriteTimeUtc($source[0].FullName,[DateTime]::UtcNow.AddDays(-2))
        if(Test-WinUiReleaseReuseSafe $false $source $xbf $lib $targets){throw 'WINUI_REUSE_EVENT_UNSAFE'}
        [IO.File]::WriteAllText($source[0].FullName,'<App xmlns:local="using:SpankyhoOptimizer.Controls"/>')
        [IO.File]::SetLastWriteTimeUtc($source[0].FullName,[DateTime]::UtcNow.AddDays(-2))
        if(Test-WinUiReleaseReuseSafe $false $source $xbf $lib $targets){throw 'WINUI_REUSE_CUSTOM_NAMESPACE_UNSAFE'}
        [IO.File]::WriteAllText($source[0].FullName,'<App />')
        [IO.File]::SetLastWriteTimeUtc($source[0].FullName,[DateTime]::UtcNow.AddDays(-2))
        Remove-Item -LiteralPath $lib -Force
        if(Test-WinUiReleaseReuseSafe $false $source $xbf $lib $targets){throw 'WINUI_REUSE_MISSING_INTERMEDIATE_UNSAFE'}
        if(-not(Test-WinUiGraphRebuild 'source-v2:oldhash' 'newhash' $source $xbf)){throw 'WINUI_GRAPH_CHANGE_MISSED'}
        [IO.File]::SetLastWriteTimeUtc($source[0].FullName,[DateTime]::UtcNow)
        if(-not(Test-WinUiGraphRebuild 'legacyhash' 'newhash' $source $xbf)){throw 'WINUI_GRAPH_MIGRATION_STALE_MISSED'}
        Remove-Item -LiteralPath $xbf -Force
        if(-not(Test-WinUiGraphRebuild 'legacyhash' 'newhash' $source $xbf)){throw 'WINUI_GRAPH_XBF_MISSING_NOT_REBUILT'}
        if(-not(Test-WinUiGraphRebuild 'source-v2:newhash' 'newhash' $source $xbf)){throw 'WINUI_GRAPH_V2_XBF_MISSING_NOT_REBUILT'}
        $gen=Join-Path $fixture 'obj\Release'
        $pub=Join-Path $fixture 'publish'
        [IO.Directory]::CreateDirectory($pub)|Out-Null
        foreach($n in @('App.xbf','App.g.cs','XamlTypeInfo.g.cs')){
            [IO.File]::WriteAllText((Join-Path $gen $n),'generated-'+$n)
        }
        [IO.File]::WriteAllText((Join-Path $gen 'App.g.i.cs'),'public void InitializeComponent() {}')
        [IO.File]::WriteAllText((Join-Path $pub 'App.xbf'),'generated-App.xbf')
        if(-not(Test-WinUiGeneratedArtifactIntegrity $gen $pub)){throw 'WINUI_GENERATED_VALID_FALSE_NEGATIVE'}
        [IO.File]::WriteAllText((Join-Path $pub 'App.xbf'),'mismatch')
        if(Test-WinUiGeneratedArtifactIntegrity $gen $pub){throw 'WINUI_GENERATED_STALE_PUBLISHED_XBF_NOT_CAUGHT'}
        [IO.File]::WriteAllText((Join-Path $pub 'App.xbf'),'generated-App.xbf')
        Remove-Item -LiteralPath (Join-Path $gen 'App.g.i.cs') -Force
        if(Test-WinUiGeneratedArtifactIntegrity $gen $pub){throw 'WINUI_GENERATED_COMPONENT_MISSING_NOT_CAUGHT'}
        $bin=Join-Path $fixture 'bin\Release'
        [IO.Directory]::CreateDirectory($bin)|Out-Null
        [IO.File]::WriteAllText((Join-Path $bin 'App.xbf'),'generated-App.xbf')
        Remove-Item -LiteralPath (Join-Path $pub 'App.xbf') -Force
        if((Repair-WinUiPublishedXbf $gen $bin $pub) -ne 'copied-verified-build-output'){
            throw 'WINUI_XBF_RECOVERY_COPY_FAIL'
        }
        if((Repair-WinUiPublishedXbf $gen $bin $pub) -ne 'verified-existing'){
            throw 'WINUI_XBF_RECOVERY_EXISTING_FAIL'
        }
        [IO.File]::WriteAllText((Join-Path $bin 'App.xbf'),'bad-binary')
        $rejected=$false
        try {[void](Repair-WinUiPublishedXbf $gen $bin $pub)}catch{$rejected=$true}
        if(-not $rejected){throw 'WINUI_XBF_RECOVERY_MISMATCH_NOT_REJECTED'}
        Write-Host 'WINUI_SOURCE_GRAPH_POLICY_SMOKE_OK sourceOnly=true generatedExcluded=true legacySafe=true noEarlyCheckpoint=true generatedArtifacts=true xbfRecoveryHash=true'
    } finally {
        if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture -Recurse -Force}
    }
}
