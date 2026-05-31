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

function Assert-UnderRoot([string]$Root, [string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside expected root: $full"
    }

    return $full
}

function Invoke-DotnetLines([string[]]$Arguments) {
    # Windows PowerShell 5.1: `2>&1` on a native exe wraps each stderr line as
    # a NativeCommandError ErrorRecord, which terminates under
    # $ErrorActionPreference='Stop' even on exit 0 (NuGet writes warnings to
    # stderr). Demote to Continue across the call so the real exit code - not
    # incidental stderr - is the success signal.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & dotnet @Arguments 2>&1 } finally { $ErrorActionPreference = $prevEap }
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

function Copy-SourceTree([string]$SourceRoot, [string]$DestinationRoot) {
    $excludedSegments = @(
        ".git",
        ".vs",
        "artifacts",
        "bin",
        "cli-dist",
        "dist",
        "dist-next",
        "dist-staging",
        "nupkg-local",
        "obj",
        "packages",
        "publish-staging",
        "TestResults"
    )

    New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null

    Get-ChildItem -LiteralPath $SourceRoot -Force -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($SourceRoot.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)

        if ([string]::IsNullOrWhiteSpace($relative)) {
            return
        }

        $segments = @($relative -split '[\\/]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($segments.Count -eq 1 -and $segments[0] -eq "global.json") {
            return
        }

        if (@($segments | Where-Object { $excludedSegments -contains $_ }).Count -gt 0) {
            return
        }

        $destination = Join-Path $DestinationRoot $relative
        if ($_.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            return
        }

        $parent = Split-Path -Parent $destination
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }

        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}

function Set-CopiedProjectTargetFrameworks([string]$SourceRoot, [string]$TargetFramework) {
    $projects = @()

    $srcRoot = Join-Path $SourceRoot "src"
    if (Test-Path -LiteralPath $srcRoot) {
        $projects += @(Get-ChildItem -LiteralPath $srcRoot -Recurse -Filter "*.csproj")
    }

    $testProject = Join-Path $SourceRoot "tests/Lifeblood.Tests/Lifeblood.Tests.csproj"
    if (Test-Path -LiteralPath $testProject) {
        $projects += @(Get-Item -LiteralPath $testProject)
    }

    $retargeted = @()
    foreach ($project in $projects) {
        $text = Get-Content -LiteralPath $project.FullName -Raw
        if ($text -notmatch '<TargetFramework>[^<]+</TargetFramework>') {
            throw "Project does not declare a single TargetFramework: $($project.FullName)"
        }

        $next = [regex]::Replace(
            $text,
            '<TargetFramework>[^<]+</TargetFramework>',
            "<TargetFramework>$TargetFramework</TargetFramework>",
            1)

        Set-Content -LiteralPath $project.FullName -Value $next -Encoding UTF8
        $retargeted += $project.FullName.Substring($SourceRoot.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    }

    return @($retargeted)
}

function Invoke-Step($Report, [string]$Name, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Host "[$Name] dotnet $($Arguments -join ' ')"

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location -LiteralPath $WorkingDirectory
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # see Invoke-DotnetLines: native stderr is not a failure
    try {
        $output = & dotnet @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
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
$checkedInSolution = Join-Path $repoRoot "Lifeblood.sln"
$targetPathName = $TargetFramework -replace '[^A-Za-z0-9_.-]', '_'
$tempBase = Join-Path ([System.IO.Path]::GetTempPath()) "lifeblood-dotnet-experimental"
$workDir = Assert-UnderRoot $tempBase (Join-Path $tempBase $targetPathName)
$experimentalSourceRoot = Join-Path $workDir "source"
$solution = Join-Path $experimentalSourceRoot "Lifeblood.sln"
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
    sourceRoot = $experimentalSourceRoot
    checkedInSolution = $checkedInSolution
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
        "The lane copies source to a temporary tree and retargets only the copied solution projects.",
        "The temporary tree omits root global.json so the installed experimental SDK can be selected honestly.",
        "Build serializes MSBuild nodes to keep experimental obj/bin output isolated and deterministic.",
        "Packages are written under repository artifacts for CI collection; no packages are published."
    )
    retargetedProjects = @()
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

if (Test-Path -LiteralPath $workDir) {
    Remove-Item -LiteralPath $workDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $workDir | Out-Null
New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null
Copy-SourceTree $repoRoot $experimentalSourceRoot
$report.retargetedProjects = @(Set-CopiedProjectTargetFrameworks $experimentalSourceRoot $TargetFramework)

try {
    Invoke-Step $report "restore" @(
        "restore", $solution,
        "-nodeReuse:false",
        "--nologo"
    ) $workDir

    Invoke-Step $report "build" @(
        "build", $solution,
        "-c", $Configuration,
        "--no-restore",
        "-maxcpucount:1",
        "-nodeReuse:false",
        "--nologo"
    ) $workDir

    if (-not $SkipTests) {
        Invoke-Step $report "test" @(
            "test", (Join-Path $experimentalSourceRoot "tests/Lifeblood.Tests/Lifeblood.Tests.csproj"),
            "-c", $Configuration,
            "--no-build",
            "--no-restore",
            "-nodeReuse:false",
            "--nologo"
        ) $workDir
    }

    if (-not $SkipPack) {
        Invoke-Step $report "pack-cli" @(
            "pack", (Join-Path $experimentalSourceRoot "src/Lifeblood.CLI/Lifeblood.CLI.csproj"),
            "-c", $Configuration,
            "--no-build",
            "-nodeReuse:false",
            "-o", $packagesDir,
            "--nologo"
        ) $workDir

        Invoke-Step $report "pack-mcp" @(
            "pack", (Join-Path $experimentalSourceRoot "src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj"),
            "-c", $Configuration,
            "--no-build",
            "-nodeReuse:false",
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
