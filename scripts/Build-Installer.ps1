param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Resolve-DotNet.ps1')
$dotnet = Resolve-DotNetPath
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$version = [string]$props.Project.PropertyGroup.Version
if (-not $SkipPublish) {
    & (Join-Path $root 'publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
$payload = Join-Path $root "artifacts\Launcher-$version-win-x64.zip"
if (-not (Test-Path -LiteralPath $payload)) { throw "Missing payload: $payload" }
$output = Join-Path $root 'artifacts\setup'
& $dotnet publish (Join-Path $root 'src\Launcher.Setup\Launcher.Setup.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false "-p:PayloadPath=$payload" -o $output --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$installer = Join-Path $root "artifacts\Launcher-Setup-$version-win-x64.exe"
Copy-Item -LiteralPath (Join-Path $output 'Launcher.Setup.exe') -Destination $installer -Force
$exe = Join-Path $root 'artifacts\publish\win-x64\Launcher.exe'
$manifest = [ordered]@{
    version = $version
    url = "https://github.com/1Boss-mma/launcher/releases/download/v$version/Launcher-$version.exe"
    sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    notes = 'Обновление Launcher'
    mandatory = $false
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts\latest.json') -Encoding utf8
Copy-Item -LiteralPath $exe -Destination (Join-Path $root "artifacts\Launcher-$version.exe") -Force
Write-Output "Installer: $installer"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)"
