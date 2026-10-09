param([switch]$CompareWithSource)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'extraction-manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $copy = Join-Path $root $entry.copy
    if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Copied module changed: $($entry.copy)" }
    if ($CompareWithSource) {
        $original = Join-Path $manifest.source $entry.source
        if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Source changed since extraction: $($entry.source)" }
    }
}
Write-Host "PASS: $($manifest.files.Count) module files match the extraction snapshot."
