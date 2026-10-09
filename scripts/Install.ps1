param([ValidateSet('2024','2025')][string]$RevitVersion = '2024')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$package = Join-Path $root "artifacts\Revit$RevitVersion"
$manifest = Join-Path $package 'RevitRouteLab.addin'
if (-not (Test-Path -LiteralPath $manifest)) { throw "Run scripts\Build.ps1 -RevitVersion $RevitVersion first." }
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before installing.' }
$addinRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addinRoot "RevitRouteLab\$RevitVersion"
$targetManifest = Join-Path $addinRoot 'RevitRouteLab.addin'
if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $targetManifest)) {
    throw 'A Route Lab installation already exists. Back it up and remove its files manually before replacing it.'
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $package "RevitRouteLab\$RevitVersion") -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $target
}
Copy-Item -LiteralPath $manifest -Destination $targetManifest
Write-Host "Installed Route Lab for Revit $RevitVersion. Start Revit and open the Route Lab ribbon."
