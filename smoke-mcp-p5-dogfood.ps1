param([string]$Project = "$PSScriptRoot", [string]$ServerDll = "$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll")
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ServerDll)) { Write-Host "[fail] dll missing"; exit 1 }
$psi = New-Object System.Diagnostics.ProcessStartInfo; $psi.FileName="dotnet"; $psi.Arguments="`"$ServerDll`""; $psi.UseShellExecute=$false; $psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
$proc = New-Object System.Diagnostics.Process; $proc.StartInfo=$psi
$null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action { if ($EventArgs.Data) { Write-Host "  [stderr] $($EventArgs.Data)" -ForegroundColor DarkGray } }
$null = $proc.Start(); $proc.BeginErrorReadLine()
function Send($obj) { $proc.StandardInput.WriteLine(($obj | ConvertTo-Json -Depth 20 -Compress)); $proc.StandardInput.Flush() }
function Recv([int]$id, [int]$t = 600) { $d=(Get-Date).AddSeconds($t); while($true){ if((Get-Date) -gt $d){throw "to"}; $l=$proc.StandardOutput.ReadLine(); if($l -eq $null){throw "closed"}; if([string]::IsNullOrWhiteSpace($l)){continue}; try{$m=$l|ConvertFrom-Json}catch{continue}; if($m.id -eq $id){return $m} } }
function Tool([int]$id, [string]$n, $a) { Send @{jsonrpc="2.0";id=$id;method="tools/call";params=@{name=$n;arguments=$a}}; (Recv $id).result.content[0].text }
$failures = @()
function Assert($c, $m) { if (-not $c) { $script:failures += $m; Write-Host "  [FAIL] $m" -ForegroundColor Red } else { Write-Host "  [ok]   $m" -ForegroundColor Green } }

try {
    Send @{jsonrpc="2.0";id=1;method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="p5";version="1"}}}
    $null = Recv 1 30; Send @{jsonrpc="2.0";method="notifications/initialized"}
    Write-Host "==== P5 dogfood (authority + port_health + cycles + forwarders) ===="
    Write-Host "Project: $Project"

    Write-Host ""
    Write-Host "[1] analyze"
    $a = (Tool 2 "lifeblood_analyze" @{projectPath=$Project}) | ConvertFrom-Json
    Write-Host ("modules=" + $a.summary.modules + " symbols=" + $a.summary.symbols)

    Write-Host ""
    Write-Host "[2] cycles"
    $c = (Tool 3 "lifeblood_cycles" @{}) | ConvertFrom-Json
    Assert ($null -ne $c.count) ('cycles tool returns count (got ' + $c.count + ')')
    Assert ($null -ne $c.envelope) "cycles response carries envelope"
    Write-Host ('  cycle SCCs in workspace: ' + $c.count)

    Write-Host ""
    Write-Host "[3] authority_report on a Lifeblood-shaped type"
    # Pick something that's likely to exist in either project: try lifeblood-specific first.
    $candidate = if ($Project -like '*Lifeblood*') { 'type:Lifeblood.Connectors.Mcp.LifebloodAuthorityReporter' } else { $null }
    if ($candidate) {
        $r = (Tool 4 "lifeblood_authority_report" @{symbolId=$candidate}) | ConvertFrom-Json
        Assert ($null -ne $r.envelope) "authority response carries envelope"
        Assert ($r.implementedInterfaceCount -ge 0) ('implementedInterfaceCount present (=' + $r.implementedInterfaceCount + ')')
        Assert ($null -ne $r.forwarderRatio) ('forwarderRatio present (=' + $r.forwarderRatio + ')')
        Write-Host ('  authority on ' + $candidate + ': ifaces=' + $r.implementedInterfaceCount + ' surface=' + $r.ownedPublicSurface + ' forwarderRatio=' + $r.forwarderRatio)
    } else {
        Write-Host '  (skipped - non-Lifeblood project)'
    }

    Write-Host ""
    Write-Host "[4] port_health on the symbol"
    if ($candidate) {
        $r = (Tool 5 "lifeblood_port_health" @{symbolId=$candidate}) | ConvertFrom-Json
        Assert ($null -ne $r.envelope) "port_health response carries envelope"
        Assert ($null -ne $r.verdict) ('verdict present (=' + $r.verdict + ')')
        Write-Host ('  port_health: members=' + $r.memberCount + ' live=' + $r.liveMembers + ' dead=' + $r.deadMembers + ' verdict=' + $r.verdict)
    } else {
        Write-Host '  (skipped - non-Lifeblood project)'
    }

    Write-Host ""
    Write-Host "[5] forwarder classification recorded on methods (count probe)"
    $exec = (Tool 6 "lifeblood_execute" @{code='Graph.Symbols.Count(s => s.Properties != null && s.Properties.ContainsKey("classification"))'}) | ConvertFrom-Json
    Assert ($exec.success) ('classification count probe execute succeeded')
    $classCount = [int]$exec.returnValue
    Assert ($classCount -gt 0) ('classification recorded on > 0 methods (got ' + $classCount + ')')
    Write-Host ('  methods with classification: ' + $classCount)

    Write-Host ""
    Write-Host "[6] PureForwarder count via execute"
    $exec = (Tool 7 "lifeblood_execute" @{code='Graph.Symbols.Count(s => s.Properties != null && s.Properties.TryGetValue("classification", out var c) && c == "PureForwarder")'}) | ConvertFrom-Json
    Assert ($exec.success) ('PureForwarder count probe succeeded')
    Write-Host ('  PureForwarder methods: ' + $exec.returnValue)

    Write-Host ""
    if ($failures.Count -eq 0) { Write-Host "==== P5 DOGFOOD: ALL GREEN ====" -ForegroundColor Green; exit 0 }
    else { Write-Host "==== P5 DOGFOOD: FAILURES ====" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }; exit 1 }
}
finally { try{Send @{jsonrpc="2.0";id=999;method="shutdown"}}catch{}; Start-Sleep -Milliseconds 200; if (-not $proc.HasExited) { $proc.Kill() } }
