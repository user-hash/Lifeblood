<#
.SYNOPSIS
  Measures the MCP-server-process peak memory under three GC configurations
  while it performs a real self-analyze. Complements
  run-lifeblood-runtime-benchmark.ps1, which measures the CLI (Workstation GC)
  and explicitly leaves mcpDispatchLatencyMs / the server GC path uncaptured.

.DESCRIPTION
  The DATAS heap-right-sizing win (System.GC.DynamicAdaptationMode=1, shipped in
  Lifeblood.Server.Mcp.csproj at c3b25dc) applies to the SERVER process, which
  runs Server GC. Server GC without DATAS reserves per-core heaps and balloons
  working set on many-core hosts; DATAS right-sizes it. This harness launches
  the SAME published server dll three times with GC env overrides
  (DOTNET_gcServer / DOTNET_GCDynamicAdaptationMode override runtimeconfig.json),
  drives one initialize + lifeblood_analyze over stdin, and samples peak
  WorkingSet64 + PrivateMemorySize64 during the graph build.

  Runs against an ISOLATED published server (artifacts/.../mcp-publish), never
  the live dist/ the editor's MCP client is connected to.
#>
param(
    [string]$ServerDll = "artifacts/runtime-benchmarks/mcp-publish/net8.0/Lifeblood.Server.Mcp.dll",
    [string]$Project = "",
    [string]$OutputPath = "artifacts/runtime-benchmarks/lifeblood-mcp-gc-benchmark.json",
    [string]$BenchmarkRunId = "",
    [int]$Runs = 2,
    [int]$TimeoutSec = 180,
    [switch]$SkipReadSideTools
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

$repoRoot = Get-RepoRoot
$resolvedBenchmarkRunId = if ([string]::IsNullOrWhiteSpace($BenchmarkRunId)) { [Guid]::NewGuid().ToString("N") } else { $BenchmarkRunId }
$projectPath = if ([string]::IsNullOrWhiteSpace($Project)) { $repoRoot } else { (Resolve-Path $Project).Path }
$serverDllFull = if ([System.IO.Path]::IsPathRooted($ServerDll)) { $ServerDll } else { Join-Path $repoRoot $ServerDll }
if (-not (Test-Path -LiteralPath $serverDllFull)) {
    throw "Server dll not found: $serverDllFull. Publish first: dotnet publish src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj -c Release -f net8.0 -o artifacts/runtime-benchmarks/mcp-publish/net8.0"
}

$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
$outputDir = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDir)) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }

# Escape the project path for embedding in a JSON string literal.
$projectJson = ($projectPath -replace '\\', '\\') -replace '"', '\"'

$configs = @(
    [ordered]@{ name = "workstation";     env = @{ DOTNET_gcServer = "0" } },
    [ordered]@{ name = "server-no-datas"; env = @{ DOTNET_gcServer = "1"; DOTNET_GCDynamicAdaptationMode = "0" } },
    [ordered]@{ name = "server-datas";    env = @{ DOTNET_gcServer = "1"; DOTNET_GCDynamicAdaptationMode = "1" } }
)

$readSideToolCalls = @(
    [ordered]@{ id = 3; name = "lifeblood_capabilities"; arguments = [ordered]@{} },
    [ordered]@{ id = 4; name = "lifeblood_context"; arguments = [ordered]@{ summarize = $true } },
    [ordered]@{ id = 5; name = "lifeblood_cycles"; arguments = [ordered]@{ summarize = $true; maxResults = 10 } },
    [ordered]@{ id = 6; name = "lifeblood_dead_code"; arguments = [ordered]@{ summarize = $true; maxResults = 10 } }
)

function Invoke-OneRun($EnvironmentOverrides) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.Arguments = '"' + $serverDllFull + '"'
    $psi.WorkingDirectory = $repoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $previousEnvironment = @{}
    foreach ($k in $EnvironmentOverrides.Keys) {
        $name = [string]$k
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        [Environment]::SetEnvironmentVariable($name, [string]$EnvironmentOverrides[$k], "Process")
    }
    $previousEnvironment["LIFEBLOOD_BENCHMARK_RUN_ID"] = [Environment]::GetEnvironmentVariable("LIFEBLOOD_BENCHMARK_RUN_ID", "Process")
    [Environment]::SetEnvironmentVariable("LIFEBLOOD_BENCHMARK_RUN_ID", $resolvedBenchmarkRunId, "Process")

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        [void]$proc.Start()
    }
    finally {
        foreach ($name in $previousEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable([string]$name, $previousEnvironment[$name], "Process")
        }
    }

    $stdin = $proc.StandardInput
    $stdin.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')
    $stdin.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $stdin.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"lifeblood_analyze","arguments":{"projectPath":"' + $projectJson + '"}}}')
    $stdin.Flush()

    $peakWs = 0L
    $peakPriv = 0L
    $samples = 0
    $analyzeSeen = $false
    $readSideDispatches = @()
    $reader = $proc.StandardOutput
    $pending = $reader.ReadLineAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)

    while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        try {
            $proc.Refresh()
            if ($proc.WorkingSet64 -gt $peakWs) { $peakWs = $proc.WorkingSet64 }
            if ($proc.PrivateMemorySize64 -gt $peakPriv) { $peakPriv = $proc.PrivateMemorySize64 }
            $samples++
        } catch { }

        if ($pending.Wait(100)) {
            $line = $pending.Result
            if ($null -eq $line) { break }   # stdout closed
            if ($line -match '"id"\s*:\s*2') { $analyzeSeen = $true; break }
            $pending = $reader.ReadLineAsync()
        }
    }

    if ($analyzeSeen -and -not $SkipReadSideTools) {
        foreach ($toolCall in $readSideToolCalls) {
            $request = [ordered]@{
                jsonrpc = "2.0"
                id = $toolCall.id
                method = "tools/call"
                params = [ordered]@{
                    name = $toolCall.name
                    arguments = $toolCall.arguments
                }
            } | ConvertTo-Json -Depth 20 -Compress

            $toolStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $stdin.WriteLine($request)
            $stdin.Flush()

            $toolSeen = $false
            $responseBytes = 0
            $pending = $reader.ReadLineAsync()
            $toolDeadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(10, [Math]::Min($TimeoutSec, 60)))

            while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $toolDeadline) {
                try {
                    $proc.Refresh()
                    if ($proc.WorkingSet64 -gt $peakWs) { $peakWs = $proc.WorkingSet64 }
                    if ($proc.PrivateMemorySize64 -gt $peakPriv) { $peakPriv = $proc.PrivateMemorySize64 }
                    $samples++
                } catch { }

                if ($pending.Wait(100)) {
                    $line = $pending.Result
                    if ($null -eq $line) { break }
                    if ($line -match ('"id"\s*:\s*' + $toolCall.id)) {
                        $toolSeen = $true
                        $responseBytes = [System.Text.Encoding]::UTF8.GetByteCount($line)
                        break
                    }
                    $pending = $reader.ReadLineAsync()
                }
            }

            $toolStopwatch.Stop()
            $readSideDispatches += [pscustomobject]@{
                toolName = $toolCall.name
                responseSeen = $toolSeen
                dispatchLatencyMs = [long]$toolStopwatch.ElapsedMilliseconds
                responseBytes = $responseBytes
            }
        }
    }

    # Capture one final post-analyze peak, then shut the server down cleanly.
    try { $proc.Refresh(); if ($proc.PrivateMemorySize64 -gt $peakPriv) { $peakPriv = $proc.PrivateMemorySize64 }; if ($proc.WorkingSet64 -gt $peakWs) { $peakWs = $proc.WorkingSet64 } } catch { }
    $sw.Stop()
    try { $stdin.Close() } catch { }
    if (-not $proc.WaitForExit(10000)) { try { $proc.Kill() } catch { } }

    return [pscustomobject]@{
        analyzeCompleted   = $analyzeSeen
        wallTimeMs         = [long]$sw.ElapsedMilliseconds
        peakWorkingSetMb   = [math]::Round($peakWs / 1MB, 1)
        peakPrivateBytesMb = [math]::Round($peakPriv / 1MB, 1)
        memorySamples      = $samples
        readSideDispatches = $readSideDispatches
        allReadSideCompleted = ($SkipReadSideTools -or -not ($readSideDispatches | Where-Object { -not $_.responseSeen }))
    }
}

$results = @()
foreach ($cfg in $configs) {
    Write-Host ("=== {0}  (env: {1}) ===" -f $cfg.name, (($cfg.env.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '))
    $runResults = @()
    for ($i = 1; $i -le $Runs; $i++) {
        $r = Invoke-OneRun $cfg.env
        Write-Host ("  run {0}: priv={1} MB  ws={2} MB  analyze={3}  {4} ms  ({5} samples)" -f $i, $r.peakPrivateBytesMb, $r.peakWorkingSetMb, $r.analyzeCompleted, $r.wallTimeMs, $r.memorySamples)
        $runResults += $r
    }
    # Report the worst (max) peak across runs; the ceiling is the contract.
    $maxPriv = ($runResults | Measure-Object -Property peakPrivateBytesMb -Maximum).Maximum
    $maxWs   = ($runResults | Measure-Object -Property peakWorkingSetMb   -Maximum).Maximum
    $results += [pscustomobject]@{
        config                = $cfg.name
        env                   = $cfg.env
        runs                  = $runResults
        maxPeakPrivateBytesMb = $maxPriv
        maxPeakWorkingSetMb   = $maxWs
        allAnalyzeCompleted   = (-not ($runResults | Where-Object { -not $_.analyzeCompleted }))
        allReadSideCompleted  = (-not ($runResults | Where-Object { -not $_.allReadSideCompleted }))
    }
}

$report = [ordered]@{
    schemaVersion  = 1
    benchmarkRunId = $resolvedBenchmarkRunId
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repoRoot       = $repoRoot
    project        = $projectPath
    serverDll      = $serverDllFull
    runsPerConfig  = $Runs
    readSideTools  = if ($SkipReadSideTools) { @() } else { @($readSideToolCalls | ForEach-Object { $_.name }) }
    host           = [ordered]@{
        osDescription        = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        frameworkDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        logicalCores         = [Environment]::ProcessorCount
    }
    configs        = $results
}

$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $outputFullPath -Encoding UTF8

Write-Host ""
Write-Host "-- MCP-server peak memory by GC config (max over $Runs runs) --"
$results | ForEach-Object {
    Write-Host ("  {0,-16}  priv={1,7} MB   ws={2,7} MB" -f $_.config, $_.maxPeakPrivateBytesMb, $_.maxPeakWorkingSetMb)
}
Write-Host ""
Write-Host "Wrote MCP GC benchmark report to $outputFullPath"
