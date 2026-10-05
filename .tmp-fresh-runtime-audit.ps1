$cut = [datetime]'2026-10-01 09:37:19'
Get-Content 'C:\ProgramData\SpankyhoOptimizer\optimizer.log' | ForEach-Object {
    $line = $_
    if ($line -match '^(?<date>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})' -and [datetime]$Matches.date -ge $cut) {
        if ($line -match '\[ERR\]|failed|FAIL') { $line }
    }
}
'---PROC---'
Get-Process SpankyhoOptimizer -ErrorAction SilentlyContinue | Select-Object Id,StartTime,@{n='WSMB';e={[math]::Round($_.WorkingSet64/1MB,1)}},Path | Format-List
