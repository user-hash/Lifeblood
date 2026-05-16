Set-StrictMode -Version Latest

function Get-ClangResourceInclude {
    param([Parameter(Mandatory)][string]$ClangPath)

    $resourceDir = (& $ClangPath -print-resource-dir).Trim()
    if (-not $resourceDir) {
        throw "Failed to resolve clang resource dir from '$ClangPath'."
    }

    return Join-Path $resourceDir 'include'
}

function Get-VsIncludeDirs {
    param([Parameter(Mandatory)][string]$VsDevCmdPath)

    $line = & cmd.exe /c "call ""$VsDevCmdPath"" -arch=x64 -host_arch=x64 >nul && set INCLUDE"
    if ($LASTEXITCODE -ne 0 -or -not $line) {
        throw "Failed to resolve Visual Studio INCLUDE dirs through '$VsDevCmdPath'."
    }

    $includeLine = $line | Where-Object { $_ -like 'INCLUDE=*' } | Select-Object -First 1
    if (-not $includeLine) {
        throw "Visual Studio developer environment did not expose INCLUDE."
    }

    return $includeLine.Substring('INCLUDE='.Length).Split(';') |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) }
}

function Get-ScoutSystemIncludes {
    param(
        [Parameter(Mandatory)][string]$ClangPath,
        [Parameter(Mandatory)][string]$VsDevCmdPath
    )

    $includes = New-Object System.Collections.Generic.List[string]
    $includes.Add((Get-ClangResourceInclude -ClangPath $ClangPath))
    foreach ($include in Get-VsIncludeDirs -VsDevCmdPath $VsDevCmdPath) {
        if (-not $includes.Contains($include)) {
            $includes.Add($include)
        }
    }

    return $includes
}

function Invoke-ScoutConfigure {
    param(
        [Parameter(Mandatory)][string]$FfmpegRoot,
        [Parameter(Mandatory)][string]$BuildDir,
        [Parameter(Mandatory)][string]$GitBashPath,
        [Parameter(Mandatory)][string]$VsDevCmdPath,
        [Parameter(Mandatory)][string]$ClangPath
    )

    $llvmBin = Split-Path -Parent $ClangPath
    $rootBash = ConvertTo-GitBashPath -Path $FfmpegRoot
    $buildBash = ConvertTo-GitBashPath -Path $BuildDir
    $configure = Quote-BashSingle -Value "$rootBash/configure"
    $build = Quote-BashSingle -Value $buildBash

    $bashCommand = @(
        "mkdir -p $build",
        "cd $build",
        "$configure --cc=clang --cxx=clang++ --target-os=win64 --arch=x86_64 --disable-programs --disable-doc --disable-x86asm --disable-asm --disable-network --disable-autodetect --disable-everything"
    ) -join ' && '

    & cmd.exe /c "call ""$VsDevCmdPath"" -arch=x64 -host_arch=x64 >nul && set ""PATH=$llvmBin;%PATH%"" && ""$GitBashPath"" -lc ""$bashCommand"""
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg configure failed with exit code $LASTEXITCODE."
    }
}
