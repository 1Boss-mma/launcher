$ErrorActionPreference = 'Stop'

function Test-DotNetSdk {
    param([string]$Path)

    try {
        $sdks = & $Path --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $false
        }

        return [bool]($sdks | Where-Object { $_ -match '^8\.' })
    }
    catch {
        return $false
    }
}

function Resolve-DotNetPath {
    $candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_EXE)) {
        $candidates += $env:DOTNET_EXE
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command -and -not [string]::IsNullOrWhiteSpace($command.Source)) {
        $candidates += $command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
    }

    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    }

    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
        $candidates += (Join-Path $env:USERPROFILE '.dotnet8\dotnet.exe')
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    }

    $found = @()
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        if ($found -contains $resolved) {
            continue
        }

        $found += $resolved
        if (Test-DotNetSdk $resolved) {
            return $resolved
        }
    }

    if ($found.Count -gt 0) {
        $message = ("dotnet found, but no .NET 8 SDK is installed ({0}). " +
            "Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0") -f ($found -join ', ')
        throw $message
    }

    throw 'dotnet not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0'
}

if ($MyInvocation.InvocationName -ne '.') {
    Resolve-DotNetPath
}
