param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$windowsPath = Join-Path $root 'src\SpankyhoOptimizer\Services\WindowsActions.cs'
$repairPath = Join-Path $root 'src\SpankyhoOptimizer\Services\SystemRepairDiagnosticsService.cs'
$windows = Get-Content -LiteralPath $windowsPath -Raw
$repair = Get-Content -LiteralPath $repairPath -Raw

$failures = New-Object System.Collections.Generic.List[string]

foreach ($check in @(
    @{ Name='FlushDns'; Pattern='public static Task FlushDnsAsync[\s\S]*?RequireCommandSuccessAsync\("ipconfig\.exe",\s*"/flushdns"' },
    @{ Name='ResetWinsock'; Pattern='public static Task ResetWinsockAsync[\s\S]*?RequireCommandSuccessAsync\("netsh\.exe",\s*"winsock reset"' },
    @{ Name='RetrimAll'; Pattern='public static Task RetrimAllAsync[\s\S]*?RequireCommandSuccessAsync\("defrag\.exe",\s*"/C /L /O /U"' },
    @{ Name='RequireCommandSuccess'; Pattern='RequireCommandSuccessAsync[\s\S]*?ProcessRunner\.RunAsync[\s\S]*?if \(!result\.Success\)[\s\S]*?throw new InvalidOperationException' }
)) {
    if ($windows -notmatch $check.Pattern) { $failures.Add($check.Name) }
    else { Write-Output "PASS $($check.Name) mutation result contract" }
}

$networkStart = $repair.IndexOf('internal static async Task<WorkerAck> FlushNetworkLocalAsync')
if ($networkStart -lt 0) {
    $failures.Add('FlushNetworkLocalMissing')
} else {
    $network = $repair.Substring($networkStart)
    $required = @(
        'ProcessRunner.RunAsync("ipconfig.exe", "/flushdns"',
        'if (!dns.Success) throw new InvalidOperationException',
        'ProcessRunner.RunAsync("netsh.exe", "winsock reset"',
        'if (!winsock.Success) throw new InvalidOperationException',
        'return new WorkerAck()'
    )
    $cursor = 0
    foreach ($needle in $required) {
        $idx = $network.IndexOf($needle, $cursor)
        if ($idx -lt 0) { $failures.Add("FlushNetworkLocal:$needle"); break }
        $cursor = $idx + $needle.Length
    }
    if (-not ($failures | Where-Object { $_ -like 'FlushNetworkLocal:*' })) {
        Write-Output 'PASS FlushNetworkLocal propagates failures in command order'
    }
}

if ($failures.Count -gt 0) {
    throw ('Mutation result contract regressions: ' + ($failures -join ', '))
}
Write-Output 'PROCESS_MUTATION_RESULT_TEST_OK'
