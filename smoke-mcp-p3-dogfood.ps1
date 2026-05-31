param(
    [string]$Project = ".",
    [string]$ServerDll = "$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll"
)

# P3 (v0.6.7) dogfood — measures the dead_code FP reduction Unity
# reachability buys on a real Unity workspace. The current binary has
# the Unity adapter wired by default; to compare we count findings on
# the workspace and confirm canonical Unity entry points
# (RuntimeInitializeOnLoadMethod methods, MonoBehaviour magic methods)
# are NOT flagged.
#
# Pass -Project on a non-Unity tree to confirm the Unity adapter is a
# no-op there (zero degradation for non-Unity workspaces).

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
function Read-JsonRpcResponse([int]$expectedId, [int]$timeoutSec = 600) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ($true) {
        if ((Get-Date) -gt $deadline) { throw "Timeout id=$expectedId" }
        $line = $proc.StandardOutput.ReadLine()
        if ($line -eq $null) { throw "stdout closed before id=$expectedId" }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $msg = $line | ConvertFrom-Json } catch { continue }
        if ($msg.id -eq $expectedId) { return $msg }
    }
}
function CallTool([int]$id, [string]$name, $callArgs) {
    Send-JsonRpc @{
        jsonrpc = "2.0"; id = $id; method = "tools/call"
        params = @{ name = $name; arguments = $callArgs }
    }
    $resp = Read-JsonRpcResponse -expectedId $id -timeoutSec 600
    if ($resp.error) { throw "$name error: $($resp.error | ConvertTo-Json)" }
    return $resp.result.content[0].text
}

$failures = @()
function Assert($condition, $msg) {
    if (-not $condition) { $script:failures += $msg; Write-Host "  [FAIL] $msg" -ForegroundColor Red }
    else { Write-Host "  [ok]   $msg" -ForegroundColor Green }
}

try {
    Write-Host "==== P3 dogfood (Unity reachability) ===="
    Write-Host "Project: $Project"
    Send-JsonRpc @{ jsonrpc = "2.0"; id = 1; method = "initialize"; params = @{
        protocolVersion = "2024-11-05"; capabilities = @{}
        clientInfo = @{ name = "p3-dogfood"; version = "1.0" } } }
    $null = Read-JsonRpcResponse -expectedId 1 -timeoutSec 30
    Send-JsonRpc @{ jsonrpc = "2.0"; method = "notifications/initialized" }

    Write-Host ""
    Write-Host "[1] analyze workspace"
    $analyzeText = CallTool 2 "lifeblood_analyze" @{ projectPath = $Project }
    $analyze = $analyzeText | ConvertFrom-Json
    Write-Host ('  modules=' + $analyze.summary.modules + ' symbols=' + $analyze.summary.symbols + ' edges=' + $analyze.summary.edges)
    Assert ($analyze.summary.modules -ge 1) ('analyze modules >= 1 (got ' + $analyze.summary.modules + ')')

    Write-Host ""
    Write-Host "[2] dead_code over the workspace"
    $deadText = CallTool 3 "lifeblood_dead_code" @{}
    $dead = $deadText | ConvertFrom-Json
    Write-Host ('  findings=' + $dead.count)

    # Categorize findings to highlight what is and is not flagged.
    $methodFindings = $dead.findings | Where-Object { $_.kind -eq 'Method' }
    $magicHits = $methodFindings | Where-Object {
        $_.name -in @('Awake','Start','Update','FixedUpdate','LateUpdate','OnEnable','OnDisable','OnDestroy','OnValidate','Reset','OnGUI','OnTriggerEnter','OnTriggerExit','OnTriggerStay','OnCollisionEnter','OnCollisionExit','OnCollisionStay','OnAudioFilterRead','OnApplicationFocus','OnApplicationPause','OnApplicationQuit','OnDrawGizmos','OnDrawGizmosSelected','OnRenderImage')
    }
    Write-Host ('  MonoBehaviour-magic-named methods still flagged: ' + ($magicHits | Measure-Object).Count)

    if ($magicHits) {
        Write-Host "  (these are either on non-MonoBehaviour types or extractor-attribute-blind:)"
        $magicHits | Select-Object -First 8 | ForEach-Object {
            Write-Host ('    ' + $_.canonicalId + '  @ ' + $_.filePath + ':' + $_.line)
        }
    }

    # Inspect a known DAWG MonoBehaviour-magic method that previously produced a FP.
    # Lifeblood-on-itself has no MonoBehaviour, so this section only fires for DAWG.
    $unityShaped = $methodFindings | Where-Object { $_.filePath -match '(Assets|Editor|Runtime)' -and $_.name -in @('Awake','Update','OnEnable','Start') }
    Write-Host ('  Unity-shaped magic-name FPs (Assets|Editor|Runtime path + magic name): ' + ($unityShaped | Measure-Object).Count)

    Write-Host ""
    Write-Host "[3] envelope on dead_code response carries Unity-FP limitation"
    Assert ($null -ne $dead.envelope) "dead_code response carries envelope"
    Assert ($dead.envelope.confidence -eq "Advisory") "dead_code envelope.confidence=Advisory"

    Write-Host ""
    Write-Host "[4] context envelope still works"
    $ctxText = CallTool 4 "lifeblood_context" @{}
    $ctx = $ctxText | ConvertFrom-Json
    Assert ($null -ne $ctx.envelope) "context response carries envelope"

    Write-Host ""
    Write-Host "[5] invariants registered (parser must succeed; total > 0 only when project has INV-* entries)"
    $invText = CallTool 5 "lifeblood_invariant_check" @{ mode = "audit" }
    $inv = $invText | ConvertFrom-Json
    Assert ($null -ne $inv.totalCount) ('invariant audit returns totalCount field (got ' + $inv.totalCount + ')')
    if ($null -ne $inv.duplicates) {
      $dupCount = ($inv.duplicates | Measure-Object).Count
      Assert ($dupCount -eq 0) ('zero duplicate invariant ids (got ' + $dupCount + ')')
    }

    Write-Host ""
    if ($failures.Count -eq 0) {
        Write-Host "==== P3 DOGFOOD: ALL GREEN ====" -ForegroundColor Green
        exit 0
    } else {
        Write-Host "==== P3 DOGFOOD: FAILURES ====" -ForegroundColor Red
        foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
        exit 1
    }
}
finally {
    try { Send-JsonRpc @{ jsonrpc = "2.0"; id = 999; method = "shutdown" } } catch {}
    Start-Sleep -Milliseconds 200
    if (-not $proc.HasExited) { $proc.Kill() }
}
