param(
    [string]$TargetFramework = "net10.0",
    [string]$OutputPath = "artifacts/dotnet-experimental/target-report.json",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipPack,
    [switch]$FailWhenUnavailable
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    $dir = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    return $dir.Path
}

function Invoke-DotnetLines([string[]]$Arguments) {
    $output = & dotnet @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$output"
    }
    return @($output)
}

function Get-TargetMajor([string]$TargetFramework) {
    $match = [regex]::Match($TargetFramework, '^net(\d+)\.')
    if (-not $match.Success) {
        throw "Unsupported target framework format: $TargetFramework"
    }

    return [int]$match.Groups[1].Value
}

function Get-SdkMajor([string]$SdkLine) {
    $match = [regex]::Match($SdkLine, '^(\d+)\.')
    if (-not $match.Success) { return $null }
    return [int]$match.Groups[1].Value
}

function Write-Report($Report, [string]$Path) {
    $dir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $Report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-Step($Report, [string]$Name, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Host "[$Name] dotnet $($Arguments -join ' ')"

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & dotnet @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $stopwatch.Stop()
    }

    $Report.steps += [pscustomobject]@{
        name = $Name
        command = @("dotnet") + $Arguments
        workingDirectory = $WorkingDirectory
        exitCode = $exitCode
        durationMs = [long]$stopwatch.ElapsedMilliseconds
        output = @($output)
    }

    if ($exitCode -ne 0) {
        $Report.status = "failed"
        throw "Step '$Name' failed with exit code $exitCode."
    }
}

$repoRoot = Get-RepoRoot
$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
$solution = Join-Path $repoRoot "Lifeblood.sln"
$packagesDir = Join-Path $repoRoot "artifacts/dotnet-experimental/$TargetFramework/packages"
$sdkLines = @(Invoke-DotnetLines @("--list-sdks"))
$runtimeLines = @(Invoke-DotnetLines @("--list-runtimes"))
$sdkMajors = @($sdkLines | ForEach-Object { Get-SdkMajor $_ } | Where-Object { $null -ne $_ })
$targetMajor = Get-TargetMajor $TargetFramework
$highestSdkMajor = if ($sdkMajors.Count -gt 0) { ($sdkMajors | Measure-Object -Maximum).Maximum } else { 0 }

$report = [ordered]@{
    schemaVersion = 1
    lane = "dotnet-experimental-target"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repoRoot = $repoRoot
    solution = $solution
    targetFramework = $TargetFramework
    configuration = $Configuration
    status = "running"
    artifacts = [ordered]@{
        packages = $packagesDir
    }
    host = [ordered]@{
        dotnetSdks = $sdkLines
        dotnetRuntimes = $runtimeLines
        highestSdkMajor = $highestSdkMajor
    }
    notes = @(
        "Production project files remain pinned to net8.0.",
        "The lane passes TargetFramework as an MSBuild global property.",
        "Commands run from a temp directory so repo global.json does not pin the experimental SDK."
    )
    steps = @()
}

if ($highestSdkMajor -lt $targetMajor) {
    $report.status = "skipped"
    $report.skipReason = "No installed .NET SDK can build $TargetFramework. Highest SDK major: $highestSdkMajor."
    Write-Report $report $outputFullPath
    Write-Host $report.skipReason
    if ($FailWhenUnavailable) { exit 1 }
    return
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) "lifeblood-dotnet-experimental"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null

try {
    Invoke-Step $report "restore" @(
        "restore", $solution,
        "-p:TargetFramework=$TargetFramework",
        "--nologo"
    ) $workDir

    Invoke-Step $report "build" @(
        "build", $solution,
        "-c", $Configuration,
        "-p:TargetFramework=$TargetFramework",
        "--no-restore",
        "--nologo"
    ) $workDir

    if (-not $SkipTests) {
        Invoke-Step $report "test" @(
            "test", (Join-Path $repoRoot "tests/Lifeblood.Tests/Lifeblood.Tests.csproj"),
            "-c", $Configuration,
            "-p:TargetFramework=$TargetFramework",
            "--no-build",
            "--no-restore",
            "--nologo"
        ) $workDir
    }

    if (-not $SkipPack) {
        Invoke-Step $report "pack-cli" @(
            "pack", (Join-Path $repoRoot "src/Lifeblood.CLI/Lifeblood.CLI.csproj"),
            "-c", $Configuration,
            "-p:TargetFramework=$TargetFramework",
            "--no-build",
            "-o", $packagesDir,
            "--nologo"
        ) $workDir

        Invoke-Step $report "pack-mcp" @(
            "pack", (Join-Path $repoRoot "src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj"),
            "-c", $Configuration,
            "-p:TargetFramework=$TargetFramework",
            "--no-build",
            "-o", $packagesDir,
            "--nologo"
        ) $workDir
    }

    $report.status = "passed"
}
finally {
    Write-Report $report $outputFullPath
}

Write-Host "Wrote experimental target report to $outputFullPath"
