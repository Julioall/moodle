[CmdletBinding()]
param(
    [Uri]$Endpoint,
    [Guid]$BatchJobId = [Guid]::Empty,
    [Guid[]]$PackageIds = @(),
    [string[]]$IndividualResourceUris = @(),
    [string]$BearerToken = $env:MCP_BEARER_TOKEN,
    [string]$ApiKey = $env:MCP_API_KEY,
    [ValidateRange(1, 100)]
    [int]$Repetitions = 10,
    [ValidateRange(1, 600)]
    [int]$PollTimeoutSeconds = 300,
    [ValidateRange(50, 10000)]
    [int]$PollIntervalMilliseconds = 500,
    [switch]$AllowInsecureHttp,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-BenchmarkPercentiles {
    [CmdletBinding()]
    param(
        [double[]]$Values
    )

    if ($Values.Count -eq 0) {
        return [pscustomobject]@{ P50 = $null; P95 = $null }
    }

    $sorted = @($Values | Sort-Object)
    $getNearestRank = {
        param([double]$Percentile)
        $rank = [int][Math]::Ceiling($sorted.Count * $Percentile)
        $sorted[[Math]::Max(0, $rank - 1)]
    }

    [pscustomobject]@{
        P50 = & $getNearestRank 0.50
        P95 = & $getNearestRank 0.95
    }
}

function Assert-BenchmarkEndpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Uri]$Endpoint,
        [bool]$AllowInsecureHttp = $false
    )

    if (-not $Endpoint.IsAbsoluteUri) {
        throw 'Endpoint deve ser uma URI absoluta.'
    }

    if ($Endpoint.Scheme -notin @('https', 'http')) {
        throw 'Endpoint deve usar HTTPS ou HTTP.'
    }

    if ($Endpoint.Scheme -eq 'http' -and -not $AllowInsecureHttp) {
        throw 'Endpoint HTTP exige -AllowInsecureHttp; use HTTPS para homologacao ou producao.'
    }

    if ($Endpoint.Query -or $Endpoint.Fragment) {
        throw 'Endpoint nao pode conter query string ou fragmento.'
    }

    if ($Endpoint.AbsolutePath.TrimEnd('/') -ne '/mcp') {
        throw 'Endpoint deve apontar diretamente para /mcp.'
    }
}

function Get-SafeEndpoint {
    param([Parameter(Mandatory)][Uri]$Endpoint)

    $authority = $Endpoint.GetLeftPart([UriPartial]::Authority)
    "$authority$($Endpoint.AbsolutePath.TrimEnd('/'))"
}

function New-BenchmarkMetricSummary {
    param(
        [Parameter(Mandatory)][string]$Name,
        [double[]]$Values
    )

    $percentiles = Get-BenchmarkPercentiles -Values $Values
    $sorted = @($Values | Sort-Object)
    [pscustomobject]@{
        count = $Values.Count
        min = if ($sorted.Count -gt 0) { $sorted[0] } else { $null }
        max = if ($sorted.Count -gt 0) { $sorted[-1] } else { $null }
        p50 = $percentiles.P50
        p95 = $percentiles.P95
    }
}

function New-BenchmarkSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Samples,
        [Parameter(Mandatory)][Uri]$Endpoint,
        [AllowEmptyString()][string]$BearerToken = ''
    )

    $metricNames = @(
        'prepareMs',
        'statusMs',
        'zipReadMs',
        'individualReadMs',
        'zipBytes',
        'individualBytes',
        'calls',
        'packages'
    )
    $metrics = [ordered]@{}
    foreach ($metricName in $metricNames) {
        $values = @(
            foreach ($sample in $Samples) {
                $property = $sample.PSObject.Properties[$metricName]
                if ($null -ne $property) {
                    [double]$property.Value
                }
            }
        )
        $metrics[$metricName] = New-BenchmarkMetricSummary -Name $metricName -Values $values
    }

    [pscustomobject]@{
        schemaVersion = 1
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        endpoint = Get-SafeEndpoint -Endpoint $Endpoint
        repetitions = $Samples.Count
        metrics = [pscustomobject]$metrics
        samples = @($Samples)
    }
}

function ConvertFrom-McpResponse {
    param([Parameter(Mandatory)][string]$Body)

    $trimmed = $Body.Trim()
    if ($trimmed.StartsWith('{')) {
        return $trimmed | ConvertFrom-Json
    }

    $matches = [regex]::Matches(
        $Body,
        '(?m)^data:\s*(?<json>\{.*\})\s*$')
    if ($matches.Count -eq 0) {
        throw 'A resposta MCP nao contem um evento SSE JSON-RPC.'
    }

    $json = $matches[$matches.Count - 1].Groups['json'].Value
    return $json | ConvertFrom-Json
}

function Invoke-McpJsonRpc {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient]$HttpClient,
        [Parameter(Mandatory)][Uri]$Endpoint,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)]$Parameters,
        [AllowEmptyString()][string]$BearerToken = '',
        [AllowEmptyString()][string]$ApiKey = '',
        [Parameter(Mandatory)][hashtable]$State,
        [switch]$Notification
    )

    $payloadObject = [ordered]@{
        jsonrpc = '2.0'
        method = $Method
        params = $Parameters
    }
    if (-not $Notification) {
        $requestId = $State.NextRequestId
        $State.NextRequestId = [int]$State.NextRequestId + 1
        $payloadObject.id = "grading-package-benchmark-$requestId"
    }
    $payload = $payloadObject | ConvertTo-Json -Compress -Depth 20

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $Endpoint)
    try {
        $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
        $request.Headers.Accept.ParseAdd('application/json')
        $request.Headers.Accept.ParseAdd('text/event-stream')
        if (-not [string]::IsNullOrWhiteSpace($State.SessionId)) {
            $request.Headers.TryAddWithoutValidation('Mcp-Session-Id', $State.SessionId) | Out-Null
        }
        if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $BearerToken)
        }
        elseif (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
            $request.Headers.TryAddWithoutValidation('X-Mcp-Api-Key', $ApiKey) | Out-Null
        }

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $response = $HttpClient.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $stopwatch.Stop()
        $State.Calls = [int]$State.Calls + 1

        if ($response.Headers.Contains('Mcp-Session-Id')) {
            $State.SessionId = @($response.Headers.GetValues('Mcp-Session-Id'))[0]
        }
        if (-not $response.IsSuccessStatusCode) {
            throw "MCP $Method falhou com HTTP $([int]$response.StatusCode)."
        }

        if ($Notification -and [string]::IsNullOrWhiteSpace($body)) {
            return [pscustomobject]@{
                Body = [pscustomobject]@{}
                ElapsedMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
            }
        }

        $message = ConvertFrom-McpResponse -Body $body
        $errorProperty = $message.PSObject.Properties['error']
        if ($null -ne $errorProperty -and $null -ne $errorProperty.Value) {
            throw "MCP $Method retornou erro JSON-RPC $($errorProperty.Value.code)."
        }

        [pscustomobject]@{
            Body = $message
            ElapsedMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        }
    }
    finally {
        $request.Dispose()
    }
}

function Initialize-McpBenchmarkSession {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient]$HttpClient,
        [Parameter(Mandatory)][Uri]$Endpoint,
        [AllowEmptyString()][string]$BearerToken = '',
        [AllowEmptyString()][string]$ApiKey = '',
        [Parameter(Mandatory)][hashtable]$State
    )

    $initialize = Invoke-McpJsonRpc `
        -HttpClient $HttpClient `
        -Endpoint $Endpoint `
        -Method 'initialize' `
        -Parameters @{
            protocolVersion = '2025-06-18'
            capabilities = @{}
            clientInfo = @{ name = 'grading-package-benchmark'; version = '1.0' }
        } `
        -BearerToken $BearerToken `
        -ApiKey $ApiKey `
        -State $State
    if ($null -eq $initialize.Body.result) {
        throw 'O handshake MCP nao retornou result.'
    }

    [void](Invoke-McpJsonRpc `
        -HttpClient $HttpClient `
        -Endpoint $Endpoint `
        -Method 'notifications/initialized' `
        -Parameters @{} `
        -BearerToken $BearerToken `
        -ApiKey $ApiKey `
        -State $State `
        -Notification)
}

function Invoke-McpTool {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient]$HttpClient,
        [Parameter(Mandatory)][Uri]$Endpoint,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Arguments,
        [AllowEmptyString()][string]$BearerToken = '',
        [AllowEmptyString()][string]$ApiKey = '',
        [Parameter(Mandatory)][hashtable]$State
    )

    $call = Invoke-McpJsonRpc `
        -HttpClient $HttpClient `
        -Endpoint $Endpoint `
        -Method 'tools/call' `
        -Parameters @{ name = $Name; arguments = $Arguments } `
        -BearerToken $BearerToken `
        -ApiKey $ApiKey `
        -State $State
    $isErrorProperty = $call.Body.result.PSObject.Properties['isError']
    if ($null -ne $isErrorProperty -and $isErrorProperty.Value -eq $true) {
        throw "A tool MCP $Name retornou isError=true."
    }

    $call
}

function Read-McpResource {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient]$HttpClient,
        [Parameter(Mandatory)][Uri]$Endpoint,
        [Parameter(Mandatory)][string]$ResourceUri,
        [AllowEmptyString()][string]$BearerToken = '',
        [AllowEmptyString()][string]$ApiKey = '',
        [Parameter(Mandatory)][hashtable]$State
    )

    $read = Invoke-McpJsonRpc `
        -HttpClient $HttpClient `
        -Endpoint $Endpoint `
        -Method 'resources/read' `
        -Parameters @{ uri = $ResourceUri } `
        -BearerToken $BearerToken `
        -ApiKey $ApiKey `
        -State $State
    $contents = @($read.Body.result.contents)
    $blob = $contents | Where-Object { -not [string]::IsNullOrWhiteSpace($_.blob) } | Select-Object -First 1
    if ($null -eq $blob) {
        throw "O resource '$ResourceUri' nao retornou blob binario."
    }

    [pscustomobject]@{
        ElapsedMs = $read.ElapsedMs
        Bytes = ([Convert]::FromBase64String([string]$blob.blob)).LongLength
    }
}

function Get-PackageIdsFromPreparation {
    param([Parameter(Mandatory)]$ToolCall)

    $data = $ToolCall.Body.result.structuredContent.data
    $ids = @(
        @($data.packages) |
            ForEach-Object { [Guid]$_.packageId } |
            ForEach-Object { $_.ToString('D') }
    )
    if ($ids.Count -eq 0) {
        throw 'prepare_grading_packages nao retornou packageIds.'
    }

    $ids | Select-Object -Unique
}

function Wait-ForPackage {
    param(
        [Parameter(Mandatory)][System.Net.Http.HttpClient]$HttpClient,
        [Parameter(Mandatory)][Uri]$Endpoint,
        [Parameter(Mandatory)][string]$PackageId,
        [AllowEmptyString()][string]$BearerToken = '',
        [AllowEmptyString()][string]$ApiKey = '',
        [Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [Parameter(Mandatory)][int]$PollMilliseconds
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $status = $null
    $statusCalls = 0
    do {
        $call = Invoke-McpTool `
            -HttpClient $HttpClient `
            -Endpoint $Endpoint `
            -Name 'get_grading_package_status' `
            -Arguments @{ packageId = $PackageId } `
            -BearerToken $BearerToken `
            -ApiKey $ApiKey `
            -State $State
        $statusCalls++
        $status = [string]$call.Body.result.structuredContent.data.descriptor.status
        if ($status -in @('Ready', 'PartiallyReady', 'ResultsReceived', 'NeedsAttention')) {
            break
        }
        if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            throw "Pacote $PackageId nao atingiu estado terminal em $TimeoutSeconds segundos."
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    } while ($true)
    $stopwatch.Stop()

    [pscustomobject]@{
        StatusMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        StatusCalls = $statusCalls
        Status = $status
        Descriptor = $call.Body.result.structuredContent.data.descriptor
    }
}

function Invoke-GradingPackageBenchmark {
    param(
        [Parameter(Mandatory)][Uri]$Endpoint,
        [Guid]$BatchJobId = [Guid]::Empty,
        [Guid[]]$PackageIds = @(),
        [string[]]$IndividualResourceUris = @(),
        [AllowEmptyString()][string]$BearerToken = '',
        [AllowEmptyString()][string]$ApiKey = '',
        [int]$Repetitions = 10,
        [int]$PollTimeoutSeconds = 300,
        [int]$PollIntervalMilliseconds = 500,
        [bool]$AllowInsecureHttp = $false,
        [string]$OutputPath
    )

    Assert-BenchmarkEndpoint -Endpoint $Endpoint -AllowInsecureHttp $AllowInsecureHttp
    if ($BatchJobId -eq [Guid]::Empty -and $PackageIds.Count -eq 0) {
        throw 'Informe -BatchJobId ou ao menos um -PackageIds.'
    }
    if (-not [string]::IsNullOrWhiteSpace($BearerToken) -and -not [string]::IsNullOrWhiteSpace($ApiKey)) {
        throw 'Informe somente BearerToken ou ApiKey, nunca os dois.'
    }
    if ([string]::IsNullOrWhiteSpace($BearerToken) -and [string]::IsNullOrWhiteSpace($ApiKey)) {
        throw 'A medicao exige BearerToken ou ApiKey; o segredo nao sera escrito no relatorio.'
    }

    $httpClient = [System.Net.Http.HttpClient]::new()
    $httpClient.Timeout = [TimeSpan]::FromSeconds(60)
    $state = @{ NextRequestId = 1; SessionId = ''; Calls = 0 }
    $samples = [System.Collections.Generic.List[object]]::new()
    try {
        Initialize-McpBenchmarkSession `
            -HttpClient $httpClient `
            -Endpoint $Endpoint `
            -BearerToken $BearerToken `
            -ApiKey $ApiKey `
            -State $state

        for ($iteration = 1; $iteration -le $Repetitions; $iteration++) {
            $iterationCalls = [int]$state.Calls
            $prepareMs = 0.0
            $iterationPackageIds = @($PackageIds | ForEach-Object { $_.ToString('D') })
            if ($BatchJobId -ne [Guid]::Empty) {
                $prepare = Invoke-McpTool `
                    -HttpClient $httpClient `
                    -Endpoint $Endpoint `
                    -Name 'prepare_grading_packages' `
                    -Arguments @{ batchJobId = $BatchJobId.ToString('D') } `
                    -BearerToken $BearerToken `
                    -ApiKey $ApiKey `
                    -State $state
                $prepareMs = $prepare.ElapsedMs
                $iterationPackageIds = @(Get-PackageIdsFromPreparation -ToolCall $prepare)
            }

            $statusMs = 0.0
            $zipReadMs = 0.0
            $zipBytes = 0L
            $statuses = [System.Collections.Generic.List[string]]::new()
            foreach ($packageId in $iterationPackageIds) {
                $ready = Wait-ForPackage `
                    -HttpClient $httpClient `
                    -Endpoint $Endpoint `
                    -PackageId $packageId `
                    -BearerToken $BearerToken `
                    -ApiKey $ApiKey `
                    -State $state `
                    -TimeoutSeconds $PollTimeoutSeconds `
                    -PollMilliseconds $PollIntervalMilliseconds
                $statusMs += $ready.StatusMs
                $statuses.Add($ready.Status)
                $resourceUri = [string]$ready.Descriptor.downloadResourceUri
                if (-not [string]::IsNullOrWhiteSpace($resourceUri)) {
                    $zip = Read-McpResource `
                        -HttpClient $httpClient `
                        -Endpoint $Endpoint `
                        -ResourceUri $resourceUri `
                        -BearerToken $BearerToken `
                        -ApiKey $ApiKey `
                        -State $state
                    $zipReadMs += $zip.ElapsedMs
                    $zipBytes += $zip.Bytes
                }
            }

            $individualReadMs = 0.0
            $individualBytes = 0L
            foreach ($resourceUri in $IndividualResourceUris) {
                $individual = Read-McpResource `
                    -HttpClient $httpClient `
                    -Endpoint $Endpoint `
                    -ResourceUri $resourceUri `
                    -BearerToken $BearerToken `
                    -ApiKey $ApiKey `
                    -State $state
                $individualReadMs += $individual.ElapsedMs
                $individualBytes += $individual.Bytes
            }

            $samples.Add([pscustomobject]@{
                iteration = $iteration
                prepareMs = [Math]::Round($prepareMs, 3)
                statusMs = [Math]::Round($statusMs, 3)
                zipReadMs = [Math]::Round($zipReadMs, 3)
                individualReadMs = [Math]::Round($individualReadMs, 3)
                zipBytes = $zipBytes
                individualBytes = $individualBytes
                calls = [int]$state.Calls - $iterationCalls
                packages = $iterationPackageIds.Count
                statuses = @($statuses)
            })
            Write-Host "Iteracao $iteration/$Repetitions concluida: $($iterationPackageIds.Count) pacote(s), $([int]$state.Calls - $iterationCalls) chamada(s)."
        }

        $summary = New-BenchmarkSummary -Samples @($samples) -Endpoint $Endpoint -BearerToken $BearerToken
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            $json = $summary | ConvertTo-Json -Depth 20
            Set-Content -LiteralPath $OutputPath -Value $json -Encoding utf8
            Write-Host "Relatorio salvo em $OutputPath"
        }
        $summary
    }
    finally {
        $httpClient.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($null -eq $Endpoint) {
        throw 'Informe -Endpoint apontando para https://dominio/mcp.'
    }

    Invoke-GradingPackageBenchmark `
        -Endpoint $Endpoint `
        -BatchJobId $BatchJobId `
        -PackageIds $PackageIds `
        -IndividualResourceUris $IndividualResourceUris `
        -BearerToken $BearerToken `
        -ApiKey $ApiKey `
        -Repetitions $Repetitions `
        -PollTimeoutSeconds $PollTimeoutSeconds `
        -PollIntervalMilliseconds $PollIntervalMilliseconds `
        -AllowInsecureHttp:$AllowInsecureHttp `
        -OutputPath $OutputPath |
        ConvertTo-Json -Depth 20
}
