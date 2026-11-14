$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$quality=Join-Path $root 'quality-audit.ps1'
$release=Join-Path $root 'build-release.ps1'
$props=Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$qualityText=Get-Content $quality -Raw
$releaseText=Get-Content $release -Raw
$required=@('dependency-audit','roslyn-custom','extended-analyzers','analyzer-regression','architecture-tests','behavior-tests','fuzz-smoke','fault-injection','coyote-concurrency','flaky-critical','rollback-matrix')
foreach($item in $required){
  if(-not $qualityText.Contains("Step '$item'")){throw "QUALITY_INTEGRATION_MISSING $item"}
}
foreach($setting in @('<EnableNETAnalyzers>true</EnableNETAnalyzers>','<AnalysisLevel Condition=')){
  if(-not $props.Contains($setting)){throw "QUALITY_ANALYZER_CONFIG_MISSING $setting"}
}
foreach($file in @('analyzer-regression-gate.ps1','extended-analyzers.ps1','dependency-audit.ps1','ai-automation-contract-audit.ps1')){
  if(-not(Test-Path (Join-Path $root $file))){throw "QUALITY_TOOL_MISSING $file"}
}
if($releaseText -notmatch 'ai-automation-contract-audit\.ps1'){throw 'QUALITY_RELEASE_CONTRACT_GUARD_MISSING'}
# Fail even before the architecture suite runs if a later edit removed its
# mandatory early release gates or their minimum TRX test-count evidence.
foreach($token in @(
  'RELEASE_SOURCE_ARCHITECTURE_GATE_FAILED',
  'RELEASE_READONLY_CORE_GATE_FAILED',
  'Assert-ReleaseUnitResults $qualityReport 43',
  'Assert-ReleaseUnitResults $coreReport 24',
  'Test-OptimizerReleaseArtifactManifest.ps1',
  'Verify-OptimizerReleaseArtifactManifest.ps1',
  'RELEASE_MANIFEST_FIXTURE_GATE_FAILED',
  'RELEASE_MANIFEST_ARTIFACT_INTEGRITY_FAILED',
  'RELEASE_SCOPED_ROLLBACK_GATE_FAILED',
  'Test-OptimizerCompactBundle.ps1',
  'RELEASE_COMPACT_BUNDLE_TAMPER_GATE_FAILED',
  'Test-OptimizerRuntimeReadyIsolation.ps1',
  'RELEASE_RUNTIME_READY_ISOLATION_FAILED',
  'Test-OptimizerDesktopExpectedHash.ps1',
  'RELEASE_DESKTOP_PINNED_HASH_FIXTURE_GATE_FAILED',
  '-ExpectedSha256 $compactHash'
)){
  if(-not $releaseText.Contains($token)){throw "QUALITY_RELEASE_UNIT_GATE_MISSING $token"}
}
Write-Output ('QUALITY_INTEGRATION_GUARD_OK fullAuditSteps='+$required.Count+' releaseContracts=true sourceArchitecture=true readonlyCore=true nativeAnalyzers=true')
