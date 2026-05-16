Set-StrictMode -Version Latest

function Get-LifebloodRepoRoot {
    param([Parameter(Mandatory)][string]$StartPath)

    $current = (Resolve-Path -LiteralPath $StartPath).Path
    while ($current) {
        if (Test-Path -LiteralPath (Join-Path $current 'Lifeblood.sln')) {
            return $current
        }

        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }
        $current = $parent
    }

    throw "Could not locate Lifeblood.sln above '$StartPath'."
}

function ConvertTo-ForwardSlashPath {
    param([Parameter(Mandatory)][string]$Path)

    return $Path.Replace('\', '/')
}

function ConvertTo-GitBashPath {
    param([Parameter(Mandatory)][string]$Path)

    $text = ConvertTo-ForwardSlashPath -Path ([System.IO.Path]::GetFullPath($Path))
    if ($text -match '^([A-Za-z]):/(.*)$') {
        return '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2]
    }

    return $text
}

function Quote-BashSingle {
    param([Parameter(Mandatory)][string]$Value)

    return "'" + $Value.Replace("'", "'\''") + "'"
}
