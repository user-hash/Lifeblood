param(
    [string]$Project = "$PSScriptRoot",
    [string]$ServerDll = "$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll"
)

# P1 (v0.6.6) end-to-end dogfood. Drives a fresh MCP server over stdio,
# analyzes Lifeblood itself, then exercises every behavioral change shipped
# in P1: BUG-002 (kind correction), BUG-015 (compile_check filePath), BUG-016
# (diagnose filePath scope), NICE-005 + FR-010 (blast_radius summarize +
# directDependants).

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
        jsonrpc = "2.0"
        id = $id
        method = "tools/call"
        params = @{ name = $name; arguments = $callArgs }
    }
    $resp = Read-JsonRpcResponse -expectedId $id -timeoutSec 300
    if ($resp.error) { throw "$name returned error: $($resp.error | ConvertTo-Json)" }
    return $resp.result.content[0].text
}

$failures = @()
function Assert($condition, $msg) {
    if (-not $condition) { $script:failures += $msg; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
    else { Write-Host "  [ok]   $msg" -ForegroundColor Green }
}

try {
    Write-Host "==== P1 dogfood ===="
    Send-JsonRpc @{ jsonrpc = "2.0"; id = 1; method = "initialize"; params = @{
        protocolVersion = "2024-11-05"; capabilities = @{}
        clientInfo = @{ name = "p1-dogfood"; version = "1.0" } } }
    $null = Read-JsonRpcResponse -expectedId 1 -timeoutSec 30
    Send-JsonRpc @{ jsonrpc = "2.0"; method = "notifications/initialized" }

    Write-Host ""
    Write-Host "[step 1/6] analyze lifeblood itself"
    $analyzeText = CallTool 2 "lifeblood_analyze" @{ projectPath = $Project }
    Write-Host ('  raw analyze first 300: ' + $analyzeText.Substring(0, [Math]::Min(300, $analyzeText.Length)))
    $analyze = $analyzeText | ConvertFrom-Json
    $sMsg = 'analyze returned more than 1000 symbols (got ' + $analyze.summary.symbols + ')'
    Assert ($analyze.summary.symbols -gt 1000) $sMsg
    $eMsg = 'analyze returned more than 5000 edges (got ' + $analyze.summary.edges + ')'
    Assert ($analyze.summary.edges -gt 5000) $eMsg

    # --- BUG-002 / INV-RESOLVER-006 ---
    # Lifeblood has property `IFileSystem.PhysicalFileSystem.FileExists`. Look it
    # up via a synthetic `method:` prefix to confirm kind correction.
    Write-Host ""
    Write-Host "[step 2/6] BUG-002 method: prefix on a property - kind correction"
    $lookupText = CallTool 3 "lifeblood_lookup" @{ symbolId = "method:Lifeblood.Adapters.CSharp.PhysicalFileSystem.FileExists" }
    $preview = $lookupText.Substring(0, [Math]::Min(220, $lookupText.Length))
    Write-Host ('  result: ' + $preview + '...')
    # FileExists is actually a method on PhysicalFileSystem so the resolver
    # returns the method directly. Pick a known-property: Lifeblood.Server.Mcp.GraphSession.FileSystem
    $kindText = CallTool 4 "lifeblood_lookup" @{ symbolId = "method:Lifeblood.Server.Mcp.GraphSession.FileSystem" }
    $kind = $kindText | ConvertFrom-Json
    Assert (($kindText -match "kindCorrected" -or $kindText -match "property:Lifeblood.Server.Mcp.GraphSession.FileSystem") -or ($kind.kind -eq "Property")) "method:GraphSession.FileSystem corrects to property"

    # --- BUG-015 ---
    Write-Host ""
    Write-Host "[step 3/6] BUG-015 compile_check filePath input"
    $ccText = CallTool 5 "lifeblood_compile_check" @{
        filePath = "src/Lifeblood.Domain/Graph/Symbol.cs"
        moduleName = "Lifeblood.Domain"
    }
    $cc = $ccText | ConvertFrom-Json
    Assert ($cc.source -eq "filePath") "compile_check echoes source=filePath"
    Assert ($cc.filePath -ne $null -and $cc.filePath.Length -gt 0) "compile_check echoes filePath"

    Write-Host ""
    Write-Host "[step 3b/6] BUG-015 mutually-exclusive guard"
    Send-JsonRpc @{
        jsonrpc = "2.0"; id = 6; method = "tools/call"
        params = @{ name = "lifeblood_compile_check"; arguments = @{
            code = "public class X {}"; filePath = "Symbol.cs"; moduleName = "Lifeblood.Domain" } }
    }
    $resp = Read-JsonRpcResponse -expectedId 6 -timeoutSec 60
    $err = ($resp.result.content[0].text + " " + ($resp.result.isError | Out-String))
    Assert ($err -match "mutually exclusive") "compile_check rejects code+filePath together"

    # --- BUG-016 ---
    Write-Host ""
    Write-Host "[step 4/6] BUG-016 diagnose with filePath scope"
    $diagText = CallTool 7 "lifeblood_diagnose" @{ filePath = "src/Lifeblood.Domain/Graph/Symbol.cs" }
    $diag = $diagText | ConvertFrom-Json
    Assert ($diag.scope -eq "file") "diagnose returns scope=file when filePath set"
    Assert ($diag.filePath -ne $null) "diagnose echoes filePath"

    $diagAll = (CallTool 8 "lifeblood_diagnose" @{}) | ConvertFrom-Json
    Assert ($diagAll.scope -eq "project") "diagnose with no filter returns scope=project"
    $msg = 'file-scoped count is at most project count (' + $diag.count + ' / ' + $diagAll.count + ')'
    Assert ($diag.count -le $diagAll.count) $msg

    # --- NICE-005 + FR-010 ---
    Write-Host ""
    Write-Host "[step 5/6] NICE-005 + FR-010 blast_radius summarize + directDependants"
    $brText = CallTool 9 "lifeblood_blast_radius" @{ symbolId = "type:Lifeblood.Domain.Graph.Symbol" }
    $br = $brText | ConvertFrom-Json
    Assert ($null -ne $br.directDependants) "blast_radius reports directDependants"
    Assert ($null -ne $br.affectedCount)   "blast_radius reports affectedCount"
    Assert ($null -ne $br.truncated)       "blast_radius reports truncated flag"
    Write-Host ("  direct={0}  transitive={1}" -f $br.directDependants, $br.affectedCount)

    $brSumText = CallTool 10 "lifeblood_blast_radius" @{ symbolId = "type:Lifeblood.Domain.Graph.Symbol"; summarize = $true }
    Assert ($brSumText -match '"summarize":\s*true') "summarize:true echoed in response"
    Assert ($brSumText -match '"preview":')          "preview field present in summarize mode"
    Assert ($brSumText -notmatch '"affected":')      "affected field omitted in summarize mode"

    # --- BUG-010 regression sanity ---
    Write-Host ""
    Write-Host "[step 6/6] BUG-010 find_references on interface method"
    $refText = CallTool 11 "lifeblood_find_references" @{ symbolId = "method:Lifeblood.Application.Ports.Infrastructure.IFileSystem.ReadAllText(string)" }
    $refs = $refText | ConvertFrom-Json
    $refMsg = 'find_references on IFileSystem.ReadAllText returns at least 1 location (got ' + $refs.count + ')'
    Assert ($refs.count -ge 1) $refMsg

    Write-Host ""
    if ($failures.Count -eq 0) {
        Write-Host "==== P1 DOGFOOD: ALL GREEN ====" -ForegroundColor Green
        exit 0
    } else {
        Write-Host "==== P1 DOGFOOD: FAILURES ====" -ForegroundColor Red
        foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
        exit 1
    }
}
finally {
    try { Send-JsonRpc @{ jsonrpc = "2.0"; id = 999; method = "shutdown" } } catch {}
    Start-Sleep -Milliseconds 200
    if (-not $proc.HasExited) { $proc.Kill() }
}
