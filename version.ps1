param(
    [Parameter(Position = 0)]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$Value
)

$ErrorActionPreference = 'Stop'

$propsPath = Join-Path $PSScriptRoot 'Directory.Build.props'
$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'

function Fail {
    param([string]$Message)

    [Console]::Error.WriteLine('Error: ' + $Message)
    exit 1
}

function Get-Version {
    if (-not (Test-Path -LiteralPath $propsPath)) {
        Fail "Directory.Build.props not found: $propsPath"
    }

    $text = [System.IO.File]::ReadAllText($propsPath)
    $match = [regex]::Match($text, '<Version>([^<]*)</Version>')
    if (-not $match.Success) {
        Fail 'Cannot find <Version>...</Version> in Directory.Build.props.'
    }

    $version = $match.Groups[1].Value.Trim()
    if ($version -notmatch $versionPattern) {
        Fail "Invalid version '$version' in Directory.Build.props. Expected MAJOR.MINOR.PATCH without leading zeros."
    }

    return $version
}

function Set-Version {
    param([string]$NewVersion)

    if ($NewVersion -notmatch $versionPattern) {
        Fail "Invalid version '$NewVersion'. Expected MAJOR.MINOR.PATCH without leading zeros."
    }

    $bytes = [System.IO.File]::ReadAllBytes($propsPath)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($propsPath, [System.Text.Encoding]::UTF8)
    $match = [regex]::Match($text, '<Version>([^<]*)</Version>')
    if (-not $match.Success) {
        Fail 'Cannot find <Version>...</Version> in Directory.Build.props.'
    }

    $group = $match.Groups[1]
    $updated = $text.Substring(0, $group.Index) + $NewVersion + $text.Substring($group.Index + $group.Length)

    try {
        $encoding = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList @($hasBom)
        [System.IO.File]::WriteAllText($propsPath, $updated, $encoding)
    }
    catch {
        Fail ('Cannot write Directory.Build.props: ' + $_.Exception.Message)
    }
}

function Add-One {
    param([string]$Number)

    $chars = $Number.ToCharArray()
    for ($i = $chars.Length - 1; $i -ge 0; $i--) {
        if ($chars[$i] -ne '9') {
            $chars[$i] = [char](([int]([char]$chars[$i])) + 1)
            return -join $chars
        }

        $chars[$i] = '0'
    }

    return '1' + (-join $chars)
}

if ([string]::IsNullOrWhiteSpace($Command)) {
    [Console]::Error.WriteLine('Usage: .\version.ps1 show|patch|minor|major|set <version>')
    exit 1
}

$commandName = $Command.Trim().ToLowerInvariant()
$current = Get-Version

switch ($commandName) {
    'show' {
        Write-Output $current
        exit 0
    }
    'patch' {
        $parts = $current.Split('.')
        $next = $parts[0] + '.' + $parts[1] + '.' + (Add-One $parts[2])
    }
    'minor' {
        $parts = $current.Split('.')
        $next = $parts[0] + '.' + (Add-One $parts[1]) + '.0'
    }
    'major' {
        $parts = $current.Split('.')
        $next = (Add-One $parts[0]) + '.0.0'
    }
    'set' {
        if ([string]::IsNullOrWhiteSpace($Value)) {
            Fail 'Missing version for set. Usage: .\version.ps1 set MAJOR.MINOR.PATCH'
        }

        $next = $Value.Trim()
        if ($next -notmatch $versionPattern) {
            Fail "Invalid version '$next'. Expected MAJOR.MINOR.PATCH without leading zeros."
        }
    }
    default {
        Fail "Unknown command '$Command'. Usage: .\version.ps1 show|patch|minor|major|set <version>"
    }
}

Set-Version $next
Write-Host "Current version: $current"
Write-Host "New version: $next"
exit 0
