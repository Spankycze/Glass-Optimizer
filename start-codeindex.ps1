$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$dll=Join-Path $root '.tools\CodeIndex\src\CodeIndex\bin\Release\net10.0\CodeIndex.dll'
if(-not(Test-Path $dll)){throw 'CodeIndex.dll missing'}
& dotnet $dll --root $root