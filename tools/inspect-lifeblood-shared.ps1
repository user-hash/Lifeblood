#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$Command,
    [string]$RuntimeRoot,
    [string]$CliCommand = 'lifeblood',
    [string]$SdkCommand,
    [string]$EnvironmentFile,
    [ValidateCount(0, 128)][string[]]$ClientToolNames,
    [ValidateLength(1, 256)][string]$ClientCatalogSource = 'caller-supplied catalog',
    [string]$BridgeObservationPath,
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$workspacePath = (Resolve-Path -LiteralPath $Workspace).Path
$process = $null
$deadline = [Diagnostics.Stopwatch]::StartNew()
$nextId = 0
$pendingFrame = ''
$maximumFrameCharacters = 4 * 1024 * 1024
$findings = [Collections.Generic.List[object]]::new()
$doctor = [ordered]@{ schemaVersion = 1; status = 'unavailable'; analysisRequested = $false }
$receipt = [ordered]@{
    inspectionMode = 'attachOnly'; proxyExecutablePath = $null; workspaceKey = $workspacePath
    observedAtUtc = [DateTime]::UtcNow.ToString('o'); capabilities = $null; doctor = $doctor
}
function Remaining-Milliseconds {
    $remaining = [int]($TimeoutSeconds * 1000 - $deadline.ElapsedMilliseconds)
    if ($remaining -le 0) { throw 'Inspection deadline exceeded' }
    return $remaining
}
function Command-Routes([string]$Name) {
    $routes = @(Get-Command $Name -CommandType Application -All -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source -Unique)
    return [ordered]@{ paths = @($routes | Select-Object -First 8); omittedCount = [Math]::Max(0, $routes.Count - 8) }
}
function Add-Finding([string]$Code, [string]$Message) {
    $findings.Add([ordered]@{ code = $Code; message = $Message })
}
function Read-InspectionFrame {
    $builder = [Text.StringBuilder]::new()
    $buffer = [char[]]::new(4096)
    while ($true) {
        if ($script:pendingFrame.Length -gt 0) {
            $chunk = $script:pendingFrame
            $script:pendingFrame = ''
        } else {
            $pending = $process.StandardOutput.ReadAsync($buffer, 0, $buffer.Length)
            if (-not $pending.Wait((Remaining-Milliseconds))) { throw 'Inspection RPC read deadline exceeded' }
            $read = $pending.GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'Inspection proxy closed stdout' }
            $chunk = [string]::new($buffer, 0, $read)
        }
        $newline = $chunk.IndexOf([char]10)
        $take = if ($newline -ge 0) { $newline } else { $chunk.Length }
        if ($builder.Length + $take -gt $maximumFrameCharacters) { throw 'Inspection RPC frame exceeds 4194304 UTF-16 characters' }
        [void]$builder.Append($chunk, 0, $take)
        if ($newline -ge 0) {
            $script:pendingFrame = $chunk.Substring($newline + 1)
            return $builder.ToString().TrimEnd([char]13)
        }
    }
}
function Invoke-InspectionRpc([string]$Method, $Parameters) {
    $script:nextId++
    $requestId = $script:nextId
    $frame = @{ jsonrpc = '2.0'; id = $requestId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 10 -Compress
    $process.StandardInput.WriteLine($frame)
    $process.StandardInput.Flush()
    while ($true) {
        $response = Read-InspectionFrame | ConvertFrom-Json -Depth 100
        if ($response.jsonrpc -ne '2.0') { throw 'Inspection received an invalid JSON-RPC revision' }
        if ($null -eq $response.id) { continue }
        if ($response.id -ne $requestId) { throw 'Inspection received an unexpected RPC response id' }
        if ($response.error) { throw ($response.error | ConvertTo-Json -Depth 10 -Compress) }
        return $response.result
    }
}
function Invoke-IdentityProbe([string]$ProbeCommand, [string[]]$Arguments) {
    $probe = $null
    $result = [ordered]@{ status = 'unavailable'; requestedCommand = $ProbeCommand }
    try {
        $null = Remaining-Milliseconds
        $result.executablePath = (Get-Command $ProbeCommand -CommandType Application -ErrorAction Stop).Source
        $start = [Diagnostics.ProcessStartInfo]::new($result.executablePath)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.RedirectStandardInput = $true
        $start.WorkingDirectory = $workspacePath
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        $probe = [Diagnostics.Process]::Start($start)
        $probe.StandardInput.Close()
        $result.processId = $probe.Id
        $probeClock = [Diagnostics.Stopwatch]::StartNew()
        $streams = @($probe.StandardOutput, $probe.StandardError) | ForEach-Object {
            $buffer = [char[]]::new(2048)
            [pscustomobject]@{ reader = $_; buffer = $buffer; text = [Text.StringBuilder]::new();
                task = $_.ReadAsync($buffer, 0, $buffer.Length); done = $false }
        }
        while (@($streams | Where-Object { -not $_.done }).Count -gt 0) {
            $active = @($streams | Where-Object { -not $_.done })
            $remaining = [int][Math]::Min((Remaining-Milliseconds), 10000 - $probeClock.ElapsedMilliseconds)
            if ($remaining -le 0) { throw 'Identity probe exceeded 10 seconds' }
            $index = [Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@($active | ForEach-Object task), $remaining)
            if ($index -lt 0) { throw 'Identity probe read deadline exceeded' }
            $stream = $active[$index]
            $read = $stream.task.GetAwaiter().GetResult()
            if ($read -eq 0) { $stream.done = $true; continue }
            if ($stream.text.Length + $read -gt 32768) { throw 'Identity probe output exceeds 32768 characters per stream' }
            [void]$stream.text.Append($stream.buffer, 0, $read)
            $stream.task = $stream.reader.ReadAsync($stream.buffer, 0, $stream.buffer.Length)
        }
        $remaining = [int][Math]::Min((Remaining-Milliseconds), [Math]::Max(0, 10000 - $probeClock.ElapsedMilliseconds))
        if (-not $probe.WaitForExit($remaining)) { throw 'Identity probe process exit deadline exceeded' }
        $result.exitCode = $probe.ExitCode
        $result.stdout = $streams[0].text.ToString()
        $result.stderr = $streams[1].text.ToString()
        if ($probe.ExitCode -ne 0) { throw "Identity probe exited with code $($probe.ExitCode)" }
        $result.status = 'available'
    } catch { $result.error = $_.Exception.Message }
    finally {
        if ($probe) {
            try {
                if (-not $probe.HasExited) {
                    $probe.Kill($true)
                    if (-not $probe.WaitForExit(3000)) { throw 'Identity probe cleanup did not terminate its owned process tree' }
                }
                $result.ownedProcessExited = $probe.HasExited
            } catch { $result.status = 'unavailable'; $result.cleanupError = $_.Exception.Message; throw }
            finally { $probe.Dispose() }
        }
    }
    return $result
}
try {
    $executable = (Get-Command $Command -CommandType Application -ErrorAction Stop).Source
    $receipt.proxyExecutablePath = $executable
    $doctor.routes = [ordered]@{ requestedProxy = $Command; resolvedProxy = $executable;
        pathCli = (Command-Routes 'lifeblood'); pathProxy = (Command-Routes 'lifeblood-mcp'); pathDotnet = (Command-Routes 'dotnet');
        configuredRuntimeRoot = $RuntimeRoot; inheritedDotnetHostPath = $env:DOTNET_HOST_PATH }
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true
    $start.WorkingDirectory = $workspacePath
    foreach ($argument in @('--shared', '--shared-key', $workspacePath)) { $start.ArgumentList.Add($argument) }
    $start.Environment['LIFEBLOOD_SHARED_DAEMON_AUTOSTART'] = '0'
    if ($RuntimeRoot) { $start.Environment['DOTNET_ROOT_X64'] = (Resolve-Path -LiteralPath $RuntimeRoot).Path }
    $process = [Diagnostics.Process]::Start($start)
    $initialize = Invoke-InspectionRpc 'initialize' @{
        protocolVersion = '2025-06-18'; capabilities = @{}
        clientInfo = @{ name = 'lifeblood-attach-only-inspection'; version = '1' }
    }
    if ($initialize.protocolVersion -notin @('2024-11-05', '2025-06-18') -or -not $initialize.serverInfo.name) { throw 'Inspection received an unsupported initialize response' }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    $result = Invoke-InspectionRpc 'tools/call' @{ name = 'lifeblood_capabilities'; arguments = @{} }
    if ($result.isError) { throw ($result | ConvertTo-Json -Depth 10 -Compress) }
    $payload = if ($result.structuredContent) { $result.structuredContent } else {
        ($result.content | Where-Object type -eq 'text' | Select-Object -First 1).text | ConvertFrom-Json -Depth 100
    }
    if ($payload.isError) { throw ($payload | ConvertTo-Json -Depth 10 -Compress) }
    if (-not $payload.server.version -or -not $payload.sharedService.active) { throw 'Inspection did not observe a live shared daemon identity' }
    $receipt.capabilities = $payload
    $doctor.proxy = [ordered]@{ processId = $process.Id; executablePath = $executable; autostartEnabled = $false;
        buildCompatibility = 'accepted by shared transport'; binaryVersion = 'not separately exposed by this observation' }
    $doctor.daemon = [ordered]@{ status = 'available'; server = $payload.server; service = $payload.sharedService; session = $payload.session }
    $doctor.connection = [ordered]@{ initialize = $initialize; structuredContentObserved = [bool]$result.structuredContent;
        endpoint = [ordered]@{ mode = $payload.sharedService.mode; workspaceRoot = $payload.sharedService.workspaceRoot;
            daemonInstanceId = $payload.sharedService.daemonInstanceId; processId = $payload.sharedService.processId } }
    $catalog = Invoke-InspectionRpc 'tools/list' @{}
    $toolNames = @($catalog.tools | ForEach-Object name | Sort-Object -Unique)
    $doctor.serverCatalog = [ordered]@{ status = 'observed'; count = $toolNames.Count; toolNames = $toolNames }
    $doctor.clientCatalog = if ($PSBoundParameters.ContainsKey('ClientToolNames')) {
        if (@($ClientToolNames | Where-Object { -not $_ -or $_.Length -gt 128 }).Count -gt 0) { throw 'Each supplied client tool name must contain 1..128 characters' }
        $names = @($ClientToolNames | Sort-Object -Unique)
        $missing = @($toolNames | Where-Object { $_ -notin $names })
        if ($missing.Count -gt 0) { Add-Finding 'ClientCatalogDifference' "$($missing.Count) server tools are absent from the supplied client catalog; client filtering or a stale catalog must be assessed by that client." }
        [ordered]@{ status = 'providedObservation'; source = $ClientCatalogSource; toolNames = $names;
            missingServerTools = $missing; independentlyRecaptured = $false }
    } else { [ordered]@{ status = 'unobserved'; reason = 'A server catalog does not establish another client catalog. Supply ClientToolNames from that client.' } }
    $doctor.environment = [ordered]@{ status = 'unobserved'; reason = 'Supply EnvironmentFile for a bounded retained compiler observation.' }
    if ($EnvironmentFile) {
        if ($payload.featureFlags.compilationEnvironmentObservation -and $payload.session.hasCompilationState) {
            $observed = Invoke-InspectionRpc 'tools/call' @{ name = 'lifeblood_diagnose'; arguments = @{
                mode = 'environment'; filePath = $EnvironmentFile; verbosity = 'compact'; referenceLimit = 0;
                expectedSnapshotId = $payload.session.snapshotId } }
            $body = if ($observed.structuredContent) { $observed.structuredContent } else { $observed.content[0].text | ConvertFrom-Json -Depth 100 }
            $doctor.environment = [ordered]@{ status = $(if ($observed.isError) { 'unavailable' } else { 'observed' }); result = $body }
        } else { $doctor.environment = [ordered]@{ status = 'unavailable'; reason = 'This service does not expose retained environment observation; no diagnostics or analysis fallback was attempted.' } }
        if ($doctor.environment.status -ne 'observed') { Add-Finding 'EnvironmentObservationUnavailable' 'The requested retained environment could not be observed; no compilation or analysis fallback was attempted.' }
    }
    $doctor.status = 'available'
} catch {
    $doctor.error = $_.Exception.Message
} finally {
    if ($process) {
        try {
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(3000)) {
                $process.Kill()
                if (-not $process.WaitForExit(3000)) { throw 'Inspection proxy did not exit within cleanup deadline' }
            }
            $doctor.ownedProxyExited = $process.HasExited
        } catch {
            $doctor.status = 'unavailable'; $doctor.cleanupError = $_.Exception.Message
        } finally { $process.Dispose() }
    }
}
try {
if ($doctor.status -eq 'available') {
    $cli = Invoke-IdentityProbe $CliCommand @('--version', '--json')
    if ($cli.status -eq 'available') {
        try {
            $cli.identity = $cli.stdout | ConvertFrom-Json -Depth 20
            if ($cli.identity -isnot [pscustomobject] -or $cli.identity.executionMode -ne 'standaloneCli' -or $cli.identity.version -isnot [string] -or -not $cli.identity.version) { throw 'CLI did not return a supported identity receipt' }
            if ($cli.identity.version -ne $receipt.capabilities.server.version) { Add-Finding 'CliDaemonVersionSkew' 'The selected CLI build differs from the live MCP daemon build.' }
        } catch { $cli.status = 'unavailable'; $cli.error = $_.Exception.Message }
    }
    if ($cli.status -ne 'available') { Add-Finding 'CliIdentityUnavailable' 'The selected CLI could not provide its identity; the daemon receipt remains independent.' }
    $doctor.cli = $cli
    if (-not $SdkCommand) { $SdkCommand = if ($env:DOTNET_HOST_PATH -and (Test-Path -LiteralPath $env:DOTNET_HOST_PATH -PathType Leaf)) { $env:DOTNET_HOST_PATH } else { 'dotnet' } }
    $sdk = Invoke-IdentityProbe $SdkCommand @('--version')
    $sdk.selectionScope = 'Explicit doctor probe in the workspace, or configured DOTNET_HOST_PATH/PATH fallback; not proof of the SDK used by an earlier analysis.'
    if ($sdk.status -eq 'available') {
        $version = $sdk.stdout.Trim()
        if ($version -match '^\d+\.\d+\.\d+[-+a-zA-Z0-9.]*$') { $sdk.version = $version }
        else { $sdk.status = 'unavailable'; $sdk.error = 'SDK probe did not return one SDK version line' }
    }
    if ($sdk.status -ne 'available') { Add-Finding 'SdkProbeUnavailable' 'The selected SDK probe failed; this does not invalidate the independent live daemon receipt.' }
    $doctor.sdk = $sdk
    $doctor.bridge = [ordered]@{ status = 'unobserved'; reason = 'No Unity operation was invoked. Supply a current bridge observation receipt to join its loaded identity.' }
    if ($BridgeObservationPath) {
        try {
            $file = Get-Item -LiteralPath $BridgeObservationPath
            if ($file.Length -gt 65536) { throw 'Bridge observation exceeds 64 KiB' }
            $bridge = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 30
            if ($bridge.data.result.result) { $bridge = $bridge.data.result.result }
            if ($bridge.schemaVersion -ne 1 -or -not $bridge.observedAtUtc -or -not $bridge.projectRoot) { throw 'Unsupported bridge observation receipt' }
            $observedAt = if ($bridge.observedAtUtc -is [DateTime]) { [DateTimeOffset]::new($bridge.observedAtUtc) }
                else { [DateTimeOffset]::Parse($bridge.observedAtUtc, [Globalization.CultureInfo]::InvariantCulture) }
            $doctor.bridge = [ordered]@{ status = 'providedObservation'; sourcePath = $file.FullName; observation = $bridge;
                ageSeconds = [Math]::Max(0, ([DateTimeOffset]::UtcNow - $observedAt).TotalSeconds);
                independentlyRecaptured = $false }
            $comparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
            if (-not $comparer.Equals([IO.Path]::GetFullPath($bridge.projectRoot), $workspacePath)) { Add-Finding 'BridgeWorkspaceDifference' 'The supplied bridge observation belongs to another workspace.' }
            if ($bridge.proxy.startedCommand -and $bridge.configuredCommand -ne $bridge.proxy.startedCommand) { Add-Finding 'BridgeCommandDifference' 'The bridge configured command differs from its started proxy command; compare resolved paths before inferring version skew.' }
        } catch { $doctor.bridge = [ordered]@{ status = 'unavailable'; error = $_.Exception.Message }; Add-Finding 'BridgeObservationUnavailable' 'The supplied bridge receipt could not be read or validated.' }
    }
    $paths = @($doctor.routes.pathCli.paths)
    if ($cli.executablePath -and @($paths | Where-Object { $_ -ne $cli.executablePath }).Count -gt 0) { Add-Finding 'AlternateCliRoutes' 'PATH exposes a different CLI path; path difference alone does not prove a version mismatch.' }
    if ($findings.Count -gt 0) { $doctor.status = 'availableWithFindings' }
}
} catch { $doctor.status = 'unavailable'; $doctor.error = $_.Exception.Message }
$doctor.findings = $findings.ToArray()
$doctor.elapsedMs = $deadline.ElapsedMilliseconds
$deadline.Stop()
$receipt | ConvertTo-Json -Depth 100
if ($doctor.status -eq 'unavailable') { exit 1 }
