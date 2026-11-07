$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot

$propsPath = Join-Path $root 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) {
    [Console]::Error.WriteLine("Error: Directory.Build.props not found: $propsPath")
    exit 1
}

$props = Get-Content -Raw -LiteralPath $propsPath
$versionMatch = [regex]::Match($props, '<Version>([^<]*)</Version>')
if (-not $versionMatch.Success) {
    [Console]::Error.WriteLine('Error: cannot find <Version>...</Version> in Directory.Build.props.')
    exit 1
}

$version = $versionMatch.Groups[1].Value.Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    [Console]::Error.WriteLine("Error: invalid version '$version' in Directory.Build.props.")
    exit 1
}

. (Join-Path $root 'scripts\Resolve-DotNet.ps1')

try {
    $dotnet = Resolve-DotNetPath
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

$publish = Join-Path $root 'artifacts\publish\win-x64'
$updaterDir = Join-Path $root 'artifacts\publish\updater'
$zip = Join-Path $root "artifacts\Launcher-$version-win-x64.zip"

Write-Host "Publishing Launcher $version (win-x64)..."

foreach ($dir in @($publish, $updaterDir)) {
    $absolute = [IO.Path]::GetFullPath($dir)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\publish')) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe publish directory: $absolute" }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}

Write-Host 'Publishing launcher...'
& $dotnet publish (Join-Path $root 'src\SamaraLauncher.App\SamaraLauncher.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -o $publish --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Publishing updater...'
& $dotnet publish (Join-Path $root 'src\SamaraLauncher.Updater\SamaraLauncher.Updater.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -o $updaterDir --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item (Join-Path $updaterDir 'Launcher.Updater.exe') $publish -Force
Get-ChildItem $publish -Filter '*.pdb' -Recurse | Remove-Item -Force

if (-not (Test-Path (Join-Path $publish 'README.txt'))) {
    Copy-Item (Join-Path $root 'docs\README-DISTRIBUTION.txt') (Join-Path $publish 'README.txt') -Force
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host ''
Write-Host "Package: $zip"
Write-Host "SHA-256: $hash"
