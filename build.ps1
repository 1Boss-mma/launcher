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

$buildArgs = @()
foreach ($arg in $args) {
    if ($arg -eq '-NoIncremental') {
        $buildArgs += '--no-incremental'
    }
    else {
        $buildArgs += $arg
    }
}

& $dotnet build (Join-Path $root 'Launcher.sln') -c Release @buildArgs
exit $LASTEXITCODE
