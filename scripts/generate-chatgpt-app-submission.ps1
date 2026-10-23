[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$arguments = @('--generate-submission')
if ($Check) {
    $arguments += '--check'
}

Push-Location $repositoryRoot
try {
    dotnet run --project src/Moodle.Benchmarks/Moodle.Benchmarks.csproj --no-restore -- @arguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
