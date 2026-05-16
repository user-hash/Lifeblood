Set-StrictMode -Version Latest

function Invoke-NativeScoutExtraction {
    param(
        [Parameter(Mandatory)][string]$NativeAdapterPath,
        [Parameter(Mandatory)][string]$FfmpegRoot,
        [Parameter(Mandatory)][string]$BuildDir,
        [Parameter(Mandatory)][string]$Profile,
        [Parameter(Mandatory)][string]$OutputGraph
    )

    if (-not (Test-Path -LiteralPath $NativeAdapterPath)) {
        throw "Native adapter executable not found: $NativeAdapterPath"
    }

    & $NativeAdapterPath `
        --project $FfmpegRoot `
        --compilation-database $BuildDir `
        --profile $Profile `
        --allow-partial `
        --out $OutputGraph
    if ($LASTEXITCODE -ne 0) {
        throw "Native FFmpeg scout extraction failed with exit code $LASTEXITCODE."
    }
}

function Write-ScoutGraphSummary {
    param([Parameter(Mandatory)][string]$GraphPath)

    $graph = Get-Content -LiteralPath $GraphPath -Raw | ConvertFrom-Json
    $nativeKinds = $graph.symbols |
        Group-Object { $_.properties.'native.kind' } |
        Sort-Object Count -Descending |
        Select-Object -First 10 |
        ForEach-Object { "$($_.Name)=$($_.Count)" }

    [pscustomobject]@{
        GraphPath = $GraphPath
        Symbols = $graph.symbols.Count
        Edges = $graph.edges.Count
        Files = @($graph.symbols | Where-Object { $_.kind -eq 'file' }).Count
        Methods = @($graph.symbols | Where-Object { $_.kind -eq 'method' }).Count
        NativeKinds = ($nativeKinds -join '; ')
    } | Format-List
}
