#Requires -Version 7.0
<#
.SYNOPSIS
  Inicializa e executa o Moodle MCP local para integracao privada ao ChatGPT.
.DESCRIPTION
  Bootstrap usa HTTP 127.0.0.1:8789, modo Development e sem tunel.
  Start usa OAuth/JWT obrigatorio, URL HTTPS externa, registro fechado e porta 8788.
  Requer Docker Desktop e uma licenca MediatR fornecida em .env.chatgpt-local.
#>
[CmdletBinding()]
param(
    [ValidateSet('Init','Bootstrap','Start','Check','Status','Stop')]
    [string]$Action = 'Status',
    [string]$PublicOrigin = '',
    [ValidateRange(1024,65535)][int]$Port = 8788,
    [ValidateRange(1024,65535)][int]$BootstrapPort = 8789
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$compose = Join-Path $root 'deploy/docker-compose.chatgpt-local.yml'
$envFile = Join-Path $root '.env.chatgpt-local'
$project = 'moodle-chatgpt-local'

function New-SecretHex {
    return [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
}

function Assert-LocalEnvironment {
    if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
        throw "Execute primeiro: pwsh ./scripts/start-chatgpt-local.ps1 -Action Init"
    }
}

function Invoke-LocalCompose {
    param([string[]]$Arguments)
    & docker compose --env-file $envFile -p $project -f $compose @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') falhou ($LASTEXITCODE)."
    }
}

if ($Action -eq 'Init') {
    if (Test-Path -LiteralPath $envFile) {
        throw ".env.chatgpt-local ja existe. Mantenha os segredos atuais para preservar o acesso aos dados cifrados."
    }
    $dbPassword = New-SecretHex
    $adminKey = New-SecretHex
    $aesKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $content = @(
        '# ARQUIVO PRIVADO - nao enviar, compartilhar ou versionar.'
        'CHATGPT_DB_PASSWORD=' + $dbPassword
        'CHATGPT_ENCRYPTION_KEY_BASE64=' + $aesKey
        'CHATGPT_ADMIN_API_KEY=' + $adminKey
        '# Preencha localmente com uma licenca MediatR valida antes de Start.'
        'MEDIATR_LICENSE_KEY='
    ) -join "`n"
    [IO.File]::WriteAllText($envFile, $content + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Criado .env.chatgpt-local (ignorado pelo Git). Configure a licenca MediatR antes de Start."
    exit 0
}

Assert-LocalEnvironment
Get-Command docker -ErrorAction Stop | Out-Null

if ($Action -eq 'Bootstrap') {
    if ($Port -eq $BootstrapPort) {
        throw "BootstrapPort e Port precisam ser diferentes para impedir publicacao acidental do modo Development."
    }
    $env:CHATGPT_PUBLIC_ORIGIN = "http://127.0.0.1:$BootstrapPort"
    $env:CHATGPT_ALLOWED_HOSTS = 'localhost;127.0.0.1'
    $env:CHATGPT_LOCAL_PORT = [string]$BootstrapPort
    $env:CHATGPT_ENVIRONMENT = 'Development'
    $env:CHATGPT_HTTPS_REQUIRED = 'false'
    $env:CHATGPT_REGISTRATION_ENABLED = 'true'
    Write-Warning 'BOOTSTRAP: nao abra ngrok ou outro tunel apontado para esta porta. Use apenas para criar sua conta local.'
    Invoke-LocalCompose -Arguments @('up','-d','--build','--force-recreate')
    Write-Host "Abra http://127.0.0.1:$BootstrapPort e crie sua conta. Depois execute Stop e Start em modo Production."
    exit 0
}

if ($Action -eq 'Start' -or $Action -eq 'Check') {
    $uri = $null
    $absolute = [Uri]::TryCreate($PublicOrigin, [UriKind]::Absolute, [ref]$uri)
    if (-not $absolute -or $uri.Scheme -ne 'https' -or -not $uri.IsDefaultPort -or
        $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo -or
        $uri.Host -eq 'localhost' -or $uri.Host -eq '127.0.0.1') {
        throw "Informe -PublicOrigin 'https://seu-tunel.example' (HTTPS publico, sem caminho, consulta ou credenciais)."
    }
    $PublicOrigin = $uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
}

if ($Action -eq 'Start') {
    $license = [string]$env:MEDIATR_LICENSE_KEY
    if ([string]::IsNullOrWhiteSpace($license)) {
        $line = @(Get-Content -LiteralPath $envFile |
            Where-Object { $_ -match '^MEDIATR_LICENSE_KEY=' } | Select-Object -Last 1)
        if ($line.Count -gt 0) {
            $license = ($line[0] -replace '^MEDIATR_LICENSE_KEY=', '').Trim()
        }
    }
    if ([string]::IsNullOrWhiteSpace($license)) {
        throw 'Configure MEDIATR_LICENSE_KEY em .env.chatgpt-local (ou na sessao do PowerShell) antes de Start.'
    }

    $env:CHATGPT_PUBLIC_ORIGIN = $PublicOrigin
    $env:CHATGPT_ALLOWED_HOSTS = $uri.Host
    $env:CHATGPT_LOCAL_PORT = [string]$Port
    $env:CHATGPT_ENVIRONMENT = 'Production'
    $env:CHATGPT_HTTPS_REQUIRED = 'true'
    $env:CHATGPT_REGISTRATION_ENABLED = 'false'
    Invoke-LocalCompose -Arguments @('up','-d','--build','--force-recreate')
    Write-Host "MCP local: http://127.0.0.1:$Port/mcp"
    Write-Host "MCP para ChatGPT: $PublicOrigin/mcp"
    Write-Host 'Confirme HTTPS e OAuth com -Action Check -PublicOrigin antes de conectar o plugin.'
    exit 0
}

# Stop/Status nao criam ou expõem endpoints publicos.
if ($Action -eq 'Status' -or $Action -eq 'Stop') {
    $env:CHATGPT_PUBLIC_ORIGIN = 'https://local-placeholder.invalid'
    $env:CHATGPT_ALLOWED_HOSTS = 'localhost;127.0.0.1'
    $env:CHATGPT_LOCAL_PORT = [string]$Port
    if ($Action -eq 'Status') { Invoke-LocalCompose -Arguments @('ps') }
    else { Invoke-LocalCompose -Arguments @('down') }
    exit 0
}

if ($Action -eq 'Check') {
    # Somente metadados publicos; nao envia credenciais.
    $health = Invoke-WebRequest -Uri "$PublicOrigin/health" -TimeoutSec 15
    if ($health.StatusCode -ne 200) {
        throw "Healthcheck HTTPS nao respondeu 200."
    }
    $server = Invoke-RestMethod -Uri "$PublicOrigin/.well-known/oauth-authorization-server" -TimeoutSec 15
    $resource = Invoke-RestMethod -Uri "$PublicOrigin/.well-known/oauth-protected-resource/mcp" -TimeoutSec 15
    if ([string]$server.issuer.TrimEnd('/') -ne $PublicOrigin -or
        [string]$server.authorization_endpoint -ne "$PublicOrigin/authorize" -or
        [string]$server.token_endpoint -ne "$PublicOrigin/token" -or
        @($resource.authorization_servers) -notcontains $server.issuer) {
        throw 'OAuth metadata nao corresponde a URL externa. Verifique APP_DOMAIN, X-Forwarded-Proto e reinicie Start.'
    }
    Write-Host "OK - HTTPS, health e descoberta OAuth: $PublicOrigin/mcp"
}
