param(
    [Parameter(Mandatory=$true)][string]$Project
)

$ErrorActionPreference = 'Stop'

$cores = (Get-CimInstance -Class Win32_Processor).NumberOfLogicalProcessors
$cpuName = (Get-CimInstance -Class Win32_Processor).Name.Trim()
$totalRamGb = [math]::Round((Get-CimInstance -Class Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)

Write-Host ""
Write-Host "================================================================"
Write-Host "  Lifeblood analyze: $Project"
Write-Host "  Host: $cpuName ($cores logical cores) | $totalRamGb GB RAM"
Write-Host "================================================================"
Write-Host ""

# Publish CLI once to avoid measuring the build
Write-Host "[prep] Publishing CLI (Release) ..."
$publishArgs = @("publish","src/Lifeblood.CLI/Lifeblood.CLI.csproj","-c","Release","-o","cli-dist","--nologo","-v","q")
& dotnet @publishArgs | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[prep] publish failed"
    exit 1
}

Write-Host "[run]  Starting analyze ..."
Write-Host ""

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = "dotnet"
$startInfo.Arguments = "cli-dist\Lifeblood.CLI.dll analyze --project `"$Project`""
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $startInfo

# Async stdout/stderr forwarding
$stdoutBuilder = New-Object System.Text.StringBuilder
$stderrBuilder = New-Object System.Text.StringBuilder
$null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action {
    if ($EventArgs.Data) { [void]$Event.MessageData.Append($EventArgs.Data + "`n"); Write-Host "  | $($EventArgs.Data)" }
} -MessageData $stdoutBuilder
$null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action {
    if ($EventArgs.Data) { [void]$Event.MessageData.Append($EventArgs.Data + "`n"); Write-Host "  | $($EventArgs.Data)" }
} -MessageData $stderrBuilder

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$null = $proc.Start()
$proc.BeginOutputReadLine()
$proc.BeginErrorReadLine()

$peakWs = 0L
$peakPrivate = 0L
$samples = 0
while (-not $proc.HasExited) {
    try {
        $proc.Refresh()
        if ($proc.WorkingSet64 -gt $peakWs) { $peakWs = $proc.WorkingSet64 }
        if ($proc.PrivateMemorySize64 -gt $peakPrivate) { $peakPrivate = $proc.PrivateMemorySize64 }
        $samples++
    } catch {}
    Start-Sleep -Milliseconds 250
}
$sw.Stop()
$proc.WaitForExit()

$wallSec = $sw.Elapsed.TotalSeconds
$cpuSec = $proc.TotalProcessorTime.TotalSeconds
$userSec = $proc.UserProcessorTime.TotalSeconds
$kernelSec = $proc.PrivilegedProcessorTime.TotalSeconds
$cpuPctTotal = if ($wallSec -gt 0) { $cpuSec / $wallSec * 100 } else { 0 }
$cpuPctPerCore = $cpuPctTotal / $cores

Write-Host ""
Write-Host "================================================================"
Write-Host "  Usage report"
Write-Host "================================================================"
Write-Host ("  Wall time           : {0,8:N1} s" -f $wallSec)
Write-Host ("  CPU time (total)    : {0,8:N1} s" -f $cpuSec)
Write-Host ("    user mode         : {0,8:N1} s" -f $userSec)
Write-Host ("    kernel mode       : {0,8:N1} s" -f $kernelSec)
Write-Host ("  CPU utilization     : {0,7:N1}% total (sum across all cores)" -f $cpuPctTotal)
Write-Host ("  CPU avg per core    : {0,7:N1}% across $cores logical cores" -f $cpuPctPerCore)
Write-Host ("  Peak working set    : {0,8:N0} MB" -f ($peakWs / 1MB))
Write-Host ("  Peak private bytes  : {0,8:N0} MB" -f ($peakPrivate / 1MB))
Write-Host ("  RAM samples taken   : {0,8:N0} (every 250 ms)" -f $samples)
Write-Host ("  Process exit code   : {0,8}" -f $proc.ExitCode)
Write-Host "================================================================"
Write-Host ""

if ($proc.ExitCode -ne 0) { exit $proc.ExitCode }
