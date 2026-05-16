Set-StrictMode -Version Latest

function Read-ScoutFileList {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Scout file list not found: $Path"
    }

    return Get-Content -LiteralPath $Path |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') }
}

function Write-ScoutCompileDatabase {
    param(
        [Parameter(Mandatory)][string]$FfmpegRoot,
        [Parameter(Mandatory)][string]$BuildDir,
        [Parameter(Mandatory)][string[]]$Files,
        [Parameter(Mandatory)][string[]]$SystemIncludes,
        [Parameter(Mandatory)][string]$OutputPath
    )

    New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

    $root = ConvertTo-ForwardSlashPath -Path ([System.IO.Path]::GetFullPath($FfmpegRoot))
    $build = ConvertTo-ForwardSlashPath -Path ([System.IO.Path]::GetFullPath($BuildDir))

    $common = @(
        'clang',
        '--target=x86_64-pc-windows-msvc',
        '-fms-extensions',
        '-fms-compatibility',
        '-D_ISOC11_SOURCE',
        '-D_FILE_OFFSET_BITS=64',
        '-D_LARGEFILE_SOURCE',
        '-DWIN32_LEAN_AND_MEAN',
        '-D_USE_MATH_DEFINES',
        '-D_CRT_SECURE_NO_WARNINGS',
        '-D_CRT_NONSTDC_NO_WARNINGS',
        '-D_WIN32_WINNT=0x0600',
        '-I.',
        '-I..',
        '-I../compat/atomics/win32'
    )

    foreach ($include in $SystemIncludes) {
        $common += @('-isystem', (ConvertTo-ForwardSlashPath -Path $include))
    }

    $common += @('-std=c17', '-c')

    $commands = foreach ($file in $Files) {
        [ordered]@{
            directory = $build
            file = "$root/$file"
            arguments = @($common + @("../$file"))
        }
    }

    $commands |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
