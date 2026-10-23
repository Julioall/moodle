Describe 'Measure-GradingPackageTransport' {
    BeforeAll {
        $scriptPath = Join-Path $PSScriptRoot '..' '..' 'scripts' 'measure-grading-package-transport.ps1'
        . $scriptPath

        Add-Type @"
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

public sealed class BenchmarkPackageTransportTestHandler : HttpMessageHandler
{
    public List<string> Bodies { get; } = new();
    private const string PackageId = "11111111-1111-1111-1111-111111111111";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
        Bodies.Add(body);
        HttpResponseMessage response;
        if (body.Contains("\"method\":\"notifications/initialized\"", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }

        string result;
        if (body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
        {
            result = "{\"capabilities\":{}}";
        }
        else if (body.Contains("\"name\":\"prepare_grading_packages\"", StringComparison.Ordinal))
        {
            result = "{\"structuredContent\":{\"data\":{\"packages\":[{\"packageId\":\"" + PackageId + "\"}]}}}";
        }
        else if (body.Contains("\"name\":\"get_grading_package_status\"", StringComparison.Ordinal))
        {
            result = "{\"structuredContent\":{\"data\":{\"descriptor\":{\"status\":\"Ready\",\"downloadResourceUri\":\"moodle://grading-package/" + PackageId + "\"}}}}";
        }
        else
        {
            result = "{\"contents\":[{\"blob\":\"AQID\"}]}";
        }

        response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"jsonrpc\":\"2.0\",\"id\":\"test\",\"result\":" + result + "}\n\n")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        if (body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
        {
            response.Headers.Add("Mcp-Session-Id", "benchmark-test-session");
        }
        return Task.FromResult(response);
    }
}
"@
    }

    It 'calcula percentis por nearest rank' {
        $result = Get-BenchmarkPercentiles -Values @(10, 20, 30, 40)

        $result.P50 | Should Be 20
        $result.P95 | Should Be 40
    }

    It 'resume amostras sem transportar segredos' {
        $summary = New-BenchmarkSummary -Samples @(
            [pscustomobject]@{ prepareMs = 10; statusMs = 20; zipReadMs = 30; zipBytes = 100; calls = 3 },
            [pscustomobject]@{ prepareMs = 20; statusMs = 30; zipReadMs = 40; zipBytes = 120; calls = 3 }
        ) -Endpoint ([Uri]'https://example.test/mcp') -BearerToken 'token-that-must-not-appear'

        $summary.repetitions | Should Be 2
        $summary.metrics.prepareMs.p50 | Should Be 10
        $summary.metrics.zipBytes.p95 | Should Be 120
        ($summary | ConvertTo-Json -Depth 10) | Should Not Match 'token-that-must-not-appear'
    }

    It 'extrai packageIds dos descritores retornados pela tool' {
        $packageId = [Guid]::NewGuid()
        $toolCall = [pscustomobject]@{
            Body = [pscustomobject]@{
                result = [pscustomobject]@{
                    structuredContent = [pscustomobject]@{
                        data = [pscustomobject]@{
                            packages = @(
                                [pscustomobject]@{ packageId = $packageId.ToString('D') }
                            )
                        }
                    }
                }
            }
        }

        @(Get-PackageIdsFromPreparation -ToolCall $toolCall) | Should Be $packageId.ToString('D')
    }

    It 'executa handshake, prepare, status e leitura do ZIP sem id na notificação' {
        $handler = [BenchmarkPackageTransportTestHandler]::new()
        $httpClient = [System.Net.Http.HttpClient]::new($handler)
        $state = @{ NextRequestId = 1; SessionId = ''; Calls = 0 }
        $endpoint = [Uri]'https://example.test/mcp'

        try {
            Initialize-McpBenchmarkSession -HttpClient $httpClient -Endpoint $endpoint -ApiKey 'test-key' -State $state
            $prepare = Invoke-McpTool -HttpClient $httpClient -Endpoint $endpoint -Name 'prepare_grading_packages' -Arguments @{ batchJobId = ([Guid]::NewGuid()).ToString('D') } -ApiKey 'test-key' -State $state
            $ids = @(Get-PackageIdsFromPreparation -ToolCall $prepare)
            $ready = Wait-ForPackage -HttpClient $httpClient -Endpoint $endpoint -PackageId $ids[0] -ApiKey 'test-key' -State $state -TimeoutSeconds 1 -PollMilliseconds 10
            $resource = Read-McpResource -HttpClient $httpClient -Endpoint $endpoint -ResourceUri 'moodle://grading-package/11111111-1111-1111-1111-111111111111' -ApiKey 'test-key' -State $state

            $ready.Status | Should Be 'Ready'
            $resource.Bytes | Should Be 3
            $handler.Bodies[1] | Should Not Match '"id"'
        }
        finally {
            $httpClient.Dispose()
        }
    }

    It 'rejeita endpoint sem HTTPS sem opt-in explícito' {
        $thrown = $false
        try {
            Assert-BenchmarkEndpoint -Endpoint ([Uri]'http://localhost:8080/mcp') -AllowInsecureHttp:$false
        }
        catch {
            $thrown = $true
        }

        $thrown | Should Be $true
    }
}
