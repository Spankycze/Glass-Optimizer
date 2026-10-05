$start=(Get-Date).AddMinutes(-20)
$events=Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$start} -ErrorAction SilentlyContinue
$matches=$events | Where-Object { $_.Message -match 'SpankyhoOptimizer|0xc000027b|WindowsAppRuntime|Microsoft.UI.Xaml' }
$matches | Select-Object -First 20 TimeCreated,Id,ProviderName,LevelDisplayName,Message | Format-List
