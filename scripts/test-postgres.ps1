[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [int]$Port = 5433,
    [switch]$KeepDatabase
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'deploy/docker-compose.test.yml'
$composeProject = 'moodle-test'
$database = 'MOODLE_ci_test'
$username = 'MOODLE'
$password = 'ci-postgres-password'
$connectionString = "Host=localhost;Port=$Port;Database=$database;Username=$username;Password=$password"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker nao foi encontrado no PATH.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK nao foi encontrado no PATH.'
}

$env:MOODLE_POSTGRES_TEST_PORT = $Port.ToString()
$env:MOODLE_POSTGRES_TEST_DATABASE = $database
$env:MOODLE_POSTGRES_TEST_USERNAME = $username
$env:MOODLE_POSTGRES_TEST_PASSWORD = $password
$env:MOODLE_POSTGRES_TEST_CONNECTION = $connectionString

$composeArgs = @('--project-directory', $repoRoot, '-f', $composeFile, '-p', $composeProject)
$testExitCode = 1
$containerId = $null

try {
    & docker compose @composeArgs up -d
    if ($LASTEXITCODE -ne 0) {
        throw 'Nao foi possivel iniciar o PostgreSQL de testes.'
    }

    $deadline = (Get-Date).AddSeconds(90)
    do {
        $containerId = (& docker compose @composeArgs ps -q postgres).Trim()
        $health = if ($containerId) {
            (& docker inspect --format '{{.State.Health.Status}}' $containerId).Trim()
        } else {
            'starting'
        }

        if ($health -eq 'healthy') {
            break
        }

        if ($health -eq 'unhealthy' -or (Get-Date) -ge $deadline) {
            & docker compose @composeArgs logs postgres
            throw "O PostgreSQL de testes nao ficou saudavel. Estado: $health"
        }

        Start-Sleep -Seconds 2
    } while ($true)

    Write-Host "PostgreSQL de testes pronto em localhost:$Port."
    Write-Host "Executando a suite com MOODLE_POSTGRES_TEST_CONNECTION."

    $runSettings = Join-Path $repoRoot 'tests/postgres.runsettings'
    if (-not (Test-Path -LiteralPath $runSettings -PathType Leaf)) {
        throw "O arquivo de configuracao do gate PostgreSQL nao foi encontrado: $runSettings"
    }

    & dotnet test (Join-Path $repoRoot 'Moodle.slnx') --configuration $Configuration --settings $runSettings
    $testExitCode = $LASTEXITCODE
}
finally {
    if (-not $KeepDatabase) {
        & docker compose @composeArgs down --remove-orphans
    }

    Remove-Item Env:MOODLE_POSTGRES_TEST_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:MOODLE_POSTGRES_TEST_DATABASE -ErrorAction SilentlyContinue
    Remove-Item Env:MOODLE_POSTGRES_TEST_USERNAME -ErrorAction SilentlyContinue
    Remove-Item Env:MOODLE_POSTGRES_TEST_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:MOODLE_POSTGRES_TEST_CONNECTION -ErrorAction SilentlyContinue
}

exit $testExitCode
