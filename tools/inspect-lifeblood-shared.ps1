#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][string]$Command,
    [string]$RuntimeRoot,
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$workspacePath = (Resolve-Path -LiteralPath $Workspace).Path
$executable = (Get-Command $Command -CommandType Application -ErrorAction Stop).Source
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.WorkingDirectory = $workspacePath
foreach ($argument in @('--shared', '--shared-key', $workspacePath)) { $start.ArgumentList.Add($argument) }
$start.Environment['LIFEBLOOD_SHARED_DAEMON_AUTOSTART'] = '0'
if ($RuntimeRoot) { $start.Environment['DOTNET_ROOT_X64'] = (Resolve-Path -LiteralPath $RuntimeRoot).Path }
$process = $null
$deadline = [Diagnostics.Stopwatch]::StartNew()
$nextId = 0
function Invoke-InspectionRpc([string]$Method, $Parameters) {
    $script:nextId++
    $requestId = $script:nextId
    $frame = @{ jsonrpc = '2.0'; id = $requestId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 10 -Compress
    $process.StandardInput.WriteLine($frame)
    $process.StandardInput.Flush()
    while ($true) {
        $remaining = [int]($TimeoutSeconds * 1000 - $deadline.ElapsedMilliseconds)
        if ($remaining -le 0) { throw "Inspection timed out during $Method" }
        $pending = $process.StandardOutput.ReadLineAsync()
        if (-not $pending.Wait($remaining)) { throw "Inspection timed out during $Method" }
        if ($null -eq $pending.Result) { throw "Inspection proxy closed stdout during $Method" }
        $response = $pending.Result | ConvertFrom-Json -Depth 100
        if ($null -eq $response.id) { continue }
        if ($response.id -ne $requestId) { throw 'Inspection received an unexpected RPC response id' }
        if ($response.error) { throw ($response.error | ConvertTo-Json -Depth 10 -Compress) }
        return $response.result
    }
}
try {
    $process = [Diagnostics.Process]::Start($start)
    $null = Invoke-InspectionRpc 'initialize' @{
        protocolVersion = '2024-11-05'; capabilities = @{}
        clientInfo = @{ name = 'lifeblood-attach-only-inspection'; version = '1' }
    }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $result = Invoke-InspectionRpc 'tools/call' @{ name = 'lifeblood_capabilities'; arguments = @{} }
    if ($result.isError) { throw ($result | ConvertTo-Json -Depth 10 -Compress) }
    $payload = ($result.content | Where-Object type -eq 'text' | Select-Object -First 1).text | ConvertFrom-Json -Depth 100
    if ($payload.isError) { throw ($payload | ConvertTo-Json -Depth 10 -Compress) }
    [ordered]@{
        inspectionMode = 'attachOnly'
        proxyExecutablePath = $executable
        workspaceKey = $workspacePath
        observedAtUtc = [DateTime]::UtcNow.ToString('o')
        capabilities = $payload
    } | ConvertTo-Json -Depth 100
} finally {
    if ($process) {
        try {
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(3000)) {
                $process.Kill()
                if (-not $process.WaitForExit(3000)) { Write-Warning 'Inspection proxy did not exit within cleanup deadline' }
            }
        } finally { $process.Dispose() }
    }
    $deadline.Stop()
}
