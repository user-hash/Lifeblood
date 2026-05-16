param(
    [string]$FfmpegRoot = 'D:\Projekti\ffmpeg-lifeblood-scout\ffmpeg',
    [string]$ScoutRoot = 'D:\Projekti\ffmpeg-lifeblood-scout',
    [string]$FfmpegRemote = 'https://github.com/FFmpeg/FFmpeg.git',
    [string]$BuildDir = '',
    [string]$OutputGraph = '',
    [string]$Profile = 'ffmpeg-clang-minimal-scout',
    [string]$FilesList = '',
    [string]$ClangPath = 'C:\Program Files\LLVM\bin\clang.exe',
    [string]$GitBashPath = 'C:\Program Files\Git\bin\bash.exe',
    [string]$VsDevCmdPath = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat',
    [switch]$SkipClone,
    [switch]$SkipConfigure,
    [switch]$SkipRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\ScoutPaths.ps1"
. "$PSScriptRoot\ScoutToolchain.ps1"
. "$PSScriptRoot\ScoutCompileDatabase.ps1"
. "$PSScriptRoot\ScoutExtraction.ps1"

function Assert-ScoutTool {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Name not found at '$Path'."
    }
}

function Ensure-FfmpegCheckout {
    param(
        [Parameter(Mandatory)][string]$FfmpegRoot,
        [Parameter(Mandatory)][string]$ScoutRoot,
        [Parameter(Mandatory)][string]$FfmpegRemote,
        [switch]$SkipClone
    )

    if (Test-Path -LiteralPath (Join-Path $FfmpegRoot '.git')) {
        return
    }

    if ($SkipClone) {
        throw "FFmpeg checkout missing at '$FfmpegRoot' and -SkipClone was set."
    }

    New-Item -ItemType Directory -Force -Path $ScoutRoot | Out-Null
    git clone --depth 1 $FfmpegRemote $FfmpegRoot
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg clone failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = Get-LifebloodRepoRoot -StartPath $PSScriptRoot
$nativeAdapter = Join-Path $repoRoot 'artifacts\native-clang-build\lifeblood-native-clang.exe'
if (-not $BuildDir) {
    $BuildDir = Join-Path $FfmpegRoot 'build-lifeblood-clang'
}
if (-not $OutputGraph) {
    $OutputGraph = Join-Path $ScoutRoot 'ffmpeg-scout.graph.json'
}
if (-not $FilesList) {
    $FilesList = Join-Path $PSScriptRoot 'default-files.txt'
}

Assert-ScoutTool -Path $ClangPath -Name 'LLVM clang'
Assert-ScoutTool -Path $GitBashPath -Name 'Git Bash'
Assert-ScoutTool -Path $VsDevCmdPath -Name 'Visual Studio developer command'

Ensure-FfmpegCheckout `
    -FfmpegRoot $FfmpegRoot `
    -ScoutRoot $ScoutRoot `
    -FfmpegRemote $FfmpegRemote `
    -SkipClone:$SkipClone

if (-not $SkipConfigure) {
    Invoke-ScoutConfigure `
        -FfmpegRoot $FfmpegRoot `
        -BuildDir $BuildDir `
        -GitBashPath $GitBashPath `
        -VsDevCmdPath $VsDevCmdPath `
        -ClangPath $ClangPath
}

$files = @(Read-ScoutFileList -Path $FilesList)
$systemIncludes = @(Get-ScoutSystemIncludes -ClangPath $ClangPath -VsDevCmdPath $VsDevCmdPath)
$compileDatabase = Join-Path $BuildDir 'compile_commands.json'
Write-ScoutCompileDatabase `
    -FfmpegRoot $FfmpegRoot `
    -BuildDir $BuildDir `
    -Files $files `
    -SystemIncludes $systemIncludes `
    -OutputPath $compileDatabase

Write-Host "Prepared FFmpeg scout compile database:"
Write-Host "  $compileDatabase"
Write-Host "  files: $($files.Count)"
Write-Host "  system includes: $($systemIncludes.Count)"

if (-not $SkipRun) {
    Invoke-NativeScoutExtraction `
        -NativeAdapterPath $nativeAdapter `
        -FfmpegRoot $FfmpegRoot `
        -BuildDir $BuildDir `
        -Profile $Profile `
        -OutputGraph $OutputGraph

    Write-ScoutGraphSummary -GraphPath $OutputGraph
}
