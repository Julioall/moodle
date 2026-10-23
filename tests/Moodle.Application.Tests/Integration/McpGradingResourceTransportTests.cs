using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;
using Moodle.Domain;
using Moodle.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Integration;

public sealed class McpGradingResourceTransportTests : IClassFixture<HighThroughputMcpTestWebApplicationFactory>
{
    private readonly McpTestWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public McpGradingResourceTransportTests(
        HighThroughputMcpTestWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task OfficialMcpClient_CanDiscoverToolsAndReadAuthorizedPackage()
    {
        var httpClient = _factory.CreateClient();
        var apiKey = await RegisterClientAsync(httpClient);
        var seeded = await SeedReadyPackageAsync();

        try
        {
            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["X-Mcp-Api-Key"] = apiKey
                    },
                    ConnectionTimeout = TimeSpan.FromSeconds(5)
                },
                httpClient,
                loggerFactory: null,
                ownsHttpClient: false);

            await using var mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: null,
                cancellationToken: CancellationToken.None);

            var tools = await mcpClient.ListToolsAsync(cancellationToken: CancellationToken.None);
            Assert.Contains(tools, tool => tool.Name == "prepare_grading_packages");
            Assert.Contains(tools, tool => tool.Name == "import_grading_package_results");
            Assert.Contains(tools, tool => tool.Name == "compare_submission_to_selected_model");
            var prepareTool = Assert.Single(tools, tool => tool.Name == "prepare_grading_packages");
            Assert.True(prepareTool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.Equal(
                "boolean",
                properties.GetProperty("regenerateTransport").GetProperty("type").GetString());
            Assert.Equal(
                "boolean",
                properties.GetProperty("waitForCompletion").GetProperty("type").GetString());
            Assert.Equal(
                "integer",
                properties.GetProperty("waitTimeoutSeconds").GetProperty("type").GetString());

            var templates = await mcpClient.ListResourceTemplatesAsync(
                cancellationToken: CancellationToken.None);
            Assert.Contains(
                templates,
                template => template.UriTemplate == "moodle://grading-package/{packageId}");

            var status = await mcpClient.CallToolAsync(
                "get_grading_package_status",
                new Dictionary<string, object?>
                {
                    ["packageId"] = seeded.PackageId.ToString("D")
                },
                cancellationToken: CancellationToken.None);
            Assert.False(status.IsError);
            Assert.Empty(status.Content.OfType<ResourceLinkBlock>());
            var statusStructured = Assert.IsType<JsonElement>(status.StructuredContent);
            Assert.Contains(
                $"moodle://grading-package/{seeded.PackageId:D}",
                statusStructured.GetRawText(),
                StringComparison.Ordinal);

            var resource = await mcpClient.ReadResourceAsync(
                $"moodle://grading-package/{seeded.PackageId:D}",
                cancellationToken: CancellationToken.None);
            var blob = Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents));

            Assert.Equal("application/zip", blob.MimeType);
            Assert.Equal(seeded.ZipBytes, blob.DecodedData.ToArray());
        }
        finally
        {
            await DeleteSeededBlobAsync(seeded.BlobKey);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfficialMcpClient_OnlySignalsExactMatchForExplicitSameActivityResources(bool identical)
    {
        var httpClient = _factory.CreateClient();
        var apiKey = await RegisterClientAsync(httpClient);
        var resources = await SeedModelComparisonResourcesAsync(identical);

        try
        {
            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["X-Mcp-Api-Key"] = apiKey
                    },
                    ConnectionTimeout = TimeSpan.FromSeconds(5)
                },
                httpClient,
                loggerFactory: null,
                ownsHttpClient: false);
            await using var mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: null,
                cancellationToken: CancellationToken.None);

            var response = await mcpClient.CallToolAsync(
                "compare_submission_to_selected_model",
                new Dictionary<string, object?>
                {
                    ["courseId"] = "10",
                    ["assignmentId"] = "501",
                    ["submissionResourceUri"] = resources[0].Resource.ResourceId is { } submissionId
                        ? $"moodle://resource/{submissionId}"
                        : string.Empty,
                    ["modelResourceUri"] = resources[1].Resource.ResourceId is { } modelId
                        ? $"moodle://resource/{modelId}"
                        : string.Empty
                },
                cancellationToken: CancellationToken.None);

            Assert.False(response.IsError);
            var raw = Assert.IsType<JsonElement>(response.StructuredContent).GetRawText();
            using var document = JsonDocument.Parse(raw);
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(identical, data.GetProperty("identical").GetBoolean());
            Assert.Equal(
                identical ? "submission_identical_to_template" : null,
                data.GetProperty("signalCode").ValueKind == JsonValueKind.Null
                    ? null
                    : data.GetProperty("signalCode").GetString());
            Assert.Equal("explicit_request_selection", data.GetProperty("selectionSource").GetString());
            Assert.Equal("sha256_exact", data.GetProperty("comparisonMethod").GetString());
            Assert.Equal(2, data.GetProperty("evidenceReferences").GetArrayLength());
            Assert.DoesNotContain(Encoding.UTF8.GetString(resources[0].Content), raw, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(SHA256.HashData(resources[0].Content)), raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grade", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("plagiarism", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteSeededResourcesAsync(resources);
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public async Task OfficialMcpClient_ReadsOneActivityZipAndCoversEverySyntheticResponse(int itemCount)
    {
        var httpClient = _factory.CreateClient();
        var apiKey = await RegisterClientAsync(httpClient);
        var seeded = await SeedReadyPackageAsync(itemCount);

        try
        {
            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["X-Mcp-Api-Key"] = apiKey
                    },
                    ConnectionTimeout = TimeSpan.FromSeconds(5)
                },
                httpClient,
                loggerFactory: null,
                ownsHttpClient: false);

            await using var mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: null,
                cancellationToken: CancellationToken.None);

            var requests = 0;
            var status = await mcpClient.CallToolAsync(
                "get_grading_package_status",
                new Dictionary<string, object?>
                {
                    ["packageId"] = seeded.PackageId.ToString("D")
                },
                cancellationToken: CancellationToken.None);
            requests++;
            Assert.False(status.IsError);

            var resource = await mcpClient.ReadResourceAsync(
                $"moodle://grading-package/{seeded.PackageId:D}",
                cancellationToken: CancellationToken.None);
            requests++;
            var blob = Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents));
            Assert.Equal("application/zip", blob.MimeType);

            using var zipStream = new MemoryStream(blob.DecodedData.ToArray());
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var responseEntries = archive.Entries
                .Where(entry => entry.FullName.StartsWith("submissions/", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(itemCount, responseEntries.Length);
            var coveredItemIds = new HashSet<int>();
            foreach (var entry in responseEntries)
            {
                var itemId = int.Parse(entry.FullName.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(coveredItemIds.Add(itemId), $"duplicate synthetic item {itemId}");
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                Assert.Equal($"synthetic submission {itemId}", await reader.ReadToEndAsync());
            }

            Assert.Equal(itemCount, coveredItemIds.Count);
            var individualResourceBaselineCalls = 1 + itemCount; // package status + one read per submission
            Assert.Equal(2, requests); // package status + one ZIP resource read
            Assert.True(requests < individualResourceBaselineCalls);
        }
        finally
        {
            await DeleteSeededBlobAsync(seeded.BlobKey);
        }
    }

    [Trait("Category", "PostgresIntegration")]
    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public async Task OfficialMcpClient_PackageTransportP95StaysWithinTenPercentOfIndividualBaseline(int itemCount)
    {
        const int warmupIterations = 3;
        const int measuredIterations = 30;
        await using var isolatedSchema = await CreateIsolatedPostgresSchemaAsync();
        using var postgresFactory = new HighThroughputPostgresMcpTestWebApplicationFactory(
            isolatedSchema.ConnectionString);
        var requestCounter = new HttpRequestCounter();
        using var httpClient = postgresFactory.CreateDefaultClient(new RequestCountingHandler(requestCounter));
        var apiKey = await RegisterClientAsync(httpClient);
        IReadOnlyList<SyntheticResourceSeed> individualResources = [];
        string? packageBlobKey = null;

        try
        {
            var seededPackage = await SeedReadyPackageAsync(itemCount, postgresFactory.Services);
            packageBlobKey = seededPackage.BlobKey;
            individualResources = await SeedSyntheticSubmissionResourcesAsync(itemCount, postgresFactory.Services);
            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["X-Mcp-Api-Key"] = apiKey
                    },
                    ConnectionTimeout = TimeSpan.FromSeconds(5)
                },
                httpClient,
                loggerFactory: null,
                ownsHttpClient: false);

            await using var mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: null,
                cancellationToken: CancellationToken.None);
            requestCounter.Reset();

            async Task<ReadMeasurement> MeasurePackageReadAsync()
            {
                var requestCountAtStart = requestCounter.Count;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var status = await mcpClient.CallToolAsync(
                    "get_grading_package_status",
                    new Dictionary<string, object?>
                    {
                        ["packageId"] = seededPackage.PackageId.ToString("D")
                    },
                    cancellationToken: CancellationToken.None);
                Assert.False(status.IsError);

                var resource = await mcpClient.ReadResourceAsync(
                    $"moodle://grading-package/{seededPackage.PackageId:D}",
                    cancellationToken: CancellationToken.None);
                var blob = Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents));
                using var zipStream = new MemoryStream(blob.DecodedData.ToArray());
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                var entries = archive.Entries
                    .Where(entry => entry.FullName.StartsWith("submissions/", StringComparison.Ordinal))
                    .ToArray();
                var coveredItemIds = new HashSet<int>();
                foreach (var entry in entries)
                {
                    var itemId = int.Parse(
                        entry.FullName.Split('/')[1],
                        System.Globalization.CultureInfo.InvariantCulture);
                    Assert.True(coveredItemIds.Add(itemId), $"duplicate synthetic item {itemId}");
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    Assert.Equal($"synthetic submission {itemId}", await reader.ReadToEndAsync());
                }

                Assert.Equal(itemCount, coveredItemIds.Count);
                stopwatch.Stop();
                return new ReadMeasurement(
                    stopwatch.Elapsed.TotalMilliseconds,
                    requestCounter.Count - requestCountAtStart);
            }

            async Task<ReadMeasurement> MeasureIndividualReadAsync()
            {
                var requestCountAtStart = requestCounter.Count;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var status = await mcpClient.CallToolAsync(
                    "get_grading_package_status",
                    new Dictionary<string, object?>
                    {
                        ["packageId"] = seededPackage.PackageId.ToString("D")
                    },
                    cancellationToken: CancellationToken.None);
                Assert.False(status.IsError);

                var coveredResourceIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var resourceSeed in individualResources)
                {
                    Assert.True(coveredResourceIds.Add(resourceSeed.ResourceId));
                    var resource = await mcpClient.ReadResourceAsync(
                        $"moodle://resource/{resourceSeed.ResourceId}",
                        cancellationToken: CancellationToken.None);
                    var blob = Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents));
                    Assert.Equal(resourceSeed.Content, blob.DecodedData.ToArray());
                }

                Assert.Equal(itemCount, coveredResourceIds.Count);
                stopwatch.Stop();
                return new ReadMeasurement(
                    stopwatch.Elapsed.TotalMilliseconds,
                    requestCounter.Count - requestCountAtStart);
            }

            for (var iteration = 0; iteration < warmupIterations; iteration++)
            {
                _ = await MeasurePackageReadAsync();
                _ = await MeasureIndividualReadAsync();
            }

            var packageSamples = new List<double>(measuredIterations);
            var individualSamples = new List<double>(measuredIterations);
            var packageRequestCounts = new List<int>(measuredIterations);
            var individualRequestCounts = new List<int>(measuredIterations);
            for (var iteration = 0; iteration < measuredIterations; iteration++)
            {
                if (iteration % 2 == 0)
                {
                    var package = await MeasurePackageReadAsync();
                    var individual = await MeasureIndividualReadAsync();
                    packageSamples.Add(package.ElapsedMilliseconds);
                    individualSamples.Add(individual.ElapsedMilliseconds);
                    packageRequestCounts.Add(package.HttpRequests);
                    individualRequestCounts.Add(individual.HttpRequests);
                }
                else
                {
                    var individual = await MeasureIndividualReadAsync();
                    var package = await MeasurePackageReadAsync();
                    individualSamples.Add(individual.ElapsedMilliseconds);
                    packageSamples.Add(package.ElapsedMilliseconds);
                    individualRequestCounts.Add(individual.HttpRequests);
                    packageRequestCounts.Add(package.HttpRequests);
                }
            }

            var packageP95 = CalculateNearestRankPercentile(packageSamples, 0.95);
            var individualP95 = CalculateNearestRankPercentile(individualSamples, 0.95);
            Assert.NotEmpty(packageRequestCounts);
            Assert.NotEmpty(individualRequestCounts);
            Assert.All(packageRequestCounts, count => Assert.Equal(packageRequestCounts[0], count));
            Assert.All(individualRequestCounts, count => Assert.Equal(individualRequestCounts[0], count));
            var packageRequestsPerIteration = packageRequestCounts[0];
            var individualRequestsPerIteration = individualRequestCounts[0];
            Assert.True(packageP95 <= individualP95 * 1.10,
                $"{itemCount} itens: package P95 {packageP95:F2} ms excedeu 110% da base individual {individualP95:F2} ms.");
            Assert.True(packageRequestsPerIteration < individualRequestsPerIteration,
                $"{itemCount} itens: pacote fez {packageRequestsPerIteration} requisições HTTP; base individual fez {individualRequestsPerIteration}.");

            _output.WriteLine(
                "Synthetic local MCP benchmark: items={0}; iterations={1}; package HTTP requests/iteration={2}; individual HTTP requests/iteration={3}; package P95={4:F2} ms; individual P95={5:F2} ms; coverage=100%; duplicateItems=0.",
                itemCount,
                measuredIterations,
                packageRequestsPerIteration,
                individualRequestsPerIteration,
                packageP95,
                individualP95);
        }
        finally
        {
            if (packageBlobKey is not null)
            {
                await DeleteSeededBlobAsync(packageBlobKey, postgresFactory.Services);
            }

            await DeleteSeededResourcesAsync(individualResources, postgresFactory.Services);
        }
    }

    [Trait("Category", "PostgresIntegration")]
    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    public async Task OfficialMcpClient_PreparesSyntheticPackagesAndMeasuresEndToEndP95(int itemCount)
    {
        const int warmupIterations = 3;
        const int measuredIterations = 30;
        const int maxItemsPerPackage = 20;
        // Match the grading skill's documented default polling cadence.
        const int statusPollSeconds = 5;
        await using var isolatedSchema = await CreateIsolatedPostgresSchemaAsync();
        var submissionsGateway = new SyntheticAssignmentSubmissionsGateway(itemCount);
        var packageRoot = Path.Combine(
            Path.GetTempPath(),
            "moodle-synthetic-package-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            using (var postgresFactory = new SyntheticPostgresMcpTestWebApplicationFactory(
                       isolatedSchema.ConnectionString,
                       packageRoot,
                       submissionsGateway))
            {
                var requestCounter = new HttpRequestCounter();
                using var httpClient = postgresFactory.CreateDefaultClient(new RequestCountingHandler(requestCounter));
                var apiKey = await RegisterClientAsync(httpClient);
                var individualResources = await SeedSyntheticSubmissionResourcesAsync(
                    itemCount,
                    postgresFactory.Services);

                await using var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                        TransportMode = HttpTransportMode.StreamableHttp,
                        AdditionalHeaders = new Dictionary<string, string>
                        {
                            ["X-Mcp-Api-Key"] = apiKey
                        },
                        ConnectionTimeout = TimeSpan.FromSeconds(5)
                    },
                    httpClient,
                    loggerFactory: null,
                    ownsHttpClient: false);

                await using var mcpClient = await McpClient.CreateAsync(
                    transport,
                    clientOptions: null,
                    loggerFactory: null,
                    cancellationToken: CancellationToken.None);
                requestCounter.Reset();

                var preparationSamples = new List<double>(measuredIterations);
                var packageSamples = new List<double>(measuredIterations);
                var endToEndSamples = new List<double>(measuredIterations);
                var individualSamples = new List<double>(measuredIterations);
                var preparationRequestCounts = new List<int>(measuredIterations);
                var submissionBatchReadCounts = new List<int>(measuredIterations);
                var packageRequestCounts = new List<int>(measuredIterations);
                var individualRequestCounts = new List<int>(measuredIterations);

                async Task<SyntheticPackageRun> PrepareSyntheticPackagesAsync()
                {
                    var seededBatch = await SeedPendingSyntheticBatchAsync(
                        itemCount,
                        postgresFactory.Services);
                    var gatewayCallsBefore = submissionsGateway.BatchReadCalls;
                    var requestCountAtStart = requestCounter.Count;
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var preparation = await mcpClient.CallToolAsync(
                        "prepare_grading_packages",
                        new Dictionary<string, object?>
                        {
                            ["batchJobId"] = seededBatch.BatchId.ToString("D"),
                            ["packageMode"] = "per_activity",
                            ["waitForCompletion"] = false
                        },
                        cancellationToken: CancellationToken.None);
                    Assert.False(preparation.IsError);
                    var preparationBody = Assert.IsType<JsonElement>(preparation.StructuredContent);
                    var packageDescriptors = preparationBody
                        .GetProperty("data")
                        .GetProperty("packages")
                        .EnumerateArray()
                        .ToArray();
                    var expectedPackageCount = (itemCount + maxItemsPerPackage - 1) / maxItemsPerPackage;
                    Assert.Equal(expectedPackageCount, packageDescriptors.Length);

                    var packageIds = packageDescriptors
                        .Select(descriptor => descriptor.GetProperty("packageId").GetGuid())
                        .ToArray();

                    var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
                    var terminalDescriptors = new Dictionary<Guid, JsonElement>();
                    while (terminalDescriptors.Count < packageIds.Length && DateTimeOffset.UtcNow < deadline)
                    {
                        foreach (var packageId in packageIds.Where(id => !terminalDescriptors.ContainsKey(id)))
                        {
                            var status = await mcpClient.CallToolAsync(
                                "get_grading_package_status",
                                new Dictionary<string, object?>
                                {
                                    ["packageId"] = packageId.ToString("D")
                                },
                                cancellationToken: CancellationToken.None);
                            Assert.False(status.IsError);
                            var statusBody = Assert.IsType<JsonElement>(status.StructuredContent);
                            var descriptor = statusBody.GetProperty("data").GetProperty("descriptor");
                            var state = descriptor.GetProperty("status").GetString();
                            if (state is "Ready" or "PartiallyReady")
                            {
                                terminalDescriptors[packageId] = descriptor.Clone();
                            }
                            else if (state is "NeedsAttention" or "Cancelled" or "ResultsReceived")
                            {
                                Assert.Fail($"O pacote sintético terminou em {state}: {descriptor.GetRawText()}");
                            }
                        }

                        if (terminalDescriptors.Count < packageIds.Length)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(statusPollSeconds));
                        }
                    }

                    Assert.True(
                        packageIds.Length == terminalDescriptors.Count,
                        $"Pacotes sintéticos não chegaram a estado terminal em 60 s; concluídos={terminalDescriptors.Count}/{packageIds.Length}.");
                    foreach (var (packageId, descriptor) in terminalDescriptors)
                    {
                        if (descriptor.GetProperty("pendingItems").GetInt32() != 0)
                        {
                            var resource = await mcpClient.ReadResourceAsync(
                                $"moodle://grading-package/{packageId:D}",
                                cancellationToken: CancellationToken.None);
                            var blob = Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents));
                            using var zipStream = new MemoryStream(blob.DecodedData.ToArray());
                            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                            var manifestEntry = archive.GetEntry("manifest.json");
                            Assert.NotNull(manifestEntry);
                            await using var manifestStream = manifestEntry!.Open();
                            using var manifestDocument = await JsonDocument.ParseAsync(manifestStream);
                            var issues = manifestDocument.RootElement
                                .GetProperty("coverage")
                                .GetProperty("issues")
                                .EnumerateArray()
                                .Select(issue => issue.GetProperty("code").GetString())
                                .Distinct()
                                .ToArray();
                            Assert.Fail(
                                $"O pacote manteve itens pendentes; descriptor={descriptor.GetRawText()}; " +
                                $"issues={string.Join(',', issues)}; " +
                                $"submissionBatchReads={submissionsGateway.BatchReadCalls - gatewayCallsBefore}.");
                        }
                    }
                    Assert.Equal(
                        itemCount,
                        terminalDescriptors.Values.Sum(descriptor => descriptor.GetProperty("includedItems").GetInt32()));

                    stopwatch.Stop();
                    var preparationRequests = requestCounter.Count - requestCountAtStart;
                    Assert.True(preparationRequests >= 1);
                    var submissionBatchReads = submissionsGateway.BatchReadCalls - gatewayCallsBefore;
                    Assert.True(submissionBatchReads >= 1,
                        "A gateway sintética de submissões precisa ser exercitada durante a preparação.");

                    return new SyntheticPackageRun(
                        seededBatch.ItemIds,
                        packageIds,
                        stopwatch.Elapsed.TotalMilliseconds,
                        preparationRequests,
                        submissionBatchReads);
                }

                async Task<ReadMeasurement> ReadPreparedPackagesAsync(SyntheticPackageRun run)
                {
                    var requestCountAtStart = requestCounter.Count;
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var coveredItemIds = new HashSet<Guid>();
                    foreach (var packageId in run.PackageIds)
                    {
                        var status = await mcpClient.CallToolAsync(
                            "get_grading_package_status",
                            new Dictionary<string, object?>
                            {
                                ["packageId"] = packageId.ToString("D")
                            },
                            cancellationToken: CancellationToken.None);
                        Assert.False(status.IsError);
                        var statusBody = Assert.IsType<JsonElement>(status.StructuredContent);
                        var descriptor = statusBody.GetProperty("data").GetProperty("descriptor");
                        Assert.Contains(
                            descriptor.GetProperty("status").GetString(),
                            new[] { "Ready", "PartiallyReady" });

                        var resource = await mcpClient.ReadResourceAsync(
                            $"moodle://grading-package/{packageId:D}",
                            cancellationToken: CancellationToken.None);
                        var blob = Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents));
                        using var zipStream = new MemoryStream(blob.DecodedData.ToArray());
                        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                        var manifestEntry = archive.GetEntry("manifest.json");
                        Assert.NotNull(manifestEntry);
                        await using var manifestStream = manifestEntry!.Open();
                        using var manifestDocument = await JsonDocument.ParseAsync(manifestStream);
                        var manifest = manifestDocument.RootElement;
                        var coverage = manifest.GetProperty("coverage");
                        Assert.Equal(
                            manifest.GetProperty("items").GetArrayLength(),
                            coverage.GetProperty("includedItems").GetInt32());
                        Assert.Equal(0, coverage.GetProperty("pendingItems").GetInt32());
                        foreach (var manifestItem in manifest.GetProperty("items").EnumerateArray())
                        {
                            Assert.True(
                                manifestItem.GetProperty("submissionEvidenceComplete").GetBoolean());
                            Assert.True(coveredItemIds.Add(
                                manifestItem.GetProperty("gradingItemId").GetGuid()),
                                "A synthetic grading item appeared in more than one package.");
                        }
                    }

                    stopwatch.Stop();
                    Assert.Equal(run.ItemIds.Order(), coveredItemIds.Order());
                    var requests = requestCounter.Count - requestCountAtStart;
                    Assert.Equal(run.PackageIds.Length * 2, requests);
                    return new ReadMeasurement(stopwatch.Elapsed.TotalMilliseconds, requests);
                }

                async Task<ReadMeasurement> ReadIndividualResourcesAsync(SyntheticPackageRun run)
                {
                    var requestCountAtStart = requestCounter.Count;
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var status = await mcpClient.CallToolAsync(
                        "get_grading_package_status",
                        new Dictionary<string, object?>
                        {
                            ["packageId"] = run.PackageIds[0].ToString("D")
                        },
                        cancellationToken: CancellationToken.None);
                    Assert.False(status.IsError);

                    var coveredResourceIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var resourceSeed in individualResources)
                    {
                        Assert.True(coveredResourceIds.Add(resourceSeed.ResourceId));
                        var resource = await mcpClient.ReadResourceAsync(
                            $"moodle://resource/{resourceSeed.ResourceId}",
                            cancellationToken: CancellationToken.None);
                        var blob = Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents));
                        Assert.Equal(resourceSeed.Content, blob.DecodedData.ToArray());
                    }

                    stopwatch.Stop();
                    Assert.Equal(itemCount, coveredResourceIds.Count);
                    var requests = requestCounter.Count - requestCountAtStart;
                    Assert.Equal(1 + itemCount, requests);
                    return new ReadMeasurement(stopwatch.Elapsed.TotalMilliseconds, requests);
                }

                for (var iteration = 0; iteration < warmupIterations; iteration++)
                {
                    var run = await PrepareSyntheticPackagesAsync();
                    _ = await ReadPreparedPackagesAsync(run);
                    _ = await ReadIndividualResourcesAsync(run);
                }

                for (var iteration = 0; iteration < measuredIterations; iteration++)
                {
                    var run = await PrepareSyntheticPackagesAsync();
                    ReadMeasurement package;
                    ReadMeasurement individual;
                    if (iteration % 2 == 0)
                    {
                        package = await ReadPreparedPackagesAsync(run);
                        individual = await ReadIndividualResourcesAsync(run);
                    }
                    else
                    {
                        individual = await ReadIndividualResourcesAsync(run);
                        package = await ReadPreparedPackagesAsync(run);
                    }

                    preparationSamples.Add(run.PreparationMilliseconds);
                    packageSamples.Add(package.ElapsedMilliseconds);
                    endToEndSamples.Add(run.PreparationMilliseconds + package.ElapsedMilliseconds);
                    individualSamples.Add(individual.ElapsedMilliseconds);
                    preparationRequestCounts.Add(run.PreparationHttpRequests);
                    submissionBatchReadCounts.Add(run.SubmissionBatchReads);
                    packageRequestCounts.Add(package.HttpRequests);
                    individualRequestCounts.Add(individual.HttpRequests);
                }

                var preparationP95 = CalculateNearestRankPercentile(preparationSamples, 0.95);
                var packageP95 = CalculateNearestRankPercentile(packageSamples, 0.95);
                var endToEndP95 = CalculateNearestRankPercentile(endToEndSamples, 0.95);
                var individualP95 = CalculateNearestRankPercentile(individualSamples, 0.95);
                var totalPackageRequests = preparationRequestCounts[0] + packageRequestCounts[0];
                var individualRequests = individualRequestCounts[0];
                Assert.All(preparationRequestCounts, count => Assert.True(count >= 1));
                Assert.All(packageRequestCounts, count => Assert.Equal(packageRequestCounts[0], count));
                Assert.All(individualRequestCounts, count => Assert.Equal(individualRequests, count));
                Assert.True(packageP95 <= individualP95 * 1.10,
                    $"{itemCount} itens: recuperação ZIP P95 {packageP95:F2} ms excedeu 110% da base individual {individualP95:F2} ms.");
                Assert.All(Enumerable.Range(0, measuredIterations), iteration =>
                    Assert.True(
                        preparationRequestCounts[iteration] + packageRequestCounts[iteration] < individualRequestCounts[iteration],
                        $"{itemCount} itens: preparação + ZIPs fizeram {preparationRequestCounts[iteration] + packageRequestCounts[iteration]} requisições MCP; a base individual fez {individualRequestCounts[iteration]}."));

                _output.WriteLine(
                    "Synthetic local MCP end-to-end: items={0}; packages={1}; warmups={2}; iterations={3}; submission batch reads/run={4}; preparation+status HTTP requests={5}; package transport HTTP requests/iteration={6}; package total HTTP requests={7}; individual baseline HTTP requests={8}; preparation P95={9:F2} ms; package transport P95={10:F2} ms; package end-to-end P95={11:F2} ms; individual baseline P95={12:F2} ms; coverage=100%; duplicateItems=0.",
                    itemCount,
                    (itemCount + maxItemsPerPackage - 1) / maxItemsPerPackage,
                    warmupIterations,
                    measuredIterations,
                    submissionBatchReadCounts[0],
                    preparationRequestCounts[0],
                    packageRequestCounts[0],
                    totalPackageRequests,
                    individualRequests,
                    preparationP95,
                    packageP95,
                    endToEndP95,
                    individualP95);
            }
        }
        finally
        {
            DeleteSyntheticPackageRoot(packageRoot);
        }
    }

    [Fact]
    public async Task OfficialMcpClient_CanUseEmbeddedFallbackForOpaqueResources()
    {
        var httpClient = _factory.CreateClient();
        var apiKey = await RegisterClientAsync(httpClient);
        var seededPackage = await SeedReadyPackageAsync();
        var resourceId = Guid.NewGuid().ToString("N");
        var resourceBytes = Encoding.UTF8.GetBytes("resposta individual do aluno");
        var resourceHash = Convert.ToHexString(SHA256.HashData(resourceBytes)).ToLowerInvariant();

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            dbContext.MoodleResources.Add(new MoodleResource
            {
                ResourceId = resourceId,
                ClientId = "resource-test-client",
                ConnectionId = "resource-test-client:default",
                MoodleAlias = "default",
                OwnerSubject = "resource-test-client",
                ResourceType = "submission_attachment",
                CourseId = 10,
                AssignmentId = 501,
                SubmissionId = 456,
                StudentId = 123,
                Filename = "answer.txt",
                MimeType = "text/plain",
                SizeBytes = resourceBytes.LongLength,
                Sha256 = resourceHash,
                RemoteFileReference = "inline://answer.txt",
                InlineContent = resourceBytes,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            });
            await dbContext.SaveChangesAsync();
        }

        try
        {
            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["X-Mcp-Api-Key"] = apiKey
                    },
                    ConnectionTimeout = TimeSpan.FromSeconds(5)
                },
                httpClient,
                loggerFactory: null,
                ownsHttpClient: false);

            await using var mcpClient = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: null,
                cancellationToken: CancellationToken.None);

            var tools = await mcpClient.ListToolsAsync(cancellationToken: CancellationToken.None);
            Assert.Contains(tools, tool => tool.Name == "read_moodle_resource");
            Assert.Contains(tools, tool => tool.Name == "read_grading_package");

            var resourceResult = await mcpClient.CallToolAsync(
                "read_moodle_resource",
                new Dictionary<string, object?>
                {
                    ["resourceUri"] = $"moodle://resource/{resourceId}"
                },
                cancellationToken: CancellationToken.None);
            Assert.False(
                resourceResult.IsError,
                resourceResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            var resourceBlob = Assert.IsType<BlobResourceContents>(
                Assert.Single(resourceResult.Content.OfType<EmbeddedResourceBlock>()).Resource);
            Assert.Equal(resourceBytes, resourceBlob.DecodedData.ToArray());

            var packageResult = await mcpClient.CallToolAsync(
                "read_grading_package",
                new Dictionary<string, object?>
                {
                    ["resourceUri"] = $"moodle://grading-package/{seededPackage.PackageId:D}"
                },
                cancellationToken: CancellationToken.None);
            Assert.False(packageResult.IsError);
            var packageBlob = Assert.IsType<BlobResourceContents>(
                Assert.Single(packageResult.Content.OfType<EmbeddedResourceBlock>()).Resource);
            Assert.Equal(seededPackage.ZipBytes, packageBlob.DecodedData.ToArray());
        }
        finally
        {
            await DeleteSeededBlobAsync(seededPackage.BlobKey);
            using var scope = _factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var resource = await dbContext.MoodleResources.SingleOrDefaultAsync(item => item.ResourceId == resourceId);
            if (resource is not null)
            {
                dbContext.MoodleResources.Remove(resource);
                await dbContext.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task ResourcesList_DoesNotExposeTheReviewAppUri()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", await RegisterClientAsync(client));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var initialize = await SendAsync(client, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "resource-test", ["version"] = "1.0" }
        }, "resource-init");
        var sessionId = initialize.SessionId;

        await SendAsync(client, "notifications/initialized", new JsonObject(), null, sessionId);

        var listed = await SendAsync(client, "resources/list", new JsonObject(), "resource-list", sessionId);
        var resources = listed.Body?["result"]?["resources"]?.AsArray();
        Assert.NotNull(resources);
        Assert.DoesNotContain(resources!, resource =>
            resource?["uri"]?.GetValue<string>() == "ui://grading-review/v2/app.html");
        Assert.DoesNotContain(resources!, resource =>
            resource?["uri"]?.GetValue<string>() == "ui://grading-review/app.html");

        var templates = await SendAsync(client, "resources/templates/list", new JsonObject(), "resource-templates", sessionId);
        var resourceTemplates = templates.Body?["result"]?["resourceTemplates"]?.AsArray();
        Assert.NotNull(resourceTemplates);
        Assert.Contains(resourceTemplates!, template =>
            template?["uriTemplate"]?.GetValue<string>() == "moodle://resource/{resourceId}");
        Assert.Contains(resourceTemplates!, template =>
            template?["uriTemplate"]?.GetValue<string>() == "moodle://grading-package/{packageId}");

        var tools = await SendAsync(client, "tools/list", new JsonObject(), "tool-list", sessionId);
        var toolEntries = tools.Body?["result"]?["tools"]?.AsArray();
        Assert.NotNull(toolEntries);
        Assert.Contains(toolEntries!, tool =>
            tool?["name"]?.GetValue<string>() == "prepare_grading_packages");
        Assert.Contains(toolEntries!, tool =>
            tool?["name"]?.GetValue<string>() == "import_grading_package_results");

    }

    [Fact]
    public async Task ResourceTemplatesList_AllowsUnauthenticatedDiscoveryContract()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var initialize = await SendAsync(client, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "anonymous-discovery-test", ["version"] = "1.0" }
        }, "anonymous-discovery-init");

        await SendAsync(client, "notifications/initialized", new JsonObject(), null, initialize.SessionId);
        var templates = await SendAsync(
            client,
            "resources/templates/list",
            new JsonObject(),
            "anonymous-resource-templates",
            initialize.SessionId);

        var resourceTemplates = templates.Body?["result"]?["resourceTemplates"]?.AsArray();
        Assert.NotNull(resourceTemplates);
        Assert.Contains(resourceTemplates!, template =>
            template?["uriTemplate"]?.GetValue<string>() == "moodle://grading-package/{packageId}");
    }

    [Fact]
    public async Task ResourcesRead_ReturnsTheAuthorizedPackageZipThroughMcpProtocol()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", await RegisterClientAsync(client));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var seeded = await SeedReadyPackageAsync();
        try
        {
            var initialize = await SendAsync(client, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "resource-roundtrip-test", ["version"] = "1.0" }
            }, "resource-roundtrip-init");
            var sessionId = initialize.SessionId;

            await SendAsync(client, "notifications/initialized", new JsonObject(), null, sessionId);
            var response = await SendAsync(
                client,
                "resources/read",
                new JsonObject
                {
                    ["uri"] = $"moodle://grading-package/{seeded.PackageId:D}"
                },
                "resource-roundtrip-read",
                sessionId);

            var contents = response.Body?[
                "result"]?["contents"]?.AsArray();
            Assert.NotNull(contents);
            var blob = Assert.Single(contents!);
            Assert.Equal("application/zip", blob?["mimeType"]?.GetValue<string>());
            var encoded = blob?["blob"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(encoded));
            Assert.Equal(seeded.ZipBytes, Convert.FromBase64String(encoded!));
        }
        finally
        {
            await DeleteSeededBlobAsync(seeded.BlobKey);
        }
    }

    [Fact]
    public async Task ProtocolRoundtrip_ImportsPackageResultsAsDraftWithoutPublishing()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Mcp-Api-Key", await RegisterClientAsync(client));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var seeded = await SeedImportablePackageAsync();
        try
        {
            var initialize = await SendAsync(client, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "package-import-roundtrip", ["version"] = "1.0" }
            }, "package-import-init");
            var sessionId = initialize.SessionId;

            await SendAsync(client, "notifications/initialized", new JsonObject(), null, sessionId);
            var resultDocument = new GradingPackageResultDocument(
                GradingPackageContract.ResultsSchemaVersion,
                seeded.PackageId,
                seeded.ManifestHash,
                [new GradingPackageResultItem(
                    seeded.ItemId,
                    seeded.ContextHash,
                    seeded.SubmissionContentHash,
                    "graded",
                    8m,
                    "Feedback baseado na evidencia da entrega.",
                    [new GradingPackageResultEvidence(
                        seeded.ArtifactId,
                        "A resposta apresenta a estrategia solicitada.",
                        "entregas/123/answer.txt")],
                    [])]);

            var response = await SendAsync(
                client,
                "tools/call",
                new JsonObject
                {
                    ["name"] = "import_grading_package_results",
                    ["arguments"] = new JsonObject
                    {
                        ["packageId"] = seeded.PackageId.ToString("D"),
                        ["manifestHash"] = seeded.ManifestHash,
                        ["resultsJson"] = JsonSerializer.Serialize(
                            resultDocument,
                            GradingPackageContract.JsonOptions)
                    }
                },
                "package-import-call",
                sessionId);

            Assert.NotNull(response.Body);
            Assert.Null(response.Body!["error"]);
            Assert.False(
                response.Body["result"]?["isError"]?.GetValue<bool>() ?? false,
                response.Body.ToJsonString());

            using var scope = _factory.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IGradingReviewRepository>();
            var packageStore = scope.ServiceProvider.GetRequiredService<IGradingPackageStore>();
            var item = await repository.GetItemAsync(seeded.ItemId, CancellationToken.None);
            var package = await packageStore.GetAsync(seeded.PackageId, CancellationToken.None);

            Assert.NotNull(item);
            Assert.True(
                item!.Status == GradingItemStatus.DraftReady,
                response.Body.ToJsonString());
            Assert.Equal(8m, item.SuggestedGrade);
            Assert.Equal(seeded.SubmissionContentHash, item.SubmissionContentHash);
            Assert.NotNull(package);
            Assert.Equal(GradingEvidencePackageStatus.ResultsReceived, package!.Status);
        }
        finally
        {
            await DeleteSeededBlobAsync(seeded.BlobKey);
        }
    }

    private async Task<(Guid PackageId, string BlobKey, byte[] ZipBytes)> SeedReadyPackageAsync(
        int itemCount = 1,
        IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var packageStore = scope.ServiceProvider.GetRequiredService<IGradingPackageStore>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IGradingPackageBlobStore>();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "resource-test-client",
            321,
            itemCount,
            connectorClientId: "resource-test-client",
            connectionAlias: "default",
            moodleConnectionId: "connection-a");
        var items = Enumerable.Range(1, itemCount)
            .Select(index => AssistedGradingItem.Create(
                batch.Id,
                10,
                501,
                2000 + index,
                1000 + index,
                1,
                $"synthetic-student-{index}"))
            .ToArray();
        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.AddRange(items);
        await dbContext.SaveChangesAsync();
        var itemIds = items.Select(item => item.Id).ToArray();
        var batchId = batch.Id;
        var identity = GradingPackageIdentity.Create(
            "resource-test-client",
            "connection-a",
            "resource-test-client",
            "default",
            10,
            501,
            "1001",
            itemIds.Select((itemId, index) => new GradingPackageItemIdentity(
                itemId,
                $"context-{index + 1}",
                $"submission-{index + 1}")).ToArray());
        var package = await packageStore.GetOrCreateAsync(identity, batchId, CancellationToken.None);
        var manifest = new GradingPackageManifest(
            GradingPackageContract.ManifestSchemaVersion,
            package.Id,
            package.PackageVersion,
            new GradingPackageActivity(10, 501, "1001", "points", 10m, [], [], null),
            itemIds.Select((itemId, index) => new GradingPackageManifestItem(
                itemId,
                batchId,
                null,
                1000 + index,
                $"synthetic-student-{index + 1}",
                2000 + index,
                1,
                null,
                $"context-{index + 1}",
                $"submission-{index + 1}",
                [],
                1,
                true,
                true)).ToArray(),
            new GradingPackageCoverage(itemCount, itemCount, 0, []));
        var manifestJson = GradingPackageManifestHash.Serialize(manifest);
        var now = DateTimeOffset.UtcNow;
        package.MarkManifest(
            manifestJson,
            GradingPackageManifestHash.Compute(manifest),
            now);

        var zipBytes = CreateZipBytes(itemCount);
        var blobKey = $"packages/{package.Id:N}/protocol-test.zip";
        var blob = await blobStore.WriteAsync(
            blobKey,
            async (destination, cancellationToken) =>
            {
                await destination.WriteAsync(zipBytes, cancellationToken);
            },
            CancellationToken.None);
        try
        {
            package.MarkReady(
                blob.BlobKey,
                blob.Sha256,
                blob.SizeBytes,
                blob.SizeBytes,
                itemCount + 1,
                itemCount,
                itemCount,
                0,
                now,
                now.AddMinutes(30));
            await packageStore.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            await blobStore.DeleteAsync(blob.BlobKey, CancellationToken.None);
            throw;
        }

        return (package.Id, blob.BlobKey, zipBytes);
    }

    private async Task<IReadOnlyList<SyntheticResourceSeed>> SeedSyntheticSubmissionResourcesAsync(
        int itemCount,
        IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var seeds = Enumerable.Range(1, itemCount)
            .Select(index =>
            {
                var content = Encoding.UTF8.GetBytes($"synthetic submission {index}");
                var resourceId = Guid.NewGuid().ToString("N");
                var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                return new SyntheticResourceSeed(resourceId, content, new MoodleResource
                {
                    ResourceId = resourceId,
                    ClientId = "resource-test-client",
                    ConnectionId = "resource-test-client:default",
                    MoodleAlias = "default",
                    OwnerSubject = "resource-test-client",
                    ResourceType = "submission_attachment",
                    CourseId = 10,
                    AssignmentId = 501,
                    SubmissionId = 2000 + index,
                    StudentId = 1000 + index,
                    Filename = "response.txt",
                    MimeType = "text/plain",
                    SizeBytes = content.LongLength,
                    Sha256 = hash,
                    RemoteFileReference = "inline://synthetic-response.txt",
                    InlineContent = content,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
                });
            })
            .ToArray();
        dbContext.MoodleResources.AddRange(seeds.Select(seed => seed.Resource));
        await dbContext.SaveChangesAsync();
        return seeds;
    }

    private static async Task<SyntheticBatchSeed> SeedPendingSyntheticBatchAsync(
        int itemCount,
        IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "resource-test-client",
            321,
            itemCount,
            connectorClientId: "resource-test-client",
            connectionAlias: "default",
            moodleConnectionId: "resource-test-client:default");
        var contextSnapshots = new List<GradingContextSnapshot>(itemCount);
        var items = Enumerable.Range(1, itemCount)
            .Select(index =>
            {
                var item = AssistedGradingItem.Create(
                    batch.Id,
                    10,
                    501,
                    2000 + index,
                    1000 + index,
                    1,
                    $"synthetic-student-{index}");
                item.MarkAwaitingAiAnalysis(null);
                var context = GradingContext.Build(
                    item.Id,
                    batch.Id,
                    "10",
                    "501",
                    item.SubmissionId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    item.MoodleUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    assignmentStatement: "Enunciado sintético para validação do transporte.",
                    criteria: "Critério sintético de cobertura.",
                    rubricDescription: null,
                    maxGrade: 10m,
                    gradeScale: "points",
                    submissionText: $"synthetic submission {index}",
                    attachedFiles: [],
                    courseMaterials: null,
                    teacherInstructions: "Validar somente dados sintéticos.",
                    gradingMode: "numeric",
                    confirmedMaximumGrade: 10m,
                    gradeScaleSource: "assignment_settings",
                    gradeScaleVersion: "synthetic-v1");
                var snapshot = GradingContextSnapshotFactory.Create(
                    item,
                    context,
                    new GradingContextOptions());
                item.RecordContextSnapshot(snapshot);
                item.RecordSubmissionIntegrity(new string('c', 64), []);
                contextSnapshots.Add(snapshot);
                return item;
            })
            .ToArray();

        dbContext.GradingBatches.Add(batch);
        dbContext.GradingItems.AddRange(items);
        dbContext.GradingContextSnapshots.AddRange(contextSnapshots.Select(GradingContextSnapshotDocument.FromSnapshot));
        await dbContext.SaveChangesAsync();
        return new SyntheticBatchSeed(batch.Id, items.Select(item => item.Id).ToArray());
    }

    private async Task<IReadOnlyList<SyntheticResourceSeed>> SeedModelComparisonResourcesAsync(bool identical)
    {
        var submissionBytes = Encoding.UTF8.GetBytes("synthetic student response for exact model comparison");
        var modelBytes = identical
            ? submissionBytes.ToArray()
            : Encoding.UTF8.GetBytes("different synthetic model answer");
        var submission = CreateModelComparisonResource(
            "submission_attachment",
            "submission.txt",
            submissionBytes,
            submissionId: 2001,
            studentId: 1001);
        var model = CreateModelComparisonResource(
            "assignment_context_attachment",
            "selected-model.txt",
            modelBytes,
            submissionId: null,
            studentId: null);
        var seeds = new[]
        {
            new SyntheticResourceSeed(submission.ResourceId, submissionBytes, submission),
            new SyntheticResourceSeed(model.ResourceId, modelBytes, model)
        };

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        dbContext.MoodleResources.AddRange(seeds.Select(seed => seed.Resource));
        await dbContext.SaveChangesAsync();
        return seeds;
    }

    private static MoodleResource CreateModelComparisonResource(
        string resourceType,
        string filename,
        byte[] content,
        long? submissionId,
        long? studentId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new MoodleResource
        {
            ResourceId = Guid.NewGuid().ToString("N"),
            ClientId = "resource-test-client",
            ConnectionId = "resource-test-client:default",
            MoodleAlias = "default",
            OwnerSubject = "resource-test-client",
            ResourceType = resourceType,
            CourseId = 10,
            AssignmentId = 501,
            SubmissionId = submissionId,
            StudentId = studentId,
            Filename = filename,
            MimeType = "text/plain",
            SizeBytes = content.LongLength,
            Sha256 = hash,
            RemoteFileReference = $"inline://{filename}",
            InlineContent = content,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };
    }

    private async Task DeleteSeededResourcesAsync(
        IReadOnlyList<SyntheticResourceSeed> seeds,
        IServiceProvider? services = null)
    {
        if (seeds.Count == 0)
        {
            return;
        }

        using var scope = (services ?? _factory.Services).CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var resourceIds = seeds.Select(seed => seed.ResourceId).ToHashSet(StringComparer.Ordinal);
        var resources = await dbContext.MoodleResources
            .Where(resource => resourceIds.Contains(resource.ResourceId))
            .ToArrayAsync();
        dbContext.MoodleResources.RemoveRange(resources);
        await dbContext.SaveChangesAsync();
    }

    private static double CalculateNearestRankPercentile(IReadOnlyList<double> samples, double percentile)
    {
        if (samples.Count == 0 || percentile is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        var sorted = samples.Order().ToArray();
        var rank = (int)Math.Ceiling(sorted.Length * percentile);
        return sorted[Math.Max(0, rank - 1)];
    }

    private static void DeleteSyntheticPackageRoot(string packageRoot)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "moodle-synthetic-package-tests"));
        var target = Path.GetFullPath(packageRoot);
        var parentPrefix = expectedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!target.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(target).Length != 32)
        {
            throw new InvalidOperationException("O diretório temporário sintético não está no destino esperado.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    private async Task<ImportablePackageSeed> SeedImportablePackageAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGradingReviewRepository>();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IGradingContextSnapshotStore>();
        var packageStore = scope.ServiceProvider.GetRequiredService<IGradingPackageStore>();
        var blobStore = scope.ServiceProvider.GetRequiredService<IGradingPackageBlobStore>();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "resource-test-client",
            321,
            1,
            connectorClientId: "resource-test-client",
            connectionAlias: "default",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 456, 123, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "456",
            "123",
            "Resolva a atividade com justificativa.",
            "A resposta deve apresentar uma justificativa coerente.",
            null,
            10m,
            "points",
            "A resposta do aluno apresenta uma estrategia.",
            [],
            null,
            null,
            gradingMode: "numeric",
            confirmedMaximumGrade: 10m,
            gradeScaleSource: "assignment_settings",
            gradeScaleVersion: "scale-v1");
        var snapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        var submissionContentHash = new string('c', 64);
        item.RecordSubmissionIntegrity(submissionContentHash, []);
        var artifactId = Guid.NewGuid();
        var artifactHash = new string('a', 64);

        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                artifactId,
                item.Id,
                "submission_file",
                "answer.txt",
                "text/plain",
                artifactHash,
                12,
                ExtractionStatus.Succeeded,
                "Resposta do aluno.",
                null,
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        await snapshotStore.PublishAsync(snapshot, CancellationToken.None);

        var identity = GradingPackageIdentity.Create(
            "resource-test-client",
            "connection-a",
            "resource-test-client",
            "default",
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(item.Id, snapshot.ContextHash, submissionContentHash)]);
        var package = await packageStore.GetOrCreateAsync(identity, batch.Id, CancellationToken.None);
        var manifest = new GradingPackageManifest(
            GradingPackageContract.ManifestSchemaVersion,
            package.Id,
            package.PackageVersion,
            new GradingPackageActivity(10, 501, "1001", "points", 10m, [], [], null),
            [new GradingPackageManifestItem(
                item.Id,
                batch.Id,
                null,
                item.MoodleUserId,
                item.StudentDisplayName,
                item.SubmissionId,
                item.AttemptNumber,
                null,
                snapshot.ContextHash,
                submissionContentHash,
                [new GradingPackageManifestFile(
                    artifactId,
                    "entregas/123/answer.txt",
                    "answer.txt",
                    "text/plain",
                    artifactHash,
                    12,
                    false)],
                1,
                true)],
            new GradingPackageCoverage(1, 1, 0, []));
        var manifestJson = GradingPackageManifestHash.Serialize(manifest);
        var manifestHash = GradingPackageManifestHash.Compute(manifest);
        var now = DateTimeOffset.UtcNow;
        package.MarkManifest(manifestJson, manifestHash, now);

        var file = GradingEvidencePackageFile.Create(
            package.Id,
            item.Id,
            artifactId,
            "entregas/123/answer.txt",
            "answer.txt",
            "text/plain",
            artifactHash,
            12,
            false);
        file.MarkIncluded(now);
        await packageStore.AddFileAsync(file, CancellationToken.None);

        var zipBytes = CreateZipBytes();
        var blobKey = $"packages/{package.Id:N}/protocol-import-test.zip";
        var blob = await blobStore.WriteAsync(
            blobKey,
            async (destination, cancellationToken) =>
            {
                await destination.WriteAsync(zipBytes, cancellationToken);
            },
            CancellationToken.None);
        package.MarkReady(
            blob.BlobKey,
            blob.Sha256,
            blob.SizeBytes,
            blob.SizeBytes,
            1,
            1,
            1,
            0,
            now,
            now.AddMinutes(5));
        await packageStore.SaveChangesAsync(CancellationToken.None);

        return new ImportablePackageSeed(
            package.Id,
            blob.BlobKey,
            manifestHash,
            item.Id,
            artifactId,
            snapshot.ContextHash,
            submissionContentHash);
    }

    private sealed record ImportablePackageSeed(
        Guid PackageId,
        string BlobKey,
        string ManifestHash,
        Guid ItemId,
        Guid ArtifactId,
        string ContextHash,
        string SubmissionContentHash);

    private sealed record SyntheticResourceSeed(
        string ResourceId,
        byte[] Content,
        MoodleResource Resource);

    private sealed record ReadMeasurement(double ElapsedMilliseconds, int HttpRequests);

    private sealed record SyntheticBatchSeed(Guid BatchId, IReadOnlyList<Guid> ItemIds);

    private sealed record SyntheticPackageRun(
        IReadOnlyList<Guid> ItemIds,
        Guid[] PackageIds,
        double PreparationMilliseconds,
        int PreparationHttpRequests,
        int SubmissionBatchReads);

    private sealed class HttpRequestCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);
    }

    private sealed class RequestCountingHandler(HttpRequestCounter counter) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            counter.Increment();
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class SyntheticPostgresMcpTestWebApplicationFactory : McpTestWebApplicationFactory
    {
        private readonly string _connectionString;
        private readonly string _packageRoot;
        private readonly IMoodleAssignmentSubmissionsGateway _submissionsGateway;

        public SyntheticPostgresMcpTestWebApplicationFactory(
            string connectionString,
            string packageRoot,
            IMoodleAssignmentSubmissionsGateway submissionsGateway)
        {
            _connectionString = string.IsNullOrWhiteSpace(connectionString)
                ? throw new ArgumentException("A connection string de teste PostgreSQL é obrigatória.", nameof(connectionString))
                : connectionString;
            _packageRoot = string.IsNullOrWhiteSpace(packageRoot)
                ? throw new ArgumentException("A raiz temporária dos pacotes sintéticos é obrigatória.", nameof(packageRoot))
                : packageRoot;
            _submissionsGateway = submissionsGateway;
        }

        protected override int McpPermitLimitForTests => 10_000;

        protected override string? PostgresConnectionStringForTests => _connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.AddFilter((_, level) => level >= LogLevel.Warning);
            });
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GradingLimits:MaxGradingPackageItems"] = "20",
                    ["GradingLimits:GradingPackageBlobRootDirectory"] = _packageRoot
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMoodleAssignmentSubmissionsGateway>();
                services.AddSingleton(_submissionsGateway);
            });
        }
    }

    private sealed class SyntheticAssignmentSubmissionsGateway : IMoodleAssignmentSubmissionsGateway
    {
        private readonly IReadOnlyList<AssignmentSubmissionRecord> _records;
        private int _batchReadCalls;

        public SyntheticAssignmentSubmissionsGateway(int itemCount)
        {
            var modifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            _records = Enumerable.Range(1, itemCount)
                .Select(index => new AssignmentSubmissionRecord(
                    (2000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (1000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "submitted",
                    "notgraded",
                    modifiedAt.AddMinutes(-5),
                    modifiedAt,
                    1,
                    0,
                    true,
                    Files: [],
                    OnlineText: $"synthetic submission {index}"))
                .ToArray();
        }

        public int BatchReadCalls => Volatile.Read(ref _batchReadCalls);

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _batchReadCalls);
            return Task.FromResult<IReadOnlyList<AssignmentSubmissionsBatch>>(
                assignmentIds
                    .Select(assignmentId => new AssignmentSubmissionsBatch(assignmentId, _records))
                    .ToArray());
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("O teste sintético espera a leitura em lote de submissões.");
    }

    private async Task DeleteSeededBlobAsync(string blobKey, IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var blobStore = scope.ServiceProvider.GetRequiredService<IGradingPackageBlobStore>();
        await blobStore.DeleteAsync(blobKey, CancellationToken.None);
    }

    private static async Task<IsolatedPostgresSchema> CreateIsolatedPostgresSchemaAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(baseConnectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION é obrigatória para o benchmark PostgreSQL.");

        var schemaName = $"mcp_p95_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(baseConnectionString);
        await adminConnection.OpenAsync();
        await using (var createSchema = adminConnection.CreateCommand())
        {
            createSchema.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await createSchema.ExecuteNonQueryAsync();
        }

        var scopedConnectionStringBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schemaName
        };
        var scopedConnectionString = scopedConnectionStringBuilder.ConnectionString;
        try
        {
            var options = new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            await using var schemaDb = new ConnectorDbContext(options);
            await schemaDb.ApplyVersionedSchemaAsync();
        }
        catch
        {
            await using var dropSchema = adminConnection.CreateCommand();
            dropSchema.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await dropSchema.ExecuteNonQueryAsync();
            throw;
        }

        return new IsolatedPostgresSchema(baseConnectionString, schemaName, scopedConnectionString);
    }

    private sealed class IsolatedPostgresSchema(
        string baseConnectionString,
        string schemaName,
        string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            await using (var scopedConnection = new NpgsqlConnection(ConnectionString))
            {
                NpgsqlConnection.ClearPool(scopedConnection);
            }

            await using var connection = new NpgsqlConnection(baseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static byte[] CreateZipBytes(int submissionCount = 0)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = archive.CreateEntry("manifest.json").Open())
            {
                var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
                entry.Write(bytes, 0, bytes.Length);
            }

            for (var itemId = 1; itemId <= submissionCount; itemId++)
            {
                using var entry = archive.CreateEntry($"submissions/{itemId}/response.txt").Open();
                var bytes = Encoding.UTF8.GetBytes($"synthetic submission {itemId}");
                entry.Write(bytes, 0, bytes.Length);
            }
        }

        return output.ToArray();
    }

    private static async Task<(JsonNode? Body, string? SessionId)> SendAsync(
        HttpClient client,
        string method,
        JsonNode @params,
        string? id,
        string? sessionId = null)
    {
        var requestBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params
        };
        if (id is not null)
        {
            requestBody["id"] = id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var session = response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues)
            ? sessionValues.FirstOrDefault()
            : null;
        return (ParseResponse(body), session);
    }

    private static JsonNode? ParseResponse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            var data = body
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line["data:".Length..].Trim())
                .FirstOrDefault(line => line.Length > 0);
            return data is null ? null : JsonNode.Parse(data);
        }
    }

    private async Task<string> RegisterClientAsync(HttpClient client)
    {
        var payload = """
        {
          "clientId": "resource-test-client",
          "moodleAlias": "default",
          "moodleBaseUrl": "https://moodle.tests",
          "moodleUsername": "usuario.teste",
          "moodlePassword": "senha.teste",
          "moodleTarget": "default",
          "isDefault": true,
          "canWrite": true
        }
        """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/connector-clients/register")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Admin-Api-Key", "admin-tests-key");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body);
        return json?["apiKey"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Test registration did not return an API key.");
    }
}
