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
    [int]$Runs = 2,
    [int]$TimeoutSec = 180
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

$repoRoot = Get-RepoRoot
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

function Invoke-OneRun([hashtable]$Env) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.Arguments = '"' + $serverDllFull + '"'
    $psi.WorkingDirectory = $repoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($k in $Env.Keys) { $psi.Environment[$k] = [string]$Env[$k] }

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$proc.Start()

    $stdin = $proc.StandardInput
    $stdin.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')
    $stdin.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $stdin.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"lifeblood_analyze","arguments":{"projectPath":"' + $projectJson + '"}}}')
    $stdin.Flush()

    $peakWs = 0L
    $peakPriv = 0L
    $samples = 0
    $analyzeSeen = $false
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
    # Report the worst (max) peak across runs — the ceiling is the contract.
    $maxPriv = ($runResults | Measure-Object -Property peakPrivateBytesMb -Maximum).Maximum
    $maxWs   = ($runResults | Measure-Object -Property peakWorkingSetMb   -Maximum).Maximum
    $results += [pscustomobject]@{
        config                = $cfg.name
        env                   = $cfg.env
        runs                  = $runResults
        maxPeakPrivateBytesMb = $maxPriv
        maxPeakWorkingSetMb   = $maxWs
        allAnalyzeCompleted   = (-not ($runResults | Where-Object { -not $_.analyzeCompleted }))
    }
}

$report = [ordered]@{
    schemaVersion  = 1
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repoRoot       = $repoRoot
    project        = $projectPath
    serverDll      = $serverDllFull
    runsPerConfig  = $Runs
    host           = [ordered]@{
        osDescription        = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        frameworkDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        logicalCores         = [Environment]::ProcessorCount
    }
    configs        = $results
}

$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $outputFullPath -Encoding UTF8

Write-Host ""
Write-Host "── MCP-server peak memory by GC config (max over $Runs runs) ──"
$results | ForEach-Object {
    Write-Host ("  {0,-16}  priv={1,7} MB   ws={2,7} MB" -f $_.config, $_.maxPeakPrivateBytesMb, $_.maxPeakWorkingSetMb)
}
Write-Host ""
Write-Host "Wrote MCP GC benchmark report to $outputFullPath"
