[CmdletBinding()]
param(
    [Parameter()]
    [string]$PluginPath = "plugins/moodle-connector",

    [Parameter()]
    [string]$OutputPath = "artifacts/moodle-plugin.zip"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "validate-plugin.ps1") -PluginPath $PluginPath

$pluginRoot = (Resolve-Path -LiteralPath $PluginPath).Path
$outputFullPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
$outputDirectory = Split-Path -Parent $outputFullPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("moodle-plugin-" + [guid]::NewGuid().ToString("N"))
$packageRoot = Join-Path $tempRoot "moodle"

try {
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    Get-ChildItem -LiteralPath $pluginRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $packageRoot -Recurse -Force
    }

    Get-ChildItem -LiteralPath $packageRoot -Directory -Recurse -Force |
        Where-Object { $_.Name -eq "__pycache__" } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force

    Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force |
        Where-Object { $_.Extension -eq ".pyc" } |
        Remove-Item -Force

    if (Test-Path -LiteralPath $outputFullPath) {
        Remove-Item -LiteralPath $outputFullPath -Force
    }

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $outputFullPath -CompressionLevel Optimal
    Write-Host "Plugin package created: $outputFullPath"
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
