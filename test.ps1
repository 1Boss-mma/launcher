$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
. (Join-Path $root 'scripts\Resolve-DotNet.ps1')

try {
    $dotnet = Resolve-DotNetPath
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

& $dotnet test (Join-Path $root 'tests\SamaraLauncher.Tests\SamaraLauncher.Tests.csproj') -c Release @args
exit $LASTEXITCODE
