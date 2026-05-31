param(
    [string]$TargetFramework = "net10.0",
    [string]$OutputPath = "artifacts/dotnet-experimental/target-report.json",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipPack,
    [switch]$SkipSemanticAnalyze,
    [switch]$RestoreIgnoreFailedSources,
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

function Parse-Long([string]$Text) {
    return [long]($Text -replace ',', '')
}

function Match-Long([string]$Text, [string]$Pattern) {
    $match = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $match.Success) { return $null }
    return Parse-Long $match.Groups[1].Value
}

function Read-StatusAnchors([string]$RepoRoot) {
    $statusPath = Join-Path $RepoRoot "docs/STATUS.md"
    $text = Get-Content -LiteralPath $statusPath -Raw
    $anchors = [ordered]@{}

    foreach ($match in [regex]::Matches($text, '<!--\s*([A-Za-z][A-Za-z0-9]*):\s*([\d,]+)\s*-->')) {
        $anchors[$match.Groups[1].Value] = Parse-Long $match.Groups[2].Value
    }

    return $anchors
}

function Get-AnchorValue($Anchors, [string]$Name) {
    if ($Anchors.Contains($Name)) { return $Anchors[$Name] }
    return $null
}

function Get-SchemaSnapshotInventory([string]$RepoRoot) {
    $schemaDir = Join-Path $RepoRoot "schemas/tools/v1"
    if (-not (Test-Path -LiteralPath $schemaDir)) {
        return [ordered]@{
            path = $schemaDir
            count = 0
            files = @()
            missing = $true
        }
    }

    $files = @(Get-ChildItem -LiteralPath $schemaDir -Filter "*.schema.json" -File | Sort-Object Name)
    return [ordered]@{
        path = $schemaDir
        count = $files.Count
        files = @($files | ForEach-Object { $_.Name })
        missing = $false
    }
}

function Convert-CliAnalyzeOutput([string]$Text) {
    return [ordered]@{
        symbols = Match-Long $Text '^Symbols:\s+([\d,]+)'
        edges = Match-Long $Text '^Edges:\s+([\d,]+)'
        modules = Match-Long $Text '^Modules:\s+([\d,]+)'
        types = Match-Long $Text '^Types:\s+([\d,]+)'
        violations = Match-Long $Text '^Violations:\s+([\d,]+)'
        cycles = Match-Long $Text '^Cycles:\s+([\d,]+)'
    }
}

function Convert-DotnetTestOutput($Output) {
    $text = (@($Output) -join "`n")
    $match = [regex]::Match($text, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
    if (-not $match.Success) {
        return [ordered]@{
            parsed = $false
            reason = "Could not find the standard dotnet test summary line."
        }
    }

    return [ordered]@{
        parsed = $true
        failed = [int]$match.Groups[1].Value
        passed = [int]$match.Groups[2].Value
        skipped = [int]$match.Groups[3].Value
        total = [int]$match.Groups[4].Value
    }
}

function New-ComparisonValue([string]$Name, $Expected, $Actual) {
    return [ordered]@{
        name = $Name
        expected = $Expected
        actual = $Actual
        matches = ($null -ne $Expected -and $null -ne $Actual -and $Expected -eq $Actual)
    }
}

function New-SemanticComparison($StatusAnchors, $Actual) {
    $checks = @(
        New-ComparisonValue "symbols" (Get-AnchorValue $StatusAnchors "selfAnalyzeSymbols") $Actual.symbols
        New-ComparisonValue "edges" (Get-AnchorValue $StatusAnchors "selfAnalyzeEdges") $Actual.edges
        New-ComparisonValue "modules" (Get-AnchorValue $StatusAnchors "selfAnalyzeModules") $Actual.modules
        New-ComparisonValue "types" (Get-AnchorValue $StatusAnchors "selfAnalyzeTypes") $Actual.types
    )

    return [ordered]@{
        baseline = "docs/STATUS.md selfAnalyze* anchors from the production net8.0 lane"
        checks = $checks
        matchesStatusAnchors = (-not ($checks | Where-Object { -not $_.matches }))
    }
}

function New-TestComparison($StatusAnchors, $Summary) {
    $checks = @(
        New-ComparisonValue "total" (Get-AnchorValue $StatusAnchors "testCount") $Summary.total
    )

    return [ordered]@{
        baseline = "docs/STATUS.md testCount anchor from the production net8.0 lane"
        checks = $checks
        matchesStatusAnchors = (-not ($checks | Where-Object { -not $_.matches }))
    }
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

function Get-LatestStep($Report) {
    if ($Report.steps.Count -eq 0) { return $null }
    return $Report.steps[$Report.steps.Count - 1]
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
$statusAnchors = Read-StatusAnchors $repoRoot

$report = [ordered]@{
    schemaVersion = 1
    lane = "dotnet-experimental-target"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repoRoot = $repoRoot
    requestedWorkDir = $workDir
    workDir = $workDir
    workDirFallbackReason = $null
    sourceRoot = $experimentalSourceRoot
    checkedInSolution = $checkedInSolution
    solution = $solution
    targetFramework = $TargetFramework
    configuration = $Configuration
    restore = [ordered]@{
        ignoreFailedSources = [bool]$RestoreIgnoreFailedSources
    }
    status = "running"
    artifacts = [ordered]@{
        packages = $packagesDir
    }
    statusAnchors = $statusAnchors
    evidence = [ordered]@{
        schemaSnapshots = Get-SchemaSnapshotInventory $repoRoot
        testSummary = $null
        testComparison = $null
        semanticSelfAnalyze = $null
        semanticComparison = $null
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
    try {
        Remove-Item -LiteralPath $workDir -Recurse -Force
    }
    catch {
        $fallbackPathName = "{0}-{1}-{2}" -f $targetPathName, (Get-Date -Format "yyyyMMddHHmmss"), ([Guid]::NewGuid().ToString("N").Substring(0, 8))
        $fallbackWorkDir = Assert-UnderRoot $tempBase (Join-Path $tempBase $fallbackPathName)
        $report["workDirFallbackReason"] = "Could not clean the previous experimental work directory '$workDir': $($_.Exception.Message)"
        $workDir = $fallbackWorkDir
        $experimentalSourceRoot = Join-Path $workDir "source"
        $solution = Join-Path $experimentalSourceRoot "Lifeblood.sln"
        $report["workDir"] = $workDir
        $report["sourceRoot"] = $experimentalSourceRoot
        $report["solution"] = $solution
    }
}

New-Item -ItemType Directory -Force -Path $workDir | Out-Null
New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null
Copy-SourceTree $repoRoot $experimentalSourceRoot
$report.retargetedProjects = @(Set-CopiedProjectTargetFrameworks $experimentalSourceRoot $TargetFramework)

try {
    $restoreArgs = @(
        "restore", $solution,
        "--disable-parallel",
        "-maxcpucount:1",
        "-nodeReuse:false",
        "--nologo"
    )
    if ($RestoreIgnoreFailedSources) {
        $restoreArgs += "--ignore-failed-sources"
    }

    Invoke-Step $report "restore" $restoreArgs $workDir

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

        $testStep = Get-LatestStep $report
        $report.evidence.testSummary = Convert-DotnetTestOutput $testStep.output
        if ($report.evidence.testSummary.parsed) {
            $report.evidence.testComparison = New-TestComparison $statusAnchors $report.evidence.testSummary
        }
    }

    if (-not $SkipSemanticAnalyze) {
        $cliDll = Join-Path $experimentalSourceRoot "src/Lifeblood.CLI/bin/$Configuration/$TargetFramework/Lifeblood.CLI.dll"
        Invoke-Step $report "semantic-self-analyze" @(
            $cliDll,
            "analyze",
            "--project", $experimentalSourceRoot,
            "--rules", "lifeblood"
        ) $workDir

        $semanticStep = Get-LatestStep $report
        $semanticOutput = (@($semanticStep.output) -join "`n")
        $report.evidence.semanticSelfAnalyze = Convert-CliAnalyzeOutput $semanticOutput
        $report.evidence.semanticComparison = New-SemanticComparison $statusAnchors $report.evidence.semanticSelfAnalyze
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
