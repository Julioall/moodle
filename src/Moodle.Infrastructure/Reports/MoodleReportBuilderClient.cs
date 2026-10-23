using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure.Reports;

internal sealed partial class MoodleReportBuilderGateway(
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient) : IMoodleReportBuilderGateway
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FilterLocks = new();
    public async Task<IReadOnlyList<MoodleReportInfo>> ListReportsAsync(CancellationToken cancellationToken)
    {
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);

        var response = await restClient.CallAsync(
            credentials,
            "core_reportbuilder_list_reports",
            new Dictionary<string, object?>(),
            cancellationToken);

        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("reports", out var reportsElement) && reportsElement.ValueKind == JsonValueKind.Array)
        {
            var reports = new List<MoodleReportInfo>();
            foreach (var element in reportsElement.EnumerateArray())
            {
                if (element.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
                {
                    var name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    var source = element.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() ?? "" : "";
                    reports.Add(new MoodleReportInfo(id, name, source));
                }
            }
            return reports;
        }

        return [];
    }

    public async Task<MoodleReportResult> DownloadAsync(
        int reportId,
        int pageSize,
        IDictionary<string, object?>? filters,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(pageSize, 1, 50_000);
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var hasFilters = filters != null && filters.Count > 0;
        var lockKey = $"{credentials.BaseUrl}_{credentials.Username}_{reportId}";
        SemaphoreSlim? semaphore = null;

        if (hasFilters)
        {
            semaphore = FilterLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await restClient.CallAsync(
                    credentials,
                    "core_reportbuilder_set_filters",
                    new Dictionary<string, object?>
                    {
                        ["reportid"] = reportId,
                        ["parameters"] = "{}",
                        ["values"] = JsonSerializer.Serialize(filters)
                    },
                    cancellationToken);
            }
            catch
            {
                semaphore.Release();
                throw;
            }
        }

        try
        {
            var allRows = new List<Dictionary<string, object?>>();
            var saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            int totalAvailable = 0;
            int page = 0;
            int perPage = Math.Min(limit, 500);

            while (true)
            {
                var wsParameters = new Dictionary<string, object?>
                {
                    ["reportid"] = reportId,
                    ["page"] = page,
                    ["perpage"] = perPage
                };

                var jsonResponse = await restClient.CallAsync(
                    credentials,
                    "core_reportbuilder_retrieve_report",
                    wsParameters,
                    cancellationToken);

                var (rows, total) = ParseWsReportData(jsonResponse);
                totalAvailable = total;
                allRows.AddRange(rows);

                if (allRows.Count >= limit || allRows.Count >= totalAvailable || rows.Count == 0)
                    break;

                page++;
            }

            var isTruncated = allRows.Count < totalAvailable;
            if (allRows.Count > limit) allRows = allRows.Take(limit).ToList();

            return new MoodleReportResult(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, saoPaulo),
                allRows,
                totalAvailable,
                isTruncated);
        }
        finally
        {
            if (semaphore != null)
            {
                try
                {
                    await restClient.CallAsync(
                        credentials,
                        "core_reportbuilder_filters_reset",
                        new Dictionary<string, object?>
                        {
                            ["reportid"] = reportId,
                            ["parameters"] = "{}"
                        },
                        CancellationToken.None);
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }
    }

    private static (IReadOnlyList<Dictionary<string, object?>> Rows, int TotalRowCount) ParseWsReportData(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
        {
            throw new InvalidOperationException("O retorno do Web Service nao contem dados (data) validos.");
        }

        if (!data.TryGetProperty("headers", out var headers))
        {
            throw new InvalidOperationException("O retorno do Web Service nao contem cabecalhos (headers) validos na estrutura 'data'.");
        }

        int totalRowCount = 0;
        if (data.TryGetProperty("totalrowcount", out var totalRowElement) && totalRowElement.ValueKind == JsonValueKind.Number)
        {
            totalRowCount = totalRowElement.GetInt32();
        }

        var headerList = new List<string>();
        foreach (var headerElement in headers.EnumerateArray())
        {
            var headerName = headerElement.GetString() ?? "campo";
            headerList.Add(headerName);
        }

        var result = new List<Dictionary<string, object?>>();
        if (!data.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
        {
            return (result, totalRowCount);
        }

        foreach (var rowElement in rowsElement.EnumerateArray())
        {
            if (rowElement.ValueKind != JsonValueKind.Object || !rowElement.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var rowDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var usedHeaders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int colIndex = 0;
            foreach (var cellElement in columns.EnumerateArray())
            {
                var rawHeader = colIndex < headerList.Count ? headerList[colIndex] : $"campo{colIndex}";
                var header = MakeUniqueHeader(NormalizeHeader(rawHeader), usedHeaders);
                rowDict[header] = NormalizeValue(header, cellElement);
                colIndex++;
            }
            result.Add(rowDict);
        }

        return (result, totalRowCount);
    }

    private static string NormalizeHeader(string value)
    {
        value = Regex.Replace(value, "<.*?>", string.Empty);

        var compact = NonAlphaNumericRegex().Replace(RemoveDiacritics(value), string.Empty).ToLowerInvariant();
        if (KnownHeaders.TryGetValue(compact, out var knownHeader)) return knownHeader;

        var words = NonAlphaNumericRegex().Split(RemoveDiacritics(value)).Where(x => x.Length > 0).ToArray();
        if (words.Length == 0) return "campo";
        return words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(x => char.ToUpperInvariant(x[0]) + x[1..].ToLowerInvariant()));
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC);
    }

    private static string MakeUniqueHeader(string header, Dictionary<string, int> counts)
    {
        counts.TryGetValue(header, out var count);
        counts[header] = ++count;
        return count == 1 ? header : $"{header}{count}";
    }

    private static object? NormalizeValue(string header, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer)) return integer;
            if (value.TryGetDecimal(out var decimalNumber)) return decimalNumber;
            return value.GetDouble();
        }
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return JsonSerializer.Deserialize<object>(value.GetRawText());

        var trimmed = value.GetString()?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && trimmed.Contains('<'))
        {
            trimmed = Regex.Replace(trimmed, "<.*?>", string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(trimmed))
            return IsNullableReportField(header) ? null : string.Empty;
        if (trimmed is "-" or "Nao disponivel" or "Nunca") return null;
        if ((header.StartsWith("data", StringComparison.OrdinalIgnoreCase) ||
             header.StartsWith("ultimoAcesso", StringComparison.OrdinalIgnoreCase)) && TryParseMoodleDate(trimmed, out var date))
            return date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        if (header.StartsWith("nota", StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out var grade))
            return grade;

        return trimmed;
    }

    private static bool IsNullableReportField(string header) =>
        header.StartsWith("data", StringComparison.OrdinalIgnoreCase) ||
        header.StartsWith("ultimoAcesso", StringComparison.OrdinalIgnoreCase) ||
        header.StartsWith("nota", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseMoodleDate(string value, out DateTime date)
    {
        var match = MoodleLongDateRegex().Match(value);
        if (match.Success && MonthNumbers.TryGetValue(match.Groups[2].Value, out var month))
        {
            date = new DateTime(
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                month,
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                0);
            return true;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.GetCultureInfo("pt-BR"),
            DateTimeStyles.AllowWhiteSpaces,
            out date);
    }

    private static readonly IReadOnlyDictionary<string, int> MonthNumbers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["jan"] = 1, ["fev"] = 2, ["mar"] = 3, ["abr"] = 4,
            ["mai"] = 5, ["jun"] = 6, ["jul"] = 7, ["ago"] = 8,
            ["set"] = 9, ["out"] = 10, ["nov"] = 11, ["dez"] = 12
        };

    private static readonly IReadOnlyDictionary<string, string> KnownHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aluno"] = "aluno",
            ["cpf"] = "cpf",
            ["email"] = "email",
            ["telefone1"] = "telefone1",
            ["telefone2"] = "telefone2",
            ["papel"] = "papel",
            ["iduc"] = "idUc",
            ["cursocaminho"] = "cursoCaminho",
            ["unidadecurricular"] = "unidadeCurricular",
            ["notafinal"] = "notaFinal",
            ["datadeinciouc"] = "dataInicioUc",
            ["datadeiniciouc"] = "dataInicioUc",
            ["datatrminouc"] = "dataTerminoUc",
            ["dataterminouc"] = "dataTerminoUc",
            ["ltimoacessouc"] = "ultimoAcessoUc",
            ["ultimoacessouc"] = "ultimoAcessoUc",
            ["ltimoacessomoodle"] = "ultimoAcessoMoodle",
            ["ultimoacessomoodle"] = "ultimoAcessoMoodle",
            ["statusuc"] = "statusUc",
            ["datamatricula"] = "dataMatricula",
            ["categoria"] = "categoria"
        };

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex("(?:^|,\\s*)(\\d{1,2})\\s+([a-zç]{3})\\.\\s+(\\d{4}),\\s+(\\d{2}):(\\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex MoodleLongDateRegex();
}
