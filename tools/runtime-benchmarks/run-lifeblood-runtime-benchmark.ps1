param(
    [string]$Project = ".",
    [string]$OutputPath = "artifacts/runtime-benchmarks/lifeblood-runtime-benchmark.json",
    [string]$BenchmarkRunId = "",
    [string[]]$TargetFrameworks = @("net8.0"),
    [ValidateSet("self-analyze", "analyze", "context", "self-context", "incremental-noop", "cli-help")]
    [string[]]$Workloads = @("self-analyze"),
    [switch]$SkipPublish,
    [switch]$RuntimeInfoOnly
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    $dir = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    return $dir.Path
}

function Read-ProjectTargetFrameworks([string]$ProjectPath) {
    [xml]$xml = Get-Content -LiteralPath $ProjectPath
    $values = New-Object System.Collections.Generic.List[string]

    foreach ($propertyGroup in $xml.Project.PropertyGroup) {
        if ($propertyGroup.TargetFramework) {
            [void]$values.Add([string]$propertyGroup.TargetFramework)
        }
        if ($propertyGroup.TargetFrameworks) {
            foreach ($tfm in ([string]$propertyGroup.TargetFrameworks).Split(';')) {
                if (-not [string]::IsNullOrWhiteSpace($tfm)) {
                    [void]$values.Add($tfm.Trim())
                }
            }
        }
    }

    return $values | Sort-Object -Unique
}

function Invoke-DotnetLines([string[]]$Arguments) {
    $output = & dotnet @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$output"
    }
    return @($output)
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
    $parseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $phases = @()
    foreach ($match in [regex]::Matches($Text, '^\s+([A-Za-z0-9_.-]+)\s+:\s+([\d,]+)\s+ms\s*$', [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
        $phases += [pscustomobject]@{
            name = $match.Groups[1].Value
            durationMs = Parse-Long $match.Groups[2].Value
        }
    }

    $gcMatch = [regex]::Match($Text, 'GC collections\s+:\s+gen0=(\d+)\s+gen1=(\d+)\s+gen2=(\d+)', [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $gc = $null
    if ($gcMatch.Success) {
        $gc = [pscustomobject]@{
            gen0 = [int]$gcMatch.Groups[1].Value
            gen1 = [int]$gcMatch.Groups[2].Value
            gen2 = [int]$gcMatch.Groups[3].Value
        }
    }

    $result = [pscustomobject]@{
        symbols = Match-Long $Text '^Symbols:\s+([\d,]+)'
        edges = Match-Long $Text '^Edges:\s+([\d,]+)'
        modules = Match-Long $Text '^Modules:\s+([\d,]+)'
        types = Match-Long $Text '^Types:\s+([\d,]+)'
        usage = [pscustomobject]@{
            wallTimeMs = Match-Long $Text 'Wall time\s+:\s+([\d,]+)\s+ms'
            cpuTotalMs = Match-Long $Text 'CPU total\s+:\s+([\d,]+)\s+ms'
            cpuUserMs = Match-Long $Text 'user mode\s+:\s+([\d,]+)\s+ms'
            cpuKernelMs = Match-Long $Text 'kernel mode\s+:\s+([\d,]+)\s+ms'
            peakWorkingSetMb = Match-Long $Text 'Peak working set\s+:\s+([\d,]+)\s+MB'
            peakPrivateBytesMb = Match-Long $Text 'Peak private bytes\s+:\s+([\d,]+)\s+MB'
            gcCollections = $gc
            phases = $phases
        }
    }
    $parseStopwatch.Stop()
    $result | Add-Member -NotePropertyName parseDurationMs -NotePropertyValue ([long]$parseStopwatch.ElapsedMilliseconds)
    return $result
}

function Join-ProcessArguments([string[]]$Arguments) {
    return (($Arguments | ForEach-Object {
        $arg = [string]$_
        if ($arg.Length -eq 0) {
            return '""'
        }

        if ($arg -notmatch '[\s"]') {
            return $arg
        }

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

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    return [pscustomobject]@{
        command = @($FileName) + $Arguments
        exitCode = $process.ExitCode
        wallTimeMs = [long]$stopwatch.ElapsedMilliseconds
        cpuTotalMs = [long]$process.TotalProcessorTime.TotalMilliseconds
        cpuUserMs = [long]$process.UserProcessorTime.TotalMilliseconds
        cpuKernelMs = [long]$process.PrivilegedProcessorTime.TotalMilliseconds
        peakWorkingSetBytes = $peakWorkingSet
        peakPrivateBytes = $peakPrivateBytes
        memorySamples = $samples
        stdout = $stdout
        stderr = $stderr
    }
}

$repoRoot = Get-RepoRoot
$resolvedBenchmarkRunId = if ([string]::IsNullOrWhiteSpace($BenchmarkRunId)) { [Guid]::NewGuid().ToString("N") } else { $BenchmarkRunId }
$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
$outputDir = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

$cliProject = Join-Path $repoRoot "src/Lifeblood.CLI/Lifeblood.CLI.csproj"
$supportedTfms = @(Read-ProjectTargetFrameworks $cliProject)
$resolvedProject = if ($Workloads -contains "self-analyze") {
    $repoRoot
} else {
    (Resolve-Path $Project).Path
}

$report = [ordered]@{
    schemaVersion = 1
    benchmarkRunId = $resolvedBenchmarkRunId
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repoRoot = $repoRoot
    project = $resolvedProject
    host = [ordered]@{
        osDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        frameworkDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        logicalCores = [Environment]::ProcessorCount
        dotnetSdks = @(Invoke-DotnetLines @("--list-sdks"))
        dotnetRuntimes = @(Invoke-DotnetLines @("--list-runtimes"))
    }
    measurementAvailability = [ordered]@{
        wallTimeMs = "captured"
        cpuTimeMs = "captured"
        peakWorkingSetBytes = "captured"
        peakPrivateBytes = "captured"
        gcCollections = "parsed from CLI AnalysisUsage when present"
        allocatedBytes = "captured by MCP analyze phase telemetry; process harness does not expose per-workload allocations yet"
        jsonParseSerializeTimeMs = "parseDurationMs captured for CLI analyze output parsing"
        roslynLoadTimeMs = "covered by CLI analyze phase timing until adapter-level spans are exported"
        graphBuildTimeMs = "covered by analyze phase timing until adapter-level spans are exported"
        resolverIndexTimeMs = "not captured yet"
        mcpDispatchLatencyMs = "captured per retained read-side tool by tools/runtime-benchmarks/run-lifeblood-mcp-gc-benchmark.ps1"
    }
    targetFrameworks = @()
}

if ($RuntimeInfoOnly) {
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $outputFullPath -Encoding UTF8
    Write-Host "Wrote runtime info to $outputFullPath"
    return
}

foreach ($tfm in $TargetFrameworks) {
    $targetResult = [ordered]@{
        targetFramework = $tfm
        supportedByCliProject = $supportedTfms -contains $tfm
        workloads = @()
    }

    if (-not ($supportedTfms -contains $tfm)) {
        $targetResult.skipped = "CLI project does not target $tfm. Supported: $($supportedTfms -join ', ')"
        $report.targetFrameworks += [pscustomobject]$targetResult
        continue
    }

    $publishDir = Join-Path $repoRoot "artifacts/runtime-benchmarks/publish/$tfm"
    if (-not $SkipPublish) {
        Invoke-DotnetLines @(
            "publish",
            "src/Lifeblood.CLI/Lifeblood.CLI.csproj",
            "-c", "Release",
            "-f", $tfm,
            "-o", $publishDir,
            "--nologo",
            "-v", "q"
        ) | Out-Null
    }

    foreach ($workload in $Workloads) {
        $dll = Join-Path $publishDir "Lifeblood.CLI.dll"
        $workloadProject = if ($workload -in @("self-analyze", "self-context", "cli-help")) { $repoRoot } else { $resolvedProject }
        $arguments = switch ($workload) {
            "self-analyze" { @($dll, "analyze", "--project", $repoRoot) }
            "analyze" { @($dll, "analyze", "--project", $workloadProject) }
            "self-context" { @($dll, "context", "--project", $repoRoot) }
            "context" { @($dll, "context", "--project", $workloadProject) }
            "incremental-noop" { @($dll, "verify", "--incremental", "--project", $workloadProject) }
            "cli-help" { @($dll, "--help") }
            default { throw "Unsupported workload: $workload" }
        }

        $measurement = Invoke-MeasuredProcess "dotnet" $arguments $repoRoot
        $combinedOutput = $measurement.stdout + "`n" + $measurement.stderr
        $parsed = if ($workload -in @("self-analyze", "analyze")) {
            Convert-CliAnalyzeOutput $combinedOutput
        } else {
            $null
        }

        $targetResult.workloads += [pscustomobject]@{
            name = $workload
            project = $workloadProject
            category = switch ($workload) {
                { $_ -in @("self-analyze", "analyze") } { "analyze"; break }
                { $_ -in @("self-context", "context") } { "context"; break }
                "incremental-noop" { "incremental"; break }
                "cli-help" { "packaging-smoke"; break }
                default { "unknown"; break }
            }
            process = [pscustomobject]@{
                command = $measurement.command
                exitCode = $measurement.exitCode
                wallTimeMs = $measurement.wallTimeMs
                cpuTotalMs = $measurement.cpuTotalMs
                cpuUserMs = $measurement.cpuUserMs
                cpuKernelMs = $measurement.cpuKernelMs
                peakWorkingSetBytes = $measurement.peakWorkingSetBytes
                peakPrivateBytes = $measurement.peakPrivateBytes
                memorySamples = $measurement.memorySamples
            }
            cli = $parsed
            stderr = $measurement.stderr
        }

        if ($measurement.exitCode -ne 0) {
            $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $outputFullPath -Encoding UTF8
            throw "Workload $workload failed for $tfm with exit code $($measurement.exitCode). Partial report written to $outputFullPath"
        }
    }

    $report.targetFrameworks += [pscustomobject]$targetResult
}

$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $outputFullPath -Encoding UTF8
Write-Host "Wrote benchmark report to $outputFullPath"
