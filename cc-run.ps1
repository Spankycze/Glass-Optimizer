param(
  [Parameter(Mandatory=$true, Position=0)]
  [string]$Command
)

$ErrorActionPreference = 'Stop'
$project = 'C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer'
Push-Location $project
try {
    & context-compress wrap --mode balanced $Command
}
finally {
    Pop-Location
}
