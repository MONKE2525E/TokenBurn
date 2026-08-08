param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.1.39'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishRoot = Join-Path $root 'artifacts\publish'

if (-not $publishRoot.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write outside the repository: $publishRoot"
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$desktopOutput = Join-Path $publishRoot 'desktop'
$cliOutput = Join-Path $publishRoot 'cli'

dotnet publish (Join-Path $root 'UsageMonitor.Desktop\UsageMonitor.Desktop.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $desktopOutput `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version

dotnet publish (Join-Path $root 'UsageMonitor.Cli\UsageMonitor.Cli.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $cliOutput `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version

if (-not (Test-Path -LiteralPath (Join-Path $desktopOutput 'UsageMonitor.exe'))) {
    throw 'Desktop publish did not produce UsageMonitor.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $cliOutput 'usage-monitor.exe'))) {
    throw 'CLI publish did not produce usage-monitor.exe.'
}

Write-Host "Published Usage Monitor $Version for $Runtime to $publishRoot"
