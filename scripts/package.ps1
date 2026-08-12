param(
    [string]$Version = '0.0.1',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration -Runtime $Runtime -Version $Version

$compiler = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $compiler) {
    throw 'Inno Setup compiler not found. Install JRSoftware.InnoSetup or build the publish output directly.'
}

$output = Join-Path $root 'artifacts\installer'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $compiler "/DMyAppVersion=$Version" "/DMyRuntime=$Runtime" "/O$output" (Join-Path $root 'packaging\UsageMonitor.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host "Installer written to $output"
