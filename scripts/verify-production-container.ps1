[CmdletBinding()]
param(
    [string]$Image = 'moodle-release-validation',
    [int]$HostPort = 0,
    [switch]$RequireGradingPackageTransport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Remove-TemporaryContainer {
    param([string]$Name)

    if (-not [string]::IsNullOrWhiteSpace($Name)) {
        & docker rm --force $Name *> $null
    }
}

function Read-SseJson {
    param(
        $Response,
        [string]$Operation
    )

    $match = [regex]::Match(
        [string]$Response.Content,
        'data:\s*(?<json>\{.*\})\s*$',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        throw "POST /mcp $Operation nao retornou uma resposta SSE JSON-RPC."
    }

    try {
        return $match.Groups['json'].Value | ConvertFrom-Json
    }
    catch {
        throw "POST /mcp $Operation retornou JSON-RPC invalido: $($_.Exception.Message)"
    }
}

& docker image inspect $Image *> $null
if ($LASTEXITCODE -ne 0) {
    throw "A imagem '$Image' não existe localmente. Crie-a antes de executar esta certificação."
}

if ($HostPort -eq 0) {
    $HostPort = Get-FreeLoopbackPort
}

if ($HostPort -lt 1 -or $HostPort -gt 65535) {
    throw 'HostPort deve estar entre 1 e 65535.'
}

$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$networkName = "moodle-cert-net-$suffix"
$databaseName = "moodle-cert-db-$suffix"
$applicationName = "moodle-cert-app-$suffix"
$connectionString = "Host=$databaseName;Port=5432;Database=MOODLE;Username=MOODLE;Password=validation-password"
$networkCreated = $false
$databaseCreated = $false
$applicationCreated = $false
$networkSubnet = $null

try {
    & docker network create $networkName *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Não foi possível criar a rede temporária de certificação.'
    }
    $networkCreated = $true
    $networkSubnet = (& docker network inspect --format '{{(index .IPAM.Config 0).Subnet}}' $networkName).Trim()
    if ([string]::IsNullOrWhiteSpace($networkSubnet)) {
        throw 'Não foi possível determinar a sub-rede da rede temporária de certificação.'
    }

    & docker run --detach --rm --name $databaseName --network $networkName `
        --env POSTGRES_DB=MOODLE `
        --env POSTGRES_USER=MOODLE `
        --env POSTGRES_PASSWORD=validation-password `
        postgres:16-alpine *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Não foi possível iniciar o PostgreSQL temporário.'
    }
    $databaseCreated = $true

    $databaseReady = $false
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        & docker exec $databaseName pg_isready -U MOODLE -d MOODLE *> $null
        if ($LASTEXITCODE -eq 0) {
            $databaseReady = $true
            break
        }

        Start-Sleep -Seconds 1
    }

    if (-not $databaseReady) {
        throw 'O PostgreSQL temporário não ficou pronto para a certificação.'
    }

    & docker run --detach --rm --name $applicationName --network $networkName `
        --publish "127.0.0.1:${HostPort}:8080" `
        --env ASPNETCORE_ENVIRONMENT=Production `
        --env "Postgres__ConnectionString=$connectionString" `
        --env 'ConnectorSecrets__EncryptionKeyBase64=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=' `
        --env "ForwardedHeaders__KnownNetworks__0=$networkSubnet" `
        --env 'AdminApi__ApiKey=production-validation-admin-key' `
        --env 'MEDIATR_LICENSE_KEY=production-validation-license' `
        --env 'GradingLimits__GradingPackageBlobRootDirectory=/app/data/private-grading-packages' `
        --env 'OAuth__Issuer=https://validation.example' `
        --env 'OAuth__Audience=https://validation.example/mcp' `
        $Image *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Não foi possível iniciar a imagem de produção temporária.'
    }
    $applicationCreated = $true

    $healthResponse = $null
    for ($attempt = 1; $attempt -le 45; $attempt++) {
        try {
            $healthResponse = Invoke-WebRequest -UseBasicParsing `
                -Uri "http://127.0.0.1:$HostPort/health" `
                -Headers @{ 'X-Forwarded-Proto' = 'https' } `
                -TimeoutSec 3
            if ($healthResponse.StatusCode -eq 200) {
                break
            }
        }
        catch {
            Start-Sleep -Seconds 1
        }
    }

    if ($null -eq $healthResponse -or $healthResponse.StatusCode -ne 200) {
        $logs = & docker logs $applicationName 2>&1 | Select-Object -Last 40 | Out-String
        throw "A imagem não respondeu ao healthcheck. Logs finais: $logs"
    }

    if ($healthResponse.Headers['Strict-Transport-Security'] -ne 'max-age=31536000') {
        throw 'A resposta HTTPS encaminhada não incluiu Strict-Transport-Security.'
    }

    $statusResponse = Invoke-WebRequest -UseBasicParsing `
        -Uri "http://127.0.0.1:$HostPort/api/status" `
        -Headers @{ 'X-Forwarded-Proto' = 'https' } `
        -TimeoutSec 5
    if ($statusResponse.StatusCode -ne 200) {
        throw 'O endpoint /api/status não respondeu com HTTP 200.'
    }

    if ($statusResponse.Headers['Strict-Transport-Security'] -ne 'max-age=31536000') {
        throw 'O endpoint /api/status não incluiu Strict-Transport-Security.'
    }

    if ($RequireGradingPackageTransport) {
        $mcpUri = "http://127.0.0.1:$HostPort/mcp"
        $initializePayload = @{
            jsonrpc = '2.0'
            id = 'production-container-initialize'
            method = 'initialize'
            params = @{
                protocolVersion = '2025-06-18'
                capabilities = @{}
                clientInfo = @{
                    name = 'production-container-contract'
                    version = '1.0'
                }
            }
        } | ConvertTo-Json -Compress -Depth 8

        $initializeResponse = Invoke-WebRequest -UseBasicParsing `
            -Uri $mcpUri `
            -Method Post `
            -ContentType 'application/json' `
            -Headers @{ Accept = 'application/json, text/event-stream' } `
            -Body $initializePayload `
            -TimeoutSec 10
        if ($initializeResponse.StatusCode -ne 200) {
            throw "POST /mcp initialize retornou HTTP $($initializeResponse.StatusCode)."
        }

        $sessionId = @($initializeResponse.Headers['Mcp-Session-Id'])[0]
        if ([string]::IsNullOrWhiteSpace($sessionId)) {
            throw 'POST /mcp initialize nao retornou Mcp-Session-Id.'
        }

        $mcpHeaders = @{
            Accept = 'application/json, text/event-stream'
            'Mcp-Session-Id' = $sessionId
        }
        $notificationPayload = @{
            jsonrpc = '2.0'
            method = 'notifications/initialized'
            params = @{}
        } | ConvertTo-Json -Compress
        $notificationResponse = Invoke-WebRequest -UseBasicParsing `
            -Uri $mcpUri `
            -Method Post `
            -ContentType 'application/json' `
            -Headers $mcpHeaders `
            -Body $notificationPayload `
            -TimeoutSec 10
        if ($notificationResponse.StatusCode -notin 200, 202) {
            throw "POST /mcp notifications/initialized retornou HTTP $($notificationResponse.StatusCode)."
        }

        $toolsPayload = @{
            jsonrpc = '2.0'
            id = 'production-container-tools-list'
            method = 'tools/list'
            params = @{}
        } | ConvertTo-Json -Compress
        $toolsResponse = Invoke-WebRequest -UseBasicParsing `
            -Uri $mcpUri `
            -Method Post `
            -ContentType 'application/json' `
            -Headers $mcpHeaders `
            -Body $toolsPayload `
            -TimeoutSec 10
        $toolsMessage = Read-SseJson -Response $toolsResponse -Operation 'tools/list'
        $toolNames = @($toolsMessage.result.tools | ForEach-Object { [string]$_.name })
        $requiredToolNames = @(
            'prepare_grading_packages',
            'get_grading_package_status',
            'import_grading_package_results'
        )
        $missingTools = @($requiredToolNames | Where-Object { $toolNames -notcontains $_ })
        if ($missingTools.Count -gt 0) {
            throw "POST /mcp tools/list nao expos os contratos de pacotes: $($missingTools -join ', ')."
        }

        $unauthorizedCallPayload = @{
            jsonrpc = '2.0'
            id = 'production-container-unauthorized-call'
            method = 'tools/call'
            params = @{
                name = 'prepare_grading_packages'
                arguments = @{
                    batchJobId = '00000000-0000-0000-0000-000000000000'
                }
            }
        } | ConvertTo-Json -Compress -Depth 8
        $unauthorizedCallResponse = Invoke-WebRequest -UseBasicParsing `
            -Uri $mcpUri `
            -Method Post `
            -ContentType 'application/json' `
            -Headers $mcpHeaders `
            -Body $unauthorizedCallPayload `
            -SkipHttpErrorCheck `
            -TimeoutSec 10

        try {
            $unauthorizedCallMessage = $unauthorizedCallResponse.Content | ConvertFrom-Json
        }
        catch {
            throw "POST /mcp tools/call sem credencial retornou JSON invalido: $($_.Exception.Message)"
        }

        if ($unauthorizedCallResponse.StatusCode -eq 200) {
            if ($unauthorizedCallMessage.result.isError -ne $true) {
                throw 'POST /mcp tools/call sem credencial deve retornar result.isError=true.'
            }

            $oauthChallenge = @($unauthorizedCallMessage.result._meta.'mcp/www_authenticate')
            if ($oauthChallenge.Count -eq 0) {
                throw 'POST /mcp tools/call sem credencial deve retornar _meta.mcp/www_authenticate.'
            }
        }
        elseif ($unauthorizedCallResponse.StatusCode -eq 401) {
            if ($unauthorizedCallMessage.error -notin @('missing_api_key', 'missing_mcp_credentials', 'missing_or_invalid_jwt', 'invalid_token')) {
                throw "POST /mcp tools/call sem credencial retornou HTTP 401 sem erro de autenticacao reconhecido: $($unauthorizedCallResponse.Content)"
            }
        }
        else {
            throw "POST /mcp tools/call sem credencial retornou HTTP $($unauthorizedCallResponse.StatusCode), esperado 200 com desafio MCP OAuth ou 401 estruturado."
        }

        $templatesPayload = @{
            jsonrpc = '2.0'
            id = 'production-container-resource-templates'
            method = 'resources/templates/list'
            params = @{}
        } | ConvertTo-Json -Compress
        $templatesResponse = Invoke-WebRequest -UseBasicParsing `
            -Uri $mcpUri `
            -Method Post `
            -ContentType 'application/json' `
            -Headers $mcpHeaders `
            -Body $templatesPayload `
            -TimeoutSec 10
        $templatesMessage = Read-SseJson -Response $templatesResponse -Operation 'resources/templates/list'
        $templateUris = @($templatesMessage.result.resourceTemplates | ForEach-Object { [string]$_.uriTemplate })
        if ($templateUris -notcontains 'moodle://grading-package/{packageId}') {
            throw "POST /mcp resources/templates/list nao expos o resource template de pacotes."
        }
    }

    Write-Output "Production container contract passed for '$Image'."
}
finally {
    if ($applicationCreated) {
        Remove-TemporaryContainer -Name $applicationName
    }

    if ($databaseCreated) {
        Remove-TemporaryContainer -Name $databaseName
    }

    if ($networkCreated) {
        & docker network rm $networkName *> $null
    }
}
