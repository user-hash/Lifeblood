<#
.SYNOPSIS
  Measures private and shared MCP process-tree memory for one and two clients.

.DESCRIPTION
  Drives the published Lifeblood MCP server through real JSON-RPC requests.
  Each run measures four steady-state topologies against the same workspace:

    private-one  one stdio server with one retained analysis
    private-two  two stdio servers with two retained analyses
    shared-one   one explicit daemon plus one thin stdio proxy
    shared-two   the same daemon plus two thin stdio proxies

  The shared daemon is always started explicitly with a unique pipe name and
  retained by process handle. The benchmark never relies on detached proxy
  auto-start, so every process is deterministically stopped in a finally block.
  Peak values are the maximum combined process-tree sample for the topology;
  steady values are sampled only after the requested graph is committed.

  This is a release-receipt harness, not a portable CI performance assertion.
  Compare repeated runs on the same host, server build, workspace state, and
  analysis profile set.
#>
param(
    [string]$ServerDll = "artifacts/runtime-benchmarks/mcp-publish/net8.0/Lifeblood.Server.Mcp.dll",
    [string]$Project = "",
    [string[]]$DefineProfiles = @(),
    [string]$OutputPath = "artifacts/runtime-benchmarks/lifeblood-shared-host-benchmark.json",
    [string]$BenchmarkRunId = "",
    [string]$DotnetExe = "dotnet",
    [int]$Runs = 2,
    [int]$TimeoutSec = 300,
    [int]$SteadySampleMilliseconds = 2000,
    [int]$SampleIntervalMilliseconds = 100
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

function Quote-ProcessArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Start-McpProcess([string[]]$Arguments, [string]$WorkingDirectory) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $DotnetExe
    $allArguments = @($serverDllFull) + @($Arguments)
    $psi.Arguments = (($allArguments | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' ')
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) {
        throw "Failed to start Lifeblood MCP process."
    }

    return [pscustomobject]@{
        Process = $process
        Stderr = $process.StandardError.ReadToEndAsync()
    }
}

function Stop-McpProcess($Owner) {
    if ($null -eq $Owner) {
        return
    }

    $process = $Owner.Process
    try {
        try { $process.StandardInput.Close() } catch { }
        if (-not $process.HasExited -and -not $process.WaitForExit(1500)) {
            try { $process.Kill() } catch { }
        }
        if (-not $process.HasExited) {
            [void]$process.WaitForExit(10000)
        }
        if ($Owner.Stderr.IsCompleted) {
            [void]$Owner.Stderr.Result
        }
    }
    finally {
        $process.Dispose()
    }
}

function Stop-McpProcesses([System.Collections.IList]$Owners) {
    for ($i = $Owners.Count - 1; $i -ge 0; $i--) {
        Stop-McpProcess $Owners[$i]
    }
    $Owners.Clear()
}

function Wait-ForPipe([string]$PipeName, [int]$TimeoutMilliseconds) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $lastFailure = $null

    while ([DateTime]::UtcNow -lt $deadline) {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
            ".",
            $PipeName,
            [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $pipe.Connect(250)
            return
        }
        catch {
            $lastFailure = $_.Exception
            Start-Sleep -Milliseconds 50
        }
        finally {
            $pipe.Dispose()
        }
    }

    throw [TimeoutException]::new(
        "Named pipe '$PipeName' did not accept connections within $TimeoutMilliseconds ms.",
        $lastFailure)
}

function New-MemoryState {
    return [pscustomobject]@{
        PeakWorkingSetBytes = 0L
        PeakPrivateBytes = 0L
        Samples = 0
    }
}

function Add-MemorySample([object[]]$Owners, $State) {
    $workingSet = 0L
    $privateBytes = 0L

    foreach ($owner in $Owners) {
        $process = $owner.Process
        try {
            if ($process.HasExited) {
                continue
            }
            $process.Refresh()
            $workingSet += $process.WorkingSet64
            $privateBytes += $process.PrivateMemorySize64
        }
        catch { }
    }

    if ($workingSet -gt $State.PeakWorkingSetBytes) {
        $State.PeakWorkingSetBytes = $workingSet
    }
    if ($privateBytes -gt $State.PeakPrivateBytes) {
        $State.PeakPrivateBytes = $privateBytes
    }
    $State.Samples++

    return [pscustomobject]@{
        WorkingSetBytes = $workingSet
        PrivateBytes = $privateBytes
    }
}

function Get-Median([long[]]$Values) {
    if ($Values.Count -eq 0) {
        return 0L
    }

    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [long]$sorted[$middle]
    }

    return [long](($sorted[$middle - 1] + $sorted[$middle]) / 2)
}

function Measure-SteadyState([object[]]$Owners, $State) {
    $workingSets = @()
    $privateValues = @()
    $deadline = [DateTime]::UtcNow.AddMilliseconds($SteadySampleMilliseconds)

    while ([DateTime]::UtcNow -lt $deadline) {
        $sample = Add-MemorySample $Owners $State
        $workingSets += [long]$sample.WorkingSetBytes
        $privateValues += [long]$sample.PrivateBytes
        Start-Sleep -Milliseconds $SampleIntervalMilliseconds
    }

    return [pscustomobject]@{
        sampleCount = $workingSets.Count
        medianWorkingSetMb = [Math]::Round((Get-Median $workingSets) / 1MB, 1)
        maxWorkingSetMb = [Math]::Round(($workingSets | Measure-Object -Maximum).Maximum / 1MB, 1)
        medianPrivateBytesMb = [Math]::Round((Get-Median $privateValues) / 1MB, 1)
        maxPrivateBytesMb = [Math]::Round(($privateValues | Measure-Object -Maximum).Maximum / 1MB, 1)
    }
}

function Invoke-JsonRpc(
    $Owner,
    [long]$Id,
    [string]$Method,
    $Params,
    [object[]]$SampleOwners,
    $MemoryState,
    [int]$RequestTimeoutSec) {

    $request = [ordered]@{
        jsonrpc = "2.0"
        id = $Id
        method = $Method
    }
    if ($null -ne $Params) {
        $request.params = $Params
    }

    $json = $request | ConvertTo-Json -Depth 30 -Compress
    $Owner.Process.StandardInput.WriteLine($json)
    $Owner.Process.StandardInput.Flush()

    $pending = $Owner.Process.StandardOutput.ReadLineAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($RequestTimeoutSec)
    while (-not $pending.Wait($SampleIntervalMilliseconds)) {
        [void](Add-MemorySample $SampleOwners $MemoryState)
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Timed out waiting for JSON-RPC id $Id ($Method)."
        }
        if ($Owner.Process.HasExited) {
            $stderr = if ($Owner.Stderr.IsCompleted) { $Owner.Stderr.Result } else { "<still draining>" }
            throw "MCP process $($Owner.Process.Id) exited while waiting for $Method. stderr: $stderr"
        }
    }

    $line = $pending.Result
    if ([string]::IsNullOrWhiteSpace($line)) {
        $stderr = if ($Owner.Stderr.IsCompleted) { $Owner.Stderr.Result } else { "<still draining>" }
        throw "MCP process $($Owner.Process.Id) closed stdout while waiting for $Method. stderr: $stderr"
    }

    [void](Add-MemorySample $SampleOwners $MemoryState)
    $response = $line | ConvertFrom-Json
    if ([long]$response.id -ne $Id) {
        throw "Expected JSON-RPC id $Id, received '$($response.id)'."
    }
    if ($null -ne $response.error) {
        throw "JSON-RPC $Method failed: $($response.error | ConvertTo-Json -Depth 10 -Compress)"
    }
    return $response
}

function Initialize-McpClient($Owner, [object[]]$SampleOwners, $MemoryState) {
    $params = [ordered]@{
        protocolVersion = "2024-11-05"
        capabilities = [ordered]@{}
        clientInfo = [ordered]@{
            name = "lifeblood-shared-host-benchmark"
            version = "1"
        }
    }
    [void](Invoke-JsonRpc $Owner 1 "initialize" $params $SampleOwners $MemoryState 30)
}

function Invoke-Analyze($Owner, [object[]]$SampleOwners, $MemoryState) {
    $arguments = [ordered]@{ projectPath = $projectPath }
    if ($DefineProfiles.Count -gt 0) {
        $arguments.defineProfiles = @($DefineProfiles)
    }
    $params = [ordered]@{
        name = "lifeblood_analyze"
        arguments = $arguments
    }
    $response = Invoke-JsonRpc $Owner 2 "tools/call" $params $SampleOwners $MemoryState $TimeoutSec
    $payload = $response.result.content[0].text | ConvertFrom-Json
    if ($payload.mode -ne "full") {
        throw "Expected a fresh full analyze, received mode '$($payload.mode)'."
    }
    return $payload
}

function Get-Capabilities($Owner, [object[]]$SampleOwners, $MemoryState, [long]$Id) {
    $params = [ordered]@{
        name = "lifeblood_capabilities"
        arguments = [ordered]@{}
    }
    $response = Invoke-JsonRpc $Owner $Id "tools/call" $params $SampleOwners $MemoryState 30
    return ($response.result.content[0].text | ConvertFrom-Json)
}

function Get-AnalysisSummary($Payload) {
    return [pscustomobject]@{
        symbols = [long]$Payload.summary.symbols
        edges = [long]$Payload.summary.edges
        modules = [long]$Payload.summary.modules
        types = [long]$Payload.summary.types
        files = [long]$Payload.summary.files
        profileCount = [long]$Payload.summary.profileCount
        activeProfiles = @($Payload.summary.activeProfiles)
    }
}

function Assert-AnalysisSummaryEqual($Expected, $Actual, [string]$Context) {
    $expectedJson = (Get-AnalysisSummary $Expected) | ConvertTo-Json -Depth 10 -Compress
    $actualJson = (Get-AnalysisSummary $Actual) | ConvertTo-Json -Depth 10 -Compress
    if ($expectedJson -cne $actualJson) {
        throw "$Context analyzed a different semantic graph. Expected $expectedJson; actual $actualJson."
    }
}

function New-TopologyResult(
    [string]$Name,
    [object[]]$Owners,
    $State,
    $Steady,
    [long]$Generation,
    [long]$AnalyzeWallTimeMs,
    $AnalysisPayload) {

    return [pscustomobject]@{
        topology = $Name
        processCount = $Owners.Count
        processIds = @($Owners | ForEach-Object { $_.Process.Id })
        analysisGeneration = $Generation
        analysisSummary = Get-AnalysisSummary $AnalysisPayload
        analyzeWallTimeMs = $AnalyzeWallTimeMs
        peakCombinedWorkingSetMb = [Math]::Round($State.PeakWorkingSetBytes / 1MB, 1)
        peakCombinedPrivateBytesMb = [Math]::Round($State.PeakPrivateBytes / 1MB, 1)
        memorySamples = $State.Samples
        steady = $Steady
    }
}

function Invoke-BenchmarkRun([int]$RunNumber) {
    Write-Host "Run $RunNumber/$Runs - private topology"
    $privateOwners = [System.Collections.ArrayList]::new()
    $privateResults = @()
    try {
        $privateOne = Start-McpProcess @() $repoRoot
        [void]$privateOwners.Add($privateOne)
        $privateOneState = New-MemoryState
        Initialize-McpClient $privateOne @($privateOne) $privateOneState
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $privateOneAnalyze = Invoke-Analyze $privateOne @($privateOne) $privateOneState
        $sw.Stop()
        $privateOneSteady = Measure-SteadyState @($privateOne) $privateOneState
        $privateResults += New-TopologyResult "private-one" @($privateOne) $privateOneState $privateOneSteady ([long]$privateOneAnalyze.envelope.analysisGeneration) ([long]$sw.ElapsedMilliseconds) $privateOneAnalyze

        $privateTwo = Start-McpProcess @() $repoRoot
        [void]$privateOwners.Add($privateTwo)
        $privateTwoState = New-MemoryState
        Initialize-McpClient $privateTwo @($privateOwners) $privateTwoState
        $sw.Restart()
        $privateTwoAnalyze = Invoke-Analyze $privateTwo @($privateOwners) $privateTwoState
        $sw.Stop()
        Assert-AnalysisSummaryEqual $privateOneAnalyze $privateTwoAnalyze "Second private client"
        $privateTwoSteady = Measure-SteadyState @($privateOwners) $privateTwoState
        $privateOneCapabilities = Get-Capabilities $privateOne @($privateOwners) $privateTwoState 3
        if ([long]$privateOneCapabilities.session.analysisGeneration -ne 1 -or
            [long]$privateTwoAnalyze.envelope.analysisGeneration -ne 1) {
            throw "Private servers did not retain independent generation-1 sessions."
        }
        $privateResults += New-TopologyResult "private-two" @($privateOwners) $privateTwoState $privateTwoSteady ([long]$privateTwoAnalyze.envelope.analysisGeneration) ([long]$sw.ElapsedMilliseconds) $privateTwoAnalyze
    }
    finally {
        Stop-McpProcesses $privateOwners
    }

    Write-Host "Run $RunNumber/$Runs - shared topology"
    $sharedOwners = [System.Collections.ArrayList]::new()
    $sharedResults = @()
    $pipeName = "lifeblood-shared-benchmark-$([Guid]::NewGuid().ToString('N'))"
    try {
        $daemon = Start-McpProcess @("--shared-daemon", $pipeName) $repoRoot
        [void]$sharedOwners.Add($daemon)
        Wait-ForPipe $pipeName 10000

        $sharedOne = Start-McpProcess @("--shared", "--shared-pipe", $pipeName) $repoRoot
        [void]$sharedOwners.Add($sharedOne)
        $sharedOneState = New-MemoryState
        Initialize-McpClient $sharedOne @($sharedOwners) $sharedOneState
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $sharedAnalyze = Invoke-Analyze $sharedOne @($sharedOwners) $sharedOneState
        $sw.Stop()
        Assert-AnalysisSummaryEqual $privateOneAnalyze $sharedAnalyze "Shared daemon"
        $sharedOneSteady = Measure-SteadyState @($sharedOwners) $sharedOneState
        $sharedResults += New-TopologyResult "shared-one" @($sharedOwners) $sharedOneState $sharedOneSteady ([long]$sharedAnalyze.envelope.analysisGeneration) ([long]$sw.ElapsedMilliseconds) $sharedAnalyze

        $sharedTwo = Start-McpProcess @("--shared", "--shared-pipe", $pipeName) $repoRoot
        [void]$sharedOwners.Add($sharedTwo)
        $sharedTwoState = New-MemoryState
        Initialize-McpClient $sharedTwo @($sharedOwners) $sharedTwoState
        $sharedCapabilities = Get-Capabilities $sharedTwo @($sharedOwners) $sharedTwoState 3
        $sharedTwoSteady = Measure-SteadyState @($sharedOwners) $sharedTwoState
        if ([long]$sharedCapabilities.session.analysisGeneration -ne
            [long]$sharedAnalyze.envelope.analysisGeneration) {
            throw "Second shared client did not observe the daemon's committed generation."
        }
        $sharedResults += New-TopologyResult "shared-two" @($sharedOwners) $sharedTwoState $sharedTwoSteady ([long]$sharedCapabilities.session.analysisGeneration) 0 $sharedAnalyze
    }
    finally {
        Stop-McpProcesses $sharedOwners
    }

    $privateTwo = $privateResults | Where-Object { $_.topology -eq "private-two" }
    $sharedTwo = $sharedResults | Where-Object { $_.topology -eq "shared-two" }
    $savedPrivateMb = [Math]::Round(
        $privateTwo.steady.medianPrivateBytesMb - $sharedTwo.steady.medianPrivateBytesMb,
        1)
    $sharedFraction = if ($privateTwo.steady.medianPrivateBytesMb -gt 0) {
        [Math]::Round(
            $sharedTwo.steady.medianPrivateBytesMb / $privateTwo.steady.medianPrivateBytesMb,
            4)
    } else { 0 }

    return [pscustomobject]@{
        run = $RunNumber
        topologies = @($privateResults + $sharedResults)
        twoClientComparison = [pscustomobject]@{
            steadyPrivateBytesSavedMb = $savedPrivateMb
            sharedToPrivateSteadyPrivateRatio = $sharedFraction
        }
    }
}

function Get-GitReceipt([string]$Path) {
    try {
        $root = (& git -C $Path rev-parse --show-toplevel 2>$null | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($root)) {
            return [pscustomobject]@{ state = "unavailable" }
        }
        $commit = (& git -C $root rev-parse HEAD 2>$null | Select-Object -First 1)
        $dirtyLines = @(& git -C $root status --porcelain 2>$null)
        return [pscustomobject]@{
            state = "available"
            root = $root.Trim()
            commit = $commit.Trim()
            dirty = $dirtyLines.Count -gt 0
            dirtyEntryCount = $dirtyLines.Count
        }
    }
    catch {
        return [pscustomobject]@{ state = "unavailable" }
    }
}

$repoRoot = Get-RepoRoot
$projectPath = if ([string]::IsNullOrWhiteSpace($Project)) {
    $repoRoot
} else {
    (Resolve-Path $Project).Path
}
$serverDllFull = if ([System.IO.Path]::IsPathRooted($ServerDll)) {
    $ServerDll
} else {
    Join-Path $repoRoot $ServerDll
}
if (-not (Test-Path -LiteralPath $serverDllFull)) {
    throw "Server dll not found: $serverDllFull. Publish first with: dotnet publish src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj -c Release -f net8.0 -o artifacts/runtime-benchmarks/mcp-publish/net8.0"
}
if ($Runs -lt 1) { throw "Runs must be at least 1." }
if ($TimeoutSec -lt 10) { throw "TimeoutSec must be at least 10." }
if ($SteadySampleMilliseconds -lt 100) { throw "SteadySampleMilliseconds must be at least 100." }
if ($SampleIntervalMilliseconds -lt 25) { throw "SampleIntervalMilliseconds must be at least 25." }

$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $repoRoot $OutputPath
}
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$resolvedRunId = if ([string]::IsNullOrWhiteSpace($BenchmarkRunId)) {
    [Guid]::NewGuid().ToString("N")
} else {
    $BenchmarkRunId
}

$runResults = @()
for ($run = 1; $run -le $Runs; $run++) {
    $runResults += Invoke-BenchmarkRun $run
}

$report = [ordered]@{
    schemaVersion = 1
    benchmarkRunId = $resolvedRunId
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    server = [ordered]@{
        path = $serverDllFull
        sha256 = (Get-FileHash -LiteralPath $serverDllFull -Algorithm SHA256).Hash
        assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($serverDllFull).Version.ToString()
        fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($serverDllFull).FileVersion
        informationalVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($serverDllFull).ProductVersion
        sourceControl = Get-GitReceipt $repoRoot
    }
    workspace = [ordered]@{
        path = $projectPath
        defineProfiles = @($DefineProfiles)
        sourceControl = Get-GitReceipt $projectPath
    }
    host = [ordered]@{
        osDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        frameworkDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        logicalCores = [Environment]::ProcessorCount
        processor = (Get-CimInstance -Class Win32_Processor | Select-Object -First 1).Name.Trim()
        totalRamGb = [Math]::Round((Get-CimInstance -Class Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
    }
    settings = [ordered]@{
        runs = $Runs
        timeoutSec = $TimeoutSec
        steadySampleMilliseconds = $SteadySampleMilliseconds
        sampleIntervalMilliseconds = $SampleIntervalMilliseconds
    }
    runs = $runResults
}

$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $outputFullPath -Encoding UTF8

Write-Host ""
Write-Host "Shared-host benchmark complete:"
foreach ($runResult in $runResults) {
    $privateTwo = $runResult.topologies | Where-Object { $_.topology -eq "private-two" }
    $sharedTwo = $runResult.topologies | Where-Object { $_.topology -eq "shared-two" }
    $summary = "  run {0}: private-two={1} MB, shared-two={2} MB, saved={3} MB" -f $runResult.run, $privateTwo.steady.medianPrivateBytesMb, $sharedTwo.steady.medianPrivateBytesMb, $runResult.twoClientComparison.steadyPrivateBytesSavedMb
    Write-Host $summary
}
Write-Host "Wrote $outputFullPath"
