param(
    [string]$TargetFramework = "net11.0",
    [string]$Project = ".",
    [string]$OutputPath = "artifacts/runtime-benchmarks/lifeblood-runtime-async-benchmark.json",
    [string]$BenchmarkRunId = "",
    [string]$DotnetExe = "dotnet",
    [string[]]$Workloads = @("self-analyze", "cli-help"),
    [int]$McpRuns = 1,
    [int]$TimeoutSec = 180,
    [switch]$SkipTests,
    [switch]$SkipMcp,
    [switch]$RestoreIgnoreFailedSources,
    [string[]]$PackageSources = @(),
    [string]$DotnetCliHome = "",
    [string]$WorkDirRoot = "",
    [switch]$FailWhenUnavailable
)

$ErrorActionPreference = "Stop"
$compilerFeatures = "runtime-async=on"

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

function Invoke-DotnetLines([string[]]$Arguments) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $script:DotnetExe @Arguments 2>&1 } finally { $ErrorActionPreference = $prevEap }
    if ($LASTEXITCODE -ne 0) {
        throw "$script:DotnetExe $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$output"
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

function Join-ProcessArguments([string[]]$Arguments) {
    return (($Arguments | ForEach-Object {
        $arg = [string]$_
        if ($arg.Length -eq 0) { return '""' }
        if ($arg -notmatch '[\s"]') { return $arg }
        '"' + ($arg -replace '"', '\"') + '"'
    }) -join ' ')
}

function Invoke-MeasuredProcess([string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    if ($startInfo.PSObject.Properties.Name -contains "ArgumentList" -and $null -ne $startInfo.ArgumentList) {
        foreach ($arg in $Arguments) {
            [void]$startInfo.ArgumentList.Add($arg)
        }
    } else {
        $startInfo.Arguments = Join-ProcessArguments $Arguments
    }

    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $peakWorkingSet = 0L
    $peakPrivateBytes = 0L
    $samples = 0

    while (-not $process.HasExited) {
        try {
            $process.Refresh()
            if ($process.WorkingSet64 -gt $peakWorkingSet) { $peakWorkingSet = $process.WorkingSet64 }
            if ($process.PrivateMemorySize64 -gt $peakPrivateBytes) { $peakPrivateBytes = $process.PrivateMemorySize64 }
            $samples++
        } catch {
        }

        Start-Sleep -Milliseconds 250
    }

    $process.WaitForExit()
    $stopwatch.Stop()

    return [ordered]@{
        command = @($FileName) + $Arguments
        exitCode = $process.ExitCode
        wallTimeMs = [long]$stopwatch.ElapsedMilliseconds
        cpuTotalMs = [long]$process.TotalProcessorTime.TotalMilliseconds
        cpuUserMs = [long]$process.UserProcessorTime.TotalMilliseconds
        cpuKernelMs = [long]$process.PrivilegedProcessorTime.TotalMilliseconds
        peakWorkingSetBytes = $peakWorkingSet
        peakPrivateBytes = $peakPrivateBytes
        memorySamples = $samples
        stdout = $stdoutTask.GetAwaiter().GetResult()
        stderr = $stderrTask.GetAwaiter().GetResult()
    }
}

function Parse-Long([string]$Text) {
    return [long]($Text -replace ',', '')
}

function Match-Long([string]$Text, [string]$Pattern) {
    $match = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $match.Success) { return $null }
    return Parse-Long $match.Groups[1].Value
}

function Convert-CliAnalyzeOutput([string]$Text) {
    return [ordered]@{
        symbols = Match-Long $Text '^Symbols:\s+([\d,]+)'
        edges = Match-Long $Text '^Edges:\s+([\d,]+)'
        modules = Match-Long $Text '^Modules:\s+([\d,]+)'
        types = Match-Long $Text '^Types:\s+([\d,]+)'
        wallTimeMs = Match-Long $Text 'Wall time\s+:\s+([\d,]+)\s+ms'
        peakWorkingSetMb = Match-Long $Text 'Peak working set\s+:\s+([\d,]+)\s+MB'
        peakPrivateBytesMb = Match-Long $Text 'Peak private bytes\s+:\s+([\d,]+)\s+MB'
    }
}

function Write-Report($Report, [string]$Path) {
    $dir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $Report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

$repoRoot = Get-RepoRoot
$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
$outputDir = Split-Path -Parent $outputFullPath
if ([string]::IsNullOrWhiteSpace($outputDir)) { $outputDir = $repoRoot }
if ([string]::IsNullOrWhiteSpace($DotnetCliHome)) {
    $DotnetCliHome = Join-Path $outputDir "dotnet-cli-home"
}
if ([string]::IsNullOrWhiteSpace($WorkDirRoot)) {
    $WorkDirRoot = Join-Path $outputDir "runtime-async-work"
}
New-Item -ItemType Directory -Force -Path $DotnetCliHome | Out-Null
New-Item -ItemType Directory -Force -Path $WorkDirRoot | Out-Null
[Environment]::SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1", "Process")
[Environment]::SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1", "Process")
[Environment]::SetEnvironmentVariable("DOTNET_NOLOGO", "1", "Process")
[Environment]::SetEnvironmentVariable("DOTNET_CLI_HOME", $DotnetCliHome, "Process")
$resolvedBenchmarkRunId = if ([string]::IsNullOrWhiteSpace($BenchmarkRunId)) { [Guid]::NewGuid().ToString("N") } else { $BenchmarkRunId }
$resolvedProject = if ([string]::IsNullOrWhiteSpace($Project)) { $repoRoot } else { (Resolve-Path $Project).Path }
$sdkLines = @(Invoke-DotnetLines @("--list-sdks"))
$runtimeLines = @(Invoke-DotnetLines @("--list-runtimes"))
$sdkMajors = @($sdkLines | ForEach-Object { Get-SdkMajor $_ } | Where-Object { $null -ne $_ })
$targetMajor = Get-TargetMajor $TargetFramework
$highestSdkMajor = if ($sdkMajors.Count -gt 0) { ($sdkMajors | Measure-Object -Maximum).Maximum } else { 0 }
$experimentalReportPath = Join-Path $outputDir "lifeblood-runtime-async-experimental-target.json"
$mcpReportPath = Join-Path $outputDir "lifeblood-runtime-async-mcp-gc.json"

$report = [ordered]@{
    schemaVersion = 1
    lane = "runtime-async-benchmark"
    benchmarkRunId = $resolvedBenchmarkRunId
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repoRoot = $repoRoot
    project = $resolvedProject
    targetFramework = $TargetFramework
    compilerFeatures = $compilerFeatures
    dotnetExe = $DotnetExe
    dotnetCliHome = $DotnetCliHome
    workDirRoot = $WorkDirRoot
    status = "running"
    host = [ordered]@{
        dotnetSdks = $sdkLines
        dotnetRuntimes = $runtimeLines
        highestSdkMajor = $highestSdkMajor
        dotnetCliTelemetryOptOut = [Environment]::GetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "Process")
        dotnetCliHome = [Environment]::GetEnvironmentVariable("DOTNET_CLI_HOME", "Process")
    }
    supportGate = [ordered]@{
        requiredSdkMajor = $targetMajor
        supported = $highestSdkMajor -ge $targetMajor
    }
    measurementAvailability = [ordered]@{
        cliAnalyze = "captured when the target SDK is installed"
        cliHelp = "captured when the target SDK is installed"
        mcpRetainedReadSideTools = "delegates to run-lifeblood-mcp-gc-benchmark.ps1 unless -SkipMcp is set"
        productionAdoption = "never inferred from this lane without tests, schema, and semantic receipts"
    }
    experimentalTargetReport = $experimentalReportPath
    mcpGcReport = if ($SkipMcp) { $null } else { $mcpReportPath }
    packageSources = @($PackageSources)
    workloads = @()
    notes = @(
        "Runtime Async is injected only into the temporary copied source tree via <Features>runtime-async=on</Features>.",
        "Production projects remain net8.0 and do not opt into Runtime Async.",
        "The lane records an honest skip when no SDK can build the requested target framework."
    )
}

if ($highestSdkMajor -lt $targetMajor) {
    $report.status = "skipped"
    $report.skipReason = "No installed .NET SDK can build $TargetFramework with $compilerFeatures. Highest SDK major: $highestSdkMajor."
    Write-Report $report $outputFullPath
    Write-Host $report.skipReason
    if ($FailWhenUnavailable) { exit 1 }
    return
}

$experimentalScript = Join-Path $repoRoot "tools/dotnet-lanes/run-lifeblood-experimental-target.ps1"
$experimentalArgs = @{
    TargetFramework = $TargetFramework
    OutputPath = $experimentalReportPath
    CompilerFeatures = $compilerFeatures
    DotnetExe = $DotnetExe
    DotnetCliHome = $DotnetCliHome
    WorkDirRoot = $WorkDirRoot
    PackageSources = $PackageSources
    SkipPack = $true
}
if ($SkipTests) { $experimentalArgs.SkipTests = $true }
if ($RestoreIgnoreFailedSources) { $experimentalArgs.RestoreIgnoreFailedSources = $true }

& $experimentalScript @experimentalArgs
$experimental = Get-Content -LiteralPath $experimentalReportPath -Raw | ConvertFrom-Json
$report.experimentalTargetStatus = $experimental.status
if ($experimental.status -ne "passed") {
    $report.status = "failed"
    Write-Report $report $outputFullPath
    throw "Runtime Async experimental target lane did not pass. See $experimentalReportPath."
}

$sourceRoot = [string]$experimental.sourceRoot
$cliDll = Join-Path $sourceRoot "src/Lifeblood.CLI/bin/Release/$TargetFramework/Lifeblood.CLI.dll"
$serverDll = Join-Path $sourceRoot "src/Lifeblood.Server.Mcp/bin/Release/$TargetFramework/Lifeblood.Server.Mcp.dll"

foreach ($workload in $Workloads) {
    $arguments = switch ($workload) {
        "self-analyze" { @($cliDll, "analyze", "--project", $sourceRoot) }
        "analyze" { @($cliDll, "analyze", "--project", $resolvedProject) }
        "cli-help" { @($cliDll, "--help") }
        default { throw "Unsupported Runtime Async workload: $workload" }
    }

    $measurement = Invoke-MeasuredProcess $DotnetExe $arguments $repoRoot
    $combinedOutput = $measurement.stdout + "`n" + $measurement.stderr
    $report.workloads += [pscustomobject]@{
        name = $workload
        process = [pscustomobject]$measurement
        cli = if ($workload -in @("self-analyze", "analyze")) { Convert-CliAnalyzeOutput $combinedOutput } else { $null }
    }

    if ($measurement.exitCode -ne 0) {
        $report.status = "failed"
        Write-Report $report $outputFullPath
        throw "Runtime Async workload $workload failed with exit code $($measurement.exitCode)."
    }
}

if (-not $SkipMcp) {
    $mcpScript = Join-Path $repoRoot "tools/runtime-benchmarks/run-lifeblood-mcp-gc-benchmark.ps1"
    & $mcpScript `
        -ServerDll $serverDll `
        -Project $sourceRoot `
        -OutputPath $mcpReportPath `
        -BenchmarkRunId $resolvedBenchmarkRunId `
        -DotnetExe $DotnetExe `
        -Runs $McpRuns `
        -TimeoutSec $TimeoutSec
}

$report.status = "passed"
Write-Report $report $outputFullPath
Write-Host "Wrote Runtime Async benchmark report to $outputFullPath"
