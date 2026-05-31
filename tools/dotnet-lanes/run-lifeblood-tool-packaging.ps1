param(
    [string]$TargetFramework = "net8.0",
    [string]$Configuration = "Release",
    [string]$OutputPath = "artifacts/tool-packaging/tool-packaging-report.json",
    [int]$McpSmokeTimeoutSeconds = 10,
    [switch]$RunPublishExperiments,
    [string]$PublishRuntimeIdentifier = "",
    [switch]$SkipMcpSmoke
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    $dir = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    return $dir.Path
}

function Assert-UnderRoot([string]$Root, [string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside repository: $full"
    }

    return $full
}

function Write-Report($Report, [string]$Path) {
    $dir = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $Report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-Step($Report, [string]$Name, [string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Host "[$Name] $FileName $($Arguments -join ' ')"

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $FileName @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $stopwatch.Stop()
    }

    $Report.steps += [pscustomobject]@{
        name = $Name
        command = @($FileName) + $Arguments
        workingDirectory = $WorkingDirectory
        exitCode = $exitCode
        durationMs = [long]$stopwatch.ElapsedMilliseconds
        output = @($output)
    }

    if ($exitCode -ne 0) {
        $Report.status = "failed"
        throw "Step '$Name' failed with exit code $exitCode."
    }

    return @($output)
}

function Invoke-ReportOnlyStep($Report, [string]$Name, [string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory) {
    Write-Host "[$Name] $FileName $($Arguments -join ' ')"

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & $FileName @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $stopwatch.Stop()
    }

    $Report.steps += [pscustomobject]@{
        name = $Name
        command = @($FileName) + $Arguments
        workingDirectory = $WorkingDirectory
        optional = $true
        reportOnly = $true
        exitCode = $exitCode
        durationMs = [long]$stopwatch.ElapsedMilliseconds
        output = @($output)
        skipped = $exitCode -ne 0
        reason = if ($exitCode -ne 0) { "Report-only publish experiment did not complete on this host/configuration; see exitCode/output." } else { $null }
    }

    return $exitCode -eq 0
}

function Get-PackageVersion([string]$PackagesDir, [string]$PackageId) {
    $prefix = "$PackageId."
    $package = Get-ChildItem -LiteralPath $PackagesDir -Filter "$PackageId.*.nupkg" |
        Where-Object { $_.Name.Substring($prefix.Length) -match '^\d' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $package) {
        throw "No package found for $PackageId in $PackagesDir"
    }

    $fileName = $package.Name
    $version = $fileName.Substring($prefix.Length, $fileName.Length - $prefix.Length - ".nupkg".Length)
    return [pscustomobject]@{
        path = $package.FullName
        version = $version
    }
}

function Get-ToolExecutable([string]$ToolPath, [string]$CommandName) {
    $extension = if ($IsWindows -or $env:OS -eq "Windows_NT") { ".exe" } else { "" }
    return Join-Path $ToolPath "$CommandName$extension"
}

function Join-ProcessArguments([string[]]$Arguments) {
    return (($Arguments | ForEach-Object {
        $arg = [string]$_
        if ($arg.Length -eq 0) { return '""' }
        if ($arg -notmatch '[\s"]') { return $arg }
        '"' + ($arg -replace '"', '\"') + '"'
    }) -join ' ')
}

function Invoke-ProcessSmoke(
    $Report,
    [string]$Name,
    [string]$FileName,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [switch]$CloseStandardInput,
    [switch]$Optional,
    [int]$TimeoutSeconds = 10) {

    Write-Host "[$Name] $FileName $($Arguments -join ' ')"

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    if ($startInfo.PSObject.Properties.Name -contains "ArgumentList" -and $null -ne $startInfo.ArgumentList) {
        foreach ($arg in $Arguments) {
            [void]$startInfo.ArgumentList.Add($arg)
        }
    } else {
        $startInfo.Arguments = Join-ProcessArguments $Arguments
    }
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = $CloseStandardInput

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start()
    if ($CloseStandardInput) {
        $process.StandardInput.Close()
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $completed = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $completed) {
        try { $process.Kill() } catch { }
        $Report.status = "failed"
        throw "Step '$Name' timed out after $TimeoutSeconds seconds."
    }

    $stopwatch.Stop()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    $Report.steps += [pscustomobject]@{
        name = $Name
        command = @($FileName) + $Arguments
        workingDirectory = $WorkingDirectory
        optional = [bool]$Optional
        exitCode = $process.ExitCode
        durationMs = [long]$stopwatch.ElapsedMilliseconds
        stdout = $stdout
        stderr = $stderr
        skipped = $false
        reason = $null
    }

    if ($process.ExitCode -ne 0) {
        if ($Optional) {
            $Report.steps[-1].skipped = $true
            $Report.steps[-1].reason = "Optional smoke is unsupported or failed on this SDK/tooling; see exitCode/stdout/stderr."
            return $false
        }

        $Report.status = "failed"
        throw "Step '$Name' failed with exit code $($process.ExitCode)."
    }

    return $true
}

function Add-SkippedStep($Report, [string]$Name, [string]$Reason) {
    $Report.steps += [pscustomobject]@{
        name = $Name
        skipped = $true
        reason = $Reason
    }
}

function Add-ValidationStep($Report, [string]$Name, [bool]$Passed, [string]$Reason) {
    $Report.steps += [pscustomobject]@{
        name = $Name
        validation = $true
        skipped = $false
        passed = $Passed
        reason = if ($Passed) { $null } else { $Reason }
    }

    if (-not $Passed) {
        $Report.status = "failed"
        throw "Validation '$Name' failed: $Reason"
    }
}

function Assert-LastStepStdoutContains($Report, [string]$Name, [string[]]$ExpectedTokens) {
    if ($Report.steps.Count -eq 0) {
        throw "No prior step exists for validation '$Name'."
    }

    $lastStep = $Report.steps[-1]
    $stdout = [string]$lastStep.stdout
    $missing = @($ExpectedTokens | Where-Object { $stdout -notlike "*$_*" })
    Add-ValidationStep $Report $Name ($missing.Count -eq 0) "Missing expected help tokens: $($missing -join ', ')"
}

function Invoke-PublishExperiment(
    $Report,
    [string]$Name,
    [string]$ProjectPath,
    [string]$OutputDirectory,
    [string[]]$ExtraArguments,
    [string]$WorkingDirectory) {

    if (-not $RunPublishExperiments) {
        Add-SkippedStep $Report $Name "Report-only publish experiments are opt-in; pass -RunPublishExperiments to execute."
        return
    }

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    Invoke-ReportOnlyStep $Report $Name "dotnet" (@(
        "publish", $ProjectPath,
        "-c", $Configuration,
        "-f", $TargetFramework,
        "-o", $OutputDirectory,
        "--nologo"
    ) + $ExtraArguments) $WorkingDirectory | Out-Null
}

$repoRoot = Get-RepoRoot
$outputFullPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }
$requestedLaneRoot = Assert-UnderRoot $repoRoot (Join-Path $repoRoot "artifacts/tool-packaging/$TargetFramework")
$laneRoot = $requestedLaneRoot
$laneRootFallbackReason = $null

if (Test-Path -LiteralPath $laneRoot) {
    try {
        Remove-Item -LiteralPath $laneRoot -Recurse -Force
    }
    catch {
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
        $laneRoot = Assert-UnderRoot $repoRoot (Join-Path $repoRoot "artifacts/tool-packaging/$TargetFramework-$stamp")
        $laneRootFallbackReason = "Could not clean the previous artifact root '$requestedLaneRoot' (likely locked local package/tool files): $($_.Exception.Message). Using '$laneRoot' for this run."
        Write-Warning $laneRootFallbackReason
    }
}

$packagesDir = Join-Path $laneRoot "packages"
$toolsDir = Join-Path $laneRoot "tools"
$publishExperimentsDir = Join-Path $laneRoot "publish-experiments"

New-Item -ItemType Directory -Force -Path $packagesDir | Out-Null
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

$report = [ordered]@{
    schemaVersion = 1
    lane = "dotnet-tool-packaging"
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    repoRoot = $repoRoot
    targetFramework = $TargetFramework
    configuration = $Configuration
    status = "running"
    requestedArtifactRoot = $requestedLaneRoot
    artifactRoot = $laneRoot
    artifactRootFallbackReason = $laneRootFallbackReason
    artifacts = [ordered]@{
        packages = $packagesDir
        tools = $toolsDir
        publishExperiments = $publishExperimentsDir
    }
    publishExperiments = [ordered]@{
        enabled = [bool]$RunPublishExperiments
        runtimeIdentifier = if ([string]::IsNullOrWhiteSpace($PublishRuntimeIdentifier)) { $null } else { $PublishRuntimeIdentifier }
        posture = "report-only"
    }
    packageIds = @("Lifeblood", "Lifeblood.Server.Mcp")
    notes = @(
        "Local packaging smoke only; this script never publishes packages.",
        "Production project files remain pinned to their checked-in target frameworks.",
        "MCP smoke closes stdin immediately and verifies graceful startup/shutdown.",
        "dotnet tool exec / dnx smoke checks run only when the installed SDK exposes those .NET 10 commands.",
        "Publish experiments are report-only. Framework-dependent publish is safe to compare; self-contained/trimming/AOT require an explicit runtime identifier and may be unsupported for Roslyn-heavy tools."
    )
    steps = @()
}

try {
    Invoke-Step $report "pack-cli" "dotnet" @(
        "pack", (Join-Path $repoRoot "src/Lifeblood.CLI/Lifeblood.CLI.csproj"),
        "-c", $Configuration,
        "-p:TargetFramework=$TargetFramework",
        "-o", $packagesDir,
        "--nologo"
    ) $repoRoot | Out-Null

    Invoke-Step $report "pack-mcp" "dotnet" @(
        "pack", (Join-Path $repoRoot "src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj"),
        "-c", $Configuration,
        "-p:TargetFramework=$TargetFramework",
        "-o", $packagesDir,
        "--nologo"
    ) $repoRoot | Out-Null

    $cliPackage = Get-PackageVersion $packagesDir "Lifeblood"
    $mcpPackage = Get-PackageVersion $packagesDir "Lifeblood.Server.Mcp"
    $report.packages = @(
        [pscustomobject]@{ packageId = "Lifeblood"; version = $cliPackage.version; path = $cliPackage.path },
        [pscustomobject]@{ packageId = "Lifeblood.Server.Mcp"; version = $mcpPackage.version; path = $mcpPackage.path }
    )

    Invoke-Step $report "install-cli-tool" "dotnet" @(
        "tool", "install",
        "--tool-path", $toolsDir,
        "--add-source", $packagesDir,
        "--version", $cliPackage.version,
        "Lifeblood"
    ) $repoRoot | Out-Null

    Invoke-Step $report "install-mcp-tool" "dotnet" @(
        "tool", "install",
        "--tool-path", $toolsDir,
        "--add-source", $packagesDir,
        "--version", $mcpPackage.version,
        "Lifeblood.Server.Mcp"
    ) $repoRoot | Out-Null

    Invoke-ProcessSmoke $report "smoke-cli-help" (Get-ToolExecutable $toolsDir "lifeblood") @("--help") $repoRoot | Out-Null
    Assert-LastStepStdoutContains $report "smoke-cli-help-contract" @(
        "lifeblood analyze --project",
        "lifeblood context --project",
        "lifeblood export  --project",
        "lifeblood verify  --incremental",
        "Built-in rule packs:"
    )

    $cliProject = Join-Path $repoRoot "src/Lifeblood.CLI/Lifeblood.CLI.csproj"
    $mcpProject = Join-Path $repoRoot "src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj"
    Invoke-PublishExperiment $report "experiment-publish-cli-framework-dependent" $cliProject (Join-Path $publishExperimentsDir "cli-framework-dependent") @() $repoRoot
    Invoke-PublishExperiment $report "experiment-publish-mcp-framework-dependent" $mcpProject (Join-Path $publishExperimentsDir "mcp-framework-dependent") @() $repoRoot

    if ([string]::IsNullOrWhiteSpace($PublishRuntimeIdentifier)) {
        Add-SkippedStep $report "experiment-publish-cli-self-contained" "Self-contained publish experiments require -PublishRuntimeIdentifier."
        Add-SkippedStep $report "experiment-publish-cli-trimmed" "Trim publish experiments require -PublishRuntimeIdentifier."
        Add-SkippedStep $report "experiment-publish-cli-aot" "AOT publish experiments require -PublishRuntimeIdentifier."
        Add-SkippedStep $report "experiment-publish-mcp-self-contained" "Self-contained publish experiments require -PublishRuntimeIdentifier."
        Add-SkippedStep $report "experiment-publish-mcp-trimmed" "MCP trimming is intentionally skipped until Roslyn compatibility evidence says otherwise."
        Add-SkippedStep $report "experiment-publish-mcp-aot" "MCP AOT is intentionally skipped until Roslyn compatibility evidence says otherwise."
    } else {
        $ridArgs = @("-r", $PublishRuntimeIdentifier)
        Invoke-PublishExperiment $report "experiment-publish-cli-self-contained" $cliProject (Join-Path $publishExperimentsDir "cli-self-contained") ($ridArgs + @("--self-contained", "true")) $repoRoot
        Invoke-PublishExperiment $report "experiment-publish-cli-trimmed" $cliProject (Join-Path $publishExperimentsDir "cli-trimmed") ($ridArgs + @("--self-contained", "true", "-p:PublishTrimmed=true")) $repoRoot
        Invoke-PublishExperiment $report "experiment-publish-cli-aot" $cliProject (Join-Path $publishExperimentsDir "cli-aot") ($ridArgs + @("--self-contained", "true", "-p:PublishAot=true")) $repoRoot
        Invoke-PublishExperiment $report "experiment-publish-mcp-self-contained" $mcpProject (Join-Path $publishExperimentsDir "mcp-self-contained") ($ridArgs + @("--self-contained", "true")) $repoRoot
        Add-SkippedStep $report "experiment-publish-mcp-trimmed" "MCP trimming is intentionally skipped until Roslyn compatibility evidence says otherwise."
        Add-SkippedStep $report "experiment-publish-mcp-aot" "MCP AOT is intentionally skipped until Roslyn compatibility evidence says otherwise."
    }

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $toolExecHelp = & dotnet tool exec --help 2>&1
        $toolExecExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
    }

    $toolExecHelpText = (@($toolExecHelp) -join "`n")
    if ($toolExecExitCode -eq 0 -and $toolExecHelpText -match '(?m)^\s+exec\s') {
        Invoke-ProcessSmoke $report "smoke-dotnet-tool-exec-help" "dotnet" @(
            "tool", "exec", "Lifeblood@$($cliPackage.version)",
            "--add-source", $packagesDir,
            "--",
            "--help"
        ) $repoRoot -Optional | Out-Null
    } else {
        Add-SkippedStep $report "smoke-dotnet-tool-exec-help" "Installed SDK does not expose `dotnet tool exec` (requires .NET 10.0.100 SDK or later)."
    }

    $dnxCommand = Get-Command "dnx" -ErrorAction SilentlyContinue
    if ($null -ne $dnxCommand) {
        Invoke-ProcessSmoke $report "smoke-dnx-help" $dnxCommand.Source @(
            "Lifeblood@$($cliPackage.version)",
            "--add-source", $packagesDir,
            "--",
            "--help"
        ) $repoRoot -Optional | Out-Null
    } else {
        Add-SkippedStep $report "smoke-dnx-help" "dnx command not found on PATH (installed with .NET 10 SDKs that provide it)."
    }

    if (-not $SkipMcpSmoke) {
        Invoke-ProcessSmoke $report "smoke-mcp-closed-stdin" (Get-ToolExecutable $toolsDir "lifeblood-mcp") @() $repoRoot -CloseStandardInput -TimeoutSeconds $McpSmokeTimeoutSeconds | Out-Null
    }

    $report.status = "passed"
}
finally {
    Write-Report $report $outputFullPath
}

Write-Host "Wrote tool packaging report to $outputFullPath"
