param(
    [string]$Project = "$PSScriptRoot",
    [string]$ServerDll = "$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll"
)

# P2 (v0.6.7) end-to-end dogfood. Verifies INV-ENVELOPE-001 over the
# JSON-RPC wire surface: every read-side tool ships a top-level envelope
# with truthTier / confidence / staleness / evidence / limitations.

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ServerDll)) {
    Write-Host "[fail] server dll not found at $ServerDll"
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
        if ((Get-Date) -gt $deadline) { throw "Timeout waiting for response id=$expectedId" }
        $line = $proc.StandardOutput.ReadLine()
        if ($line -eq $null) { throw "Server stdout closed before response id=$expectedId" }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $msg = $line | ConvertFrom-Json } catch {
            Write-Host "  [non-json] $line" -ForegroundColor Yellow
            continue
        }
        if ($msg.id -eq $expectedId) { return $msg }
    }
}

function CallTool([int]$id, [string]$name, $callArgs) {
    Send-JsonRpc @{
        jsonrpc = "2.0"; id = $id; method = "tools/call"
        params = @{ name = $name; arguments = $callArgs }
    }
    $resp = Read-JsonRpcResponse -expectedId $id -timeoutSec 300
    if ($resp.error) { throw "$name error: $($resp.error | ConvertTo-Json)" }
    return $resp.result.content[0].text
}

$failures = @()
function Assert($condition, $msg) {
    if (-not $condition) { $script:failures += $msg; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
    else { Write-Host "  [ok]   $msg" -ForegroundColor Green }
}

function CheckEnvelope($json, $tool) {
    $parsed = $json | ConvertFrom-Json
    Assert ($null -ne $parsed.envelope) "$tool response carries envelope"
    if ($null -ne $parsed.envelope) {
        Assert ($null -ne $parsed.envelope.truthTier) "$tool envelope.truthTier present (got $($parsed.envelope.truthTier))"
        Assert ($null -ne $parsed.envelope.confidence) "$tool envelope.confidence present"
        Assert ($null -ne $parsed.envelope.evidenceSource) "$tool envelope.evidenceSource present"
        Assert ($null -ne $parsed.envelope.stalenessSeconds) "$tool envelope.stalenessSeconds present"
        Assert ($null -ne $parsed.envelope.filesChangedSinceAnalyze) "$tool envelope.filesChangedSinceAnalyze present"
    }
    return $parsed
}

try {
    Write-Host "==== P2 dogfood (truth envelope) ===="
    Send-JsonRpc @{ jsonrpc = "2.0"; id = 1; method = "initialize"; params = @{
        protocolVersion = "2024-11-05"; capabilities = @{}
        clientInfo = @{ name = "p2-dogfood"; version = "1.0" } } }
    $null = Read-JsonRpcResponse -expectedId 1 -timeoutSec 30
    Send-JsonRpc @{ jsonrpc = "2.0"; method = "notifications/initialized" }

    Write-Host ""
    Write-Host "[1] analyze envelope"
    $a = CheckEnvelope (CallTool 2 "lifeblood_analyze" @{ projectPath = $Project }) "lifeblood_analyze"

    Write-Host ""
    Write-Host "[2] lookup -> Semantic / Proven"
    $r = CheckEnvelope (CallTool 3 "lifeblood_lookup" @{ symbolId = "type:Lifeblood.Domain.Graph.Symbol" }) "lifeblood_lookup"
    Assert ($r.envelope.truthTier -eq "Semantic") ('lookup truthTier=Semantic (got ' + $r.envelope.truthTier + ')')
    Assert ($r.envelope.confidence -eq "Proven")  ('lookup confidence=Proven (got ' + $r.envelope.confidence + ')')

    Write-Host ""
    Write-Host "[3] blast_radius -> Derived"
    $r = CheckEnvelope (CallTool 4 "lifeblood_blast_radius" @{ symbolId = "type:Lifeblood.Domain.Graph.Symbol" }) "lifeblood_blast_radius"
    Assert ($r.envelope.truthTier -eq "Derived") ('blast_radius truthTier=Derived (got ' + $r.envelope.truthTier + ')')

    Write-Host ""
    Write-Host "[4] dead_code -> Heuristic / Advisory + limitations populated"
    $r = CheckEnvelope (CallTool 5 "lifeblood_dead_code" @{}) "lifeblood_dead_code"
    Assert ($r.envelope.truthTier -eq "Heuristic")  ('dead_code truthTier=Heuristic (got ' + $r.envelope.truthTier + ')')
    Assert ($r.envelope.confidence -eq "Advisory")   ('dead_code confidence=Advisory (got ' + $r.envelope.confidence + ')')
    Assert (($r.envelope.limitations | Measure-Object).Count -ge 1) "dead_code envelope.limitations is non-empty"

    Write-Host ""
    Write-Host "[5] search -> Heuristic / Advisory"
    $r = CheckEnvelope (CallTool 6 "lifeblood_search" @{ query = "ResponseEnvelope" }) "lifeblood_search"
    Assert ($r.envelope.truthTier -eq "Heuristic") ('search truthTier=Heuristic (got ' + $r.envelope.truthTier + ')')

    Write-Host ""
    Write-Host "[6] resolve_short_name envelope"
    $null = CheckEnvelope (CallTool 7 "lifeblood_resolve_short_name" @{ name = "Symbol" }) "lifeblood_resolve_short_name"

    Write-Host ""
    Write-Host "[7] dependants envelope (renamed payload shape)"
    $r = CheckEnvelope (CallTool 8 "lifeblood_dependants" @{ symbolId = "type:Lifeblood.Domain.Graph.Symbol" }) "lifeblood_dependants"
    Assert ($null -ne $r.dependants) "dependants response includes dependants array"

    Write-Host ""
    Write-Host "[8] file_impact envelope -> Derived"
    $r = CheckEnvelope (CallTool 9 "lifeblood_file_impact" @{ filePath = "src/Lifeblood.Domain/Graph/Symbol.cs" }) "lifeblood_file_impact"
    Assert ($r.envelope.truthTier -eq "Derived") ('file_impact truthTier=Derived (got ' + $r.envelope.truthTier + ')')

    Write-Host ""
    Write-Host "[9] invariant_check audit -> Semantic"
    $r = CheckEnvelope (CallTool 10 "lifeblood_invariant_check" @{ mode = "audit" }) "lifeblood_invariant_check"
    Assert ($r.envelope.truthTier -eq "Semantic") ('invariant_check truthTier=Semantic (got ' + $r.envelope.truthTier + ')')

    Write-Host ""
    Write-Host "[10] context envelope"
    $null = CheckEnvelope (CallTool 11 "lifeblood_context" @{}) "lifeblood_context"

    Write-Host ""
    if ($failures.Count -eq 0) {
        Write-Host "==== P2 DOGFOOD: ALL GREEN ====" -ForegroundColor Green
        exit 0
    } else {
        Write-Host "==== P2 DOGFOOD: FAILURES ====" -ForegroundColor Red
        foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
        exit 1
    }
}
finally {
    try { Send-JsonRpc @{ jsonrpc = "2.0"; id = 999; method = "shutdown" } } catch {}
    Start-Sleep -Milliseconds 200
    if (-not $proc.HasExited) { $proc.Kill() }
}
