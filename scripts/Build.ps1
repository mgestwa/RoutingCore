param(
    [ValidateSet('2024', '2025')][string]$RevitVersion = '2024',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$RevitApiPath = "C:\Program Files\Autodesk\Revit $RevitVersion",
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\RevitRouteLab\RevitRouteLab.csproj'
$buildArgs = @('build', $project, '-c', $Configuration, "-p:RevitVersion=$RevitVersion", "-p:RevitApiPath=$RevitApiPath", '--nologo')
if ($NoRestore) { $buildArgs += '--no-restore' }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
$framework = if ($RevitVersion -eq '2024') { 'net48' } else { 'net8.0-windows' }
$output = Join-Path $root "src\RevitRouteLab\bin\$Configuration\$RevitVersion\$framework"
$package = Join-Path $root "artifacts\Revit$RevitVersion"
New-Item -ItemType Directory -Path $package -Force | Out-Null
$assemblyDir = Join-Path $package "RevitRouteLab\$RevitVersion"
New-Item -ItemType Directory -Path $assemblyDir -Force | Out-Null
# Explicit payload: never package Revit's own assemblies.
foreach ($name in @('RevitRouteLab.dll', 'RevitRouteLab.pdb', 'Newtonsoft.Json.dll', 'AutoTrayRouting.default.json')) {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination $assemblyDir -Force
}
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Revit Route Lab</Name>
    <Assembly>RevitRouteLab\$RevitVersion\RevitRouteLab.dll</Assembly>
    <AddInId>160999A9-DF52-4A01-A492-F01009A47928</AddInId>
    <FullClassName>RevitRouteLab.Application</FullClassName>
    <VendorId>RTLB</VendorId>
    <VendorDescription>Independent Revit routing laboratory</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[IO.File]::WriteAllText((Join-Path $package 'RevitRouteLab.addin'), $manifest, [Text.UTF8Encoding]::new($false))
Write-Host "Package ready: $package"
Write-Host 'Build does not install or register the add-in.'
