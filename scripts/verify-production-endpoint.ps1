[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9.-]+$')]
    [string]$AppDomain,

    [string]$ExpectedGitCommit,

    [switch]$RequireStrictTransportSecurity,

    [switch]$RequireGradingPackageTransport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$baseUri = "https://$AppDomain"
$expectedIssuer = "$baseUri/"
$expectedAudience = "$baseUri/mcp"
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) {
    $failures.Add($Message)
}

function Get-HttpStatusCodeFromException($Exception) {
    $responseProperty = $Exception.PSObject.Properties['Response']
    if ($null -eq $responseProperty -or $null -eq $responseProperty.Value) {
        return $null
    }

    return [int]$responseProperty.Value.StatusCode
}

function Get-Response([string]$Path) {
    try {
        return Invoke-WebRequest -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 20 -Uri "$baseUri$Path"
    }
    catch {
        $statusCode = Get-HttpStatusCodeFromException $_.Exception
        Add-Failure "GET $Path failed$(if ($null -ne $statusCode) { " with HTTP $statusCode" }): $($_.Exception.Message)"
        return $null
    }
}

function Read-JsonResponse($Response, [string]$Path) {
    if ($null -eq $Response) {
        return $null
    }

    if ($Response.StatusCode -ne 200) {
        Add-Failure "GET $Path returned HTTP $($Response.StatusCode), expected 200."
        return $null
    }

    try {
        return $Response.Content | ConvertFrom-Json
    }
    catch {
        Add-Failure "GET $Path did not return valid JSON: $($_.Exception.Message)"
        return $null
    }
}

function Require-Equal($Actual, $Expected, [string]$Name) {
    if ($Actual -cne $Expected) {
        Add-Failure "$Name must be '$Expected', but was '$Actual'."
    }
}

function Parse-McpSseJson([string]$Content, [string]$Operation) {
    $match = [regex]::Match($Content, 'data:\s*(?<json>\{.*\})\s*$', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        Add-Failure "POST /mcp $Operation must return an SSE JSON-RPC response."
        return $null
    }

    try {
        return $match.Groups['json'].Value | ConvertFrom-Json
    }
    catch {
        Add-Failure "POST /mcp $Operation returned invalid SSE JSON: $($_.Exception.Message)"
        return $null
    }
}

function Parse-McpJsonRpcResponse([string]$Content, [string]$Operation) {
    if ($Content.TrimStart().StartsWith('{', [System.StringComparison]::Ordinal)) {
        try {
            return $Content | ConvertFrom-Json
        }
        catch {
            Add-Failure "POST /mcp $Operation returned invalid JSON: $($_.Exception.Message)"
            return $null
        }
    }

    return Parse-McpSseJson $Content $Operation
}

$health = Get-Response '/health'
if ($null -ne $health) {
    if ($health.StatusCode -ne 200) {
        Add-Failure "GET /health returned HTTP $($health.StatusCode), expected 200."
    }

    if ($health.Headers['X-Content-Type-Options'] -ne 'nosniff') {
        Add-Failure 'GET /health must send X-Content-Type-Options: nosniff.'
    }

    if ($health.Headers['Referrer-Policy'] -ne 'strict-origin-when-cross-origin') {
        Add-Failure 'GET /health must send Referrer-Policy: strict-origin-when-cross-origin.'
    }

    if ([string]::IsNullOrWhiteSpace($health.Headers['Content-Security-Policy'])) {
        Add-Failure 'GET /health must send Content-Security-Policy.'
    }

    if ($RequireStrictTransportSecurity -and [string]::IsNullOrWhiteSpace($health.Headers['Strict-Transport-Security'])) {
        Add-Failure 'GET /health must send Strict-Transport-Security in the production proxy.'
    }

    if ($RequireStrictTransportSecurity) {
        $serverHeader = [string]$health.Headers['Server']
        $cloudflareRay = [string]$health.Headers['CF-Ray']
        $isCloudflareEdgeHeader =
            -not [string]::IsNullOrWhiteSpace($cloudflareRay) -and
            [string]::Equals($serverHeader, 'cloudflare', [System.StringComparison]::OrdinalIgnoreCase)

        if (-not [string]::IsNullOrWhiteSpace($serverHeader) -and -not $isCloudflareEdgeHeader) {
            Add-Failure "GET /health must not expose the origin Server header, but received '$serverHeader'."
        }
    }
}

$status = Read-JsonResponse (Get-Response '/api/status') '/api/status'
if ($null -ne $status) {
    if ($status.ok -ne $true -or $status.status -ne 'online') {
        Add-Failure '/api/status must report an online service.'
    }

    Require-Equal $status.endpoint '/mcp' '/api/status.endpoint'
    if ($status.auth.requireJwt -ne $true -or $status.auth.requireApiKey -ne $false) {
        Add-Failure '/api/status must report RequireJwt=true and RequireApiKey=false.'
    }

    Require-Equal $status.auth.issuer $expectedIssuer '/api/status.auth.issuer'
    Require-Equal $status.auth.audience $expectedAudience '/api/status.auth.audience'
    if ($status.auth.dynamicClientRegistrationEnabled -ne $true) {
        Add-Failure '/api/status must report Dynamic Client Registration enabled for OAuth connections.'
    }

    if ([int]$status.toolsCount -lt 1) {
        Add-Failure '/api/status.toolsCount must be positive.'
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedGitCommit) -and $status.gitCommit -ne $ExpectedGitCommit) {
        Add-Failure "/api/status.gitCommit must be '$ExpectedGitCommit', but was '$($status.gitCommit)'."
    }
}

$protectedResource = Read-JsonResponse (Get-Response '/.well-known/oauth-protected-resource/mcp') '/.well-known/oauth-protected-resource/mcp'
if ($null -ne $protectedResource) {
    Require-Equal $protectedResource.resource $expectedAudience 'oauth-protected-resource.resource'
    if (@($protectedResource.authorization_servers) -notcontains $expectedIssuer) {
        Add-Failure "oauth-protected-resource.authorization_servers must include '$expectedIssuer'."
    }
}

$openid = Read-JsonResponse (Get-Response '/.well-known/openid-configuration') '/.well-known/openid-configuration'
if ($null -ne $openid) {
    Require-Equal $openid.issuer $expectedIssuer 'openid-configuration.issuer'
    Require-Equal $openid.authorization_endpoint "${expectedIssuer}authorize" 'openid-configuration.authorization_endpoint'
    Require-Equal $openid.token_endpoint "${expectedIssuer}token" 'openid-configuration.token_endpoint'
    Require-Equal $openid.jwks_uri "${expectedIssuer}.well-known/jwks" 'openid-configuration.jwks_uri'
}

$authorizationServer = Read-JsonResponse (Get-Response '/.well-known/oauth-authorization-server') '/.well-known/oauth-authorization-server'
if ($null -ne $authorizationServer) {
    Require-Equal $authorizationServer.registration_endpoint ($baseUri + '/register') 'oauth-authorization-server.registration_endpoint'
    if (@($authorizationServer.token_endpoint_auth_methods_supported) -notcontains 'none') {
        Add-Failure 'oauth-authorization-server.token_endpoint_auth_methods_supported must include none.'
    }

    if (@($authorizationServer.code_challenge_methods_supported) -notcontains 'S256') {
        Add-Failure 'oauth-authorization-server.code_challenge_methods_supported must include S256.'
    }
}

$jwks = Read-JsonResponse (Get-Response '/.well-known/jwks') '/.well-known/jwks'
if ($null -ne $jwks -and @($jwks.keys).Count -lt 1) {
    Add-Failure '/.well-known/jwks must expose at least one public key.'
}

$initializePayload = @{
    jsonrpc = '2.0'
    id = 'production-contract-check'
    method = 'initialize'
    params = @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{
            name = 'production-contract-check'
            version = '1.0'
        }
    }
} | ConvertTo-Json -Compress -Depth 5

try {
    $initialize = Invoke-WebRequest -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 20 -Method Post -Uri "$baseUri/mcp" -ContentType 'application/json' -Headers @{ Accept = 'application/json, text/event-stream' } -Body $initializePayload
    if ($initialize.StatusCode -ne 200) {
        Add-Failure "POST /mcp initialize returned HTTP $($initialize.StatusCode), expected 200."
    }

    if ($initialize.Headers['Content-Type'] -notmatch 'text/event-stream') {
        Add-Failure 'POST /mcp initialize must return an event stream.'
    }

    $mcpSessionId = @($initialize.Headers['Mcp-Session-Id'])[0]
    if ([string]::IsNullOrWhiteSpace($mcpSessionId)) {
        Add-Failure 'POST /mcp initialize must return Mcp-Session-Id.'
    }

    if ($initialize.Content -notmatch '"protocolVersion"') {
        Add-Failure 'POST /mcp initialize response is missing protocolVersion.'
    }

    if (-not [string]::IsNullOrWhiteSpace($mcpSessionId)) {
        $mcpHeaders = @{ Accept = 'application/json, text/event-stream'; 'Mcp-Session-Id' = $mcpSessionId }
        $notificationPayload = @{
            jsonrpc = '2.0'
            method = 'notifications/initialized'
            params = @{}
        } | ConvertTo-Json -Compress
        $notification = Invoke-WebRequest -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 20 -Method Post -Uri "$baseUri/mcp" -ContentType 'application/json' -Headers $mcpHeaders -Body $notificationPayload
        if ($notification.StatusCode -notin 200, 202) {
            Add-Failure "POST /mcp notifications/initialized returned HTTP $($notification.StatusCode), expected 200 or 202."
        }

        $toolsPayload = @{
            jsonrpc = '2.0'
            id = 'production-contract-tools-list'
            method = 'tools/list'
            params = @{}
        } | ConvertTo-Json -Compress
        $toolsResponse = Invoke-WebRequest -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 20 -Method Post -Uri "$baseUri/mcp" -ContentType 'application/json' -Headers $mcpHeaders -Body $toolsPayload
        if ($toolsResponse.StatusCode -ne 200) {
            Add-Failure "POST /mcp tools/list returned HTTP $($toolsResponse.StatusCode), expected 200."
        }

        $toolsMessage = Parse-McpSseJson $toolsResponse.Content 'tools/list'
        if ($null -ne $toolsMessage) {
            $tools = @($toolsMessage.result.tools)
            if ($tools.Count -lt 1) {
                Add-Failure 'POST /mcp tools/list must expose at least one tool.'
            }
            elseif ($null -ne $status -and $tools.Count -gt [int]$status.toolsCount) {
                # /api/status reports the registered, feature-enabled inventory.
                # tools/list is request-specific and may legitimately be smaller
                # after OAuth scopes, linked-connection policy, and Moodle
                # capability filtering are applied.
                Add-Failure "POST /mcp tools/list exposed $($tools.Count) tools, more than the /api/status inventory of $($status.toolsCount)."
            }

            if (@($tools | Where-Object { $null -eq $_.outputSchema }).Count -gt 0) {
                Add-Failure 'POST /mcp tools/list must expose outputSchema for every production tool.'
            }

            if ($RequireGradingPackageTransport) {
                $requiredToolNames = @(
                    'prepare_grading_packages',
                    'get_grading_package_status',
                    'import_grading_package_results'
                )
                $exposedToolNames = @($tools | ForEach-Object { [string]$_.name })
                foreach ($requiredToolName in $requiredToolNames) {
                    if ($exposedToolNames -notcontains $requiredToolName) {
                        Add-Failure "POST /mcp tools/list must expose '$requiredToolName' when grading package transport is required."
                    }
                }
            }
        }

        $unauthorizedCallPayload = @{
            jsonrpc = '2.0'
            id = 'production-contract-unauthorized-call'
            method = 'tools/call'
            params = @{
                name = 'prepare_grading_packages'
                arguments = @{
                    batchJobId = '00000000-0000-0000-0000-000000000000'
                }
            }
        } | ConvertTo-Json -Compress -Depth 8
        try {
            $unauthorizedCallResponse = Invoke-WebRequest -UseBasicParsing -SkipHttpErrorCheck `
                -MaximumRedirection 0 -TimeoutSec 20 -Method Post -Uri "$baseUri/mcp" `
                -ContentType 'application/json' -Headers $mcpHeaders -Body $unauthorizedCallPayload
            $unauthorizedCallMessage = Parse-McpJsonRpcResponse $unauthorizedCallResponse.Content 'tools/call sem credencial'
            if ($unauthorizedCallResponse.StatusCode -eq 200) {
                if ($null -eq $unauthorizedCallMessage -or $unauthorizedCallMessage.result.isError -ne $true) {
                    Add-Failure 'POST /mcp tools/call sem credencial deve retornar result.isError=true.'
                }
                elseif (@($unauthorizedCallMessage.result._meta.'mcp/www_authenticate').Count -eq 0) {
                    Add-Failure 'POST /mcp tools/call sem credencial deve retornar _meta.mcp/www_authenticate.'
                }
            }
            elseif ($unauthorizedCallResponse.StatusCode -eq 401) {
                if ($null -eq $unauthorizedCallMessage -or
                    $unauthorizedCallMessage.error -notin @('missing_api_key', 'missing_mcp_credentials', 'missing_or_invalid_jwt', 'invalid_token')) {
                    Add-Failure 'POST /mcp tools/call sem credencial deve retornar 401 estruturado ou desafio OAuth MCP.'
                }
            }
            else {
                Add-Failure "POST /mcp tools/call sem credencial retornou HTTP $($unauthorizedCallResponse.StatusCode), esperado 200 ou 401."
            }
        }
        catch {
            Add-Failure "POST /mcp tools/call sem credencial falhou: $($_.Exception.Message)"
        }

        if ($RequireGradingPackageTransport) {
            $templatesPayload = @{
                jsonrpc = '2.0'
                id = 'production-contract-resource-templates'
                method = 'resources/templates/list'
                params = @{}
            } | ConvertTo-Json -Compress
            $templatesResponse = Invoke-WebRequest -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 20 -Method Post -Uri "$baseUri/mcp" -ContentType 'application/json' -Headers $mcpHeaders -Body $templatesPayload
            if ($templatesResponse.StatusCode -ne 200) {
                Add-Failure "POST /mcp resources/templates/list returned HTTP $($templatesResponse.StatusCode), expected 200."
            }
            else {
                $templatesMessage = Parse-McpSseJson $templatesResponse.Content 'resources/templates/list'
                if ($null -ne $templatesMessage) {
                    $resourceTemplates = @($templatesMessage.result.resourceTemplates)
                    if (@($resourceTemplates | Where-Object {
                                $_.uriTemplate -eq 'moodle://grading-package/{packageId}'
                            }).Count -eq 0) {
                        Add-Failure "POST /mcp resources/templates/list must expose 'moodle://grading-package/{packageId}' when grading package transport is required."
                    }
                }
            }
        }
    }
}
catch {
    $statusCode = Get-HttpStatusCodeFromException $_.Exception
    Add-Failure "POST /mcp initialize failed$(if ($null -ne $statusCode) { " with HTTP $statusCode" }): $($_.Exception.Message)"
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Production endpoint verification failed with $($failures.Count) issue(s)."
}

Write-Host "Production endpoint verification passed: $baseUri"
