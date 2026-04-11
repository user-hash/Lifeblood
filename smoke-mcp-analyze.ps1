param(
    [Parameter(Mandatory=$true)][string]$Project,
    [string]$ServerDll = "$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll"
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "================================================================"
Write-Host "  MCP smoke test"
Write-Host "  Server : $ServerDll"
Write-Host "  Project: $Project"
Write-Host "================================================================"
Write-Host ""

if (-not (Test-Path $ServerDll)) {
    Write-Host "[fail] server dll not found"
    exit 1
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "`"$ServerDll`""
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi

# Forward stderr to our host so we can see progress phases as they happen
$null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action {
    if ($EventArgs.Data) { Write-Host "  [stderr] $($EventArgs.Data)" -ForegroundColor DarkGray }
}

$null = $proc.Start()
$proc.BeginErrorReadLine()

function Send-JsonRpc($obj) {
    $line = $obj | ConvertTo-Json -Depth 20 -Compress
    $proc.StandardInput.WriteLine($line)
    $proc.StandardInput.Flush()
}

function Read-JsonRpcResponse([int]$expectedId, [int]$timeoutSec = 300) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ($true) {
        if ((Get-Date) -gt $deadline) {
            throw "Timeout waiting for response id=$expectedId"
        }
        $line = $proc.StandardOutput.ReadLine()
        if ($line -eq $null) {
            throw "Server stdout closed before response id=$expectedId"
        }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $msg = $line | ConvertFrom-Json
        } catch {
            Write-Host "  [non-json stdout] $line" -ForegroundColor Yellow
            continue
        }
        if ($msg.id -eq $expectedId) { return $msg }
        # ignore notifications, progress, etc
    }
}

try {
    Write-Host "[mcp] initialize ..."
    Send-JsonRpc @{
        jsonrpc = "2.0"
        id = 1
        method = "initialize"
        params = @{
            protocolVersion = "2024-11-05"
            capabilities = @{}
            clientInfo = @{ name = "smoke-test"; version = "1.0" }
        }
    }
    $init = Read-JsonRpcResponse -expectedId 1 -timeoutSec 30
    if ($init.error) {
        Write-Host "[fail] initialize returned error: $($init.error | ConvertTo-Json)"
        exit 1
    }
    Write-Host ("[mcp] initialized -> protocol {0}" -f $init.result.protocolVersion)

    Write-Host "[mcp] notifications/initialized ..."
    Send-JsonRpc @{
        jsonrpc = "2.0"
        method = "notifications/initialized"
    }

    Write-Host "[mcp] calling lifeblood_analyze projectPath=$Project ..."
    Send-JsonRpc @{
        jsonrpc = "2.0"
        id = 2
        method = "tools/call"
        params = @{
            name = "lifeblood_analyze"
            arguments = @{ projectPath = $Project }
        }
    }

    $analyzeResp = Read-JsonRpcResponse -expectedId 2 -timeoutSec 300
    if ($analyzeResp.error) {
        Write-Host "[fail] analyze returned error: $($analyzeResp.error | ConvertTo-Json)"
        exit 1
    }

    Write-Host ""
    Write-Host "================================================================"
    Write-Host "  Raw analyze result content[0].text (first 4 KB)"
    Write-Host "================================================================"
    $content = $analyzeResp.result.content
    if ($content -eq $null -or $content.Count -eq 0) {
        Write-Host "[fail] result.content was null or empty"
        exit 1
    }
    $text = $content[0].text
    if ($text.Length -gt 4096) {
        Write-Host ($text.Substring(0, 4096))
        Write-Host "... (truncated)"
    } else {
        Write-Host $text
    }

    Write-Host ""
    Write-Host "================================================================"
    Write-Host "  Parsed usage block"
    Write-Host "================================================================"
    try {
        $parsed = $text | ConvertFrom-Json
    } catch {
        Write-Host "[fail] result.content[0].text is not JSON. Old dist still running?"
        exit 1
    }

    if (-not $parsed.PSObject.Properties['usage'] -or $parsed.usage -eq $null) {
        Write-Host "[fail] response has no 'usage' field. Feature NOT surfaced through MCP."
        exit 1
    }

    $u = $parsed.usage
    Write-Host ("  mode             : {0}" -f $parsed.mode)
    Write-Host ("  summary.symbols  : {0}" -f $parsed.summary.symbols)
    Write-Host ("  summary.edges    : {0}" -f $parsed.summary.edges)
    Write-Host ("  summary.modules  : {0}" -f $parsed.summary.modules)
    Write-Host ("  summary.types    : {0}" -f $parsed.summary.types)
    Write-Host ("  wallTimeMs       : {0}" -f $u.wallTimeMs)
    Write-Host ("  cpuTimeTotalMs   : {0}" -f $u.cpuTimeTotalMs)
    Write-Host ("  cpuTimeUserMs    : {0}" -f $u.cpuTimeUserMs)
    Write-Host ("  cpuTimeKernelMs  : {0}" -f $u.cpuTimeKernelMs)
    Write-Host ("  cpuUtilization%  : {0}" -f $u.cpuUtilizationPercent)
    Write-Host ("  peakWsMb         : {0}" -f $u.peakWorkingSetMb)
    Write-Host ("  peakPrivateMb    : {0}" -f $u.peakPrivateBytesMb)
    Write-Host ("  hostCores        : {0}" -f $u.hostLogicalCores)
    Write-Host ("  gc gen0/1/2      : {0} / {1} / {2}" -f $u.gcGen0Collections,$u.gcGen1Collections,$u.gcGen2Collections)
    if ($u.phases) {
        Write-Host "  phases:"
        foreach ($p in $u.phases) {
            Write-Host ("    {0,-20} : {1,10} ms" -f $p.name, $p.durationMs)
        }
    }
    Write-Host ""
    Write-Host "[pass] MCP end-to-end: usage block surfaced in lifeblood_analyze response"
}
finally {
    try {
        Send-JsonRpc @{ jsonrpc = "2.0"; id = 99; method = "shutdown" }
    } catch {}
    Start-Sleep -Milliseconds 200
    if (-not $proc.HasExited) {
        $proc.Kill()
    }
}
