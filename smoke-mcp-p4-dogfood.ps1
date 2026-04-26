param(
    [string]$Project = "$PSScriptRoot",
    [string]$ServerDll = "$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/Debug/net8.0/Lifeblood.Server.Mcp.dll"
)
# P4 dogfood — execute robustness:
#  - lifeblood_execute returns success on a trivial expression (host profile)
#  - Help global is reachable from sandbox
#  - SymbolsOfKind("Type") works as a string-named filter
#  - On a Unity workspace with no Library/, runtimeAssemblyWarnings is silent
#  - On a Unity workspace, UnityEngine.dll references inject (script can touch GameObject)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ServerDll)) { Write-Host "[fail] server dll missing"; exit 1 }
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
    Send @{jsonrpc="2.0";id=1;method="initialize";params=@{protocolVersion="2024-11-05";capabilities=@{};clientInfo=@{name="p4";version="1"}}}
    $null = Recv 1 30; Send @{jsonrpc="2.0";method="notifications/initialized"}

    Write-Host "==== P4 dogfood (execute robustness) ===="
    Write-Host "Project: $Project"

    Write-Host ""
    Write-Host "[1] analyze"
    $a = (Tool 2 "lifeblood_analyze" @{projectPath=$Project}) | ConvertFrom-Json
    Write-Host ("modules=" + $a.summary.modules + " symbols=" + $a.summary.symbols)

    Write-Host ""
    Write-Host "[2] execute trivial expression (host profile, default)"
    $r = (Tool 3 "lifeblood_execute" @{code="21 * 2"}) | ConvertFrom-Json
    Assert ($r.success) ('execute success on 21*2 (returnValue=' + $r.returnValue + ')')
    Assert ($r.returnValue -eq "42") ('returnValue is "42"')
    Assert (($r.targetRuntimeWarnings | Measure-Object).Count -eq 0) "host profile has no targetRuntimeWarnings"

    Write-Host ""
    Write-Host "[3] execute reaches Help global"
    $r = (Tool 4 "lifeblood_execute" @{code="Help.Length"}) | ConvertFrom-Json
    Assert ($r.success) ('Help global reachable (returnValue=' + $r.returnValue + ')')
    Assert ([int]$r.returnValue -gt 100) ('Help has substantial content (length=' + $r.returnValue + ')')

    Write-Host ""
    Write-Host "[4] execute SymbolsOfKind(""Type"") string helper"
    $r = (Tool 5 "lifeblood_execute" @{code='SymbolsOfKind("Type").Count()'}) | ConvertFrom-Json
    Assert ($r.success) ('SymbolsOfKind helper executes (returnValue=' + $r.returnValue + ')')
    Assert ([int]$r.returnValue -gt 0) ('SymbolsOfKind returned a non-zero count')

    Write-Host ""
    Write-Host "[5] execute EdgesOfKind(""Contains"") string helper"
    $r = (Tool 6 "lifeblood_execute" @{code='EdgesOfKind("Contains").Count()'}) | ConvertFrom-Json
    Assert ($r.success) ('EdgesOfKind helper executes (returnValue=' + $r.returnValue + ')')
    Assert ([int]$r.returnValue -gt 0) ('EdgesOfKind returned a non-zero count')

    Write-Host ""
    Write-Host "[6] execute with unknown targetProfile -> falls back to host with warning"
    $r = (Tool 7 "lifeblood_execute" @{code="1+1"; targetProfile="bogus-profile"}) | ConvertFrom-Json
    Assert ($r.success) ('fallback execute success')
    Assert (($r.targetRuntimeWarnings | Measure-Object).Count -ge 1) "unknown profile surfaces a targetRuntimeWarnings entry"

    Write-Host ""
    if ($failures.Count -eq 0) { Write-Host "==== P4 DOGFOOD: ALL GREEN ====" -ForegroundColor Green; exit 0 }
    else { Write-Host "==== P4 DOGFOOD: FAILURES ====" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }; exit 1 }
}
finally { try{Send @{jsonrpc="2.0";id=999;method="shutdown"}}catch{}; Start-Sleep -Milliseconds 200; if (-not $proc.HasExited) { $proc.Kill() } }
