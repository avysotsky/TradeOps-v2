using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.PublicResearchDemo;

public sealed record SecFilingMetadata(
    string Cik,
    string AccessionNumber,
    string FormType,
    DateOnly ReportDate,
    DateOnly FilingDate,
    DateTimeOffset AcceptedAt,
    string PrimaryDocument,
    Uri SourceUri);

public sealed record PublicDemoRawSnapshot(
    DateTimeOffset RetrievedAt,
    string SecSubmissionsJson,
    string SecCompanyFactsJson,
    string AlphaVantageDailyJson);

public sealed record PublicDemoCacheResource(
    string Provider,
    string SourceUri,
    DateTimeOffset RetrievedAt,
    string Symbol,
    string? Cik,
    string RelativePath,
    string Sha256);

public sealed record PublicDemoCacheManifest(
    int Version,
    string Symbol,
    string Cik,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<string> AccessionNumbers,
    DateOnly MarketDataStart,
    DateOnly MarketDataEnd,
    IReadOnlyList<PublicDemoCacheResource> Resources);

public static class PublicDemoNetworkGate
{
    public const string ConfirmationVariable =
        "TRADEOPS_PUBLIC_DEMO_CONFIRM";
    public const string ConfirmationValue =
        "RUN_PUBLIC_DATA_DEMO";
    public const string SecUserAgentVariable =
        "TRADEOPS_SEC_USER_AGENT";

    public static string RequireExternalFetch()
    {
        var confirmation =
            Environment.GetEnvironmentVariable(
                ConfirmationVariable);

        if (!string.Equals(
                confirmation,
                ConfirmationValue,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"External public-data acquisition is disabled. Set {ConfirmationVariable}={ConfirmationValue} to opt in.");
        }

        var userAgent =
            Environment.GetEnvironmentVariable(
                SecUserAgentVariable);

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            throw new InvalidOperationException(
                $"SEC fetch requires {SecUserAgentVariable}. Supply a runtime contact-style User-Agent; do not commit personal contact data.");
        }

        return userAgent.Trim();
    }
}

public sealed class PublicDemoDownloader : IDisposable
{
    public const string SecSubmissionsUri =
        "https://data.sec.gov/submissions/CIK0000051143.json";
    public const string SecCompanyFactsUri =
        "https://data.sec.gov/api/xbrl/companyfacts/CIK0000051143.json";
    public const string AlphaVantageDailyUri =
        "https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol=IBM&apikey=demo";

    private static readonly TimeSpan SecMinimumInterval =
        TimeSpan.FromMilliseconds(350);

    private readonly HttpClient _httpClient;
    private DateTimeOffset? _lastSecRequestCompletedAt;

    public PublicDemoDownloader(HttpMessageHandler? handler = null)
    {
        handler ??=
            new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            };

        _httpClient =
            new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
    }

    public async Task<PublicDemoRawSnapshot> DownloadIbmAsync(
        CancellationToken cancellationToken = default)
    {
        var secUserAgent =
            PublicDemoNetworkGate.RequireExternalFetch();

        var submissions =
            await GetSecAsync(
                SecSubmissionsUri,
                secUserAgent,
                cancellationToken);

        var companyFacts =
            await GetSecAsync(
                SecCompanyFactsUri,
                secUserAgent,
                cancellationToken);

        var marketData =
            await GetAsync(
                AlphaVantageDailyUri,
                userAgent: null,
                cancellationToken);

        return new PublicDemoRawSnapshot(
            DateTimeOffset.UtcNow,
            submissions,
            companyFacts,
            marketData);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<string> GetSecAsync(
        string uri,
        string userAgent,
        CancellationToken cancellationToken)
    {
        if (_lastSecRequestCompletedAt.HasValue)
        {
            var elapsed =
                DateTimeOffset.UtcNow -
                _lastSecRequestCompletedAt.Value;
            var delay =
                SecMinimumInterval - elapsed;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(
                    delay,
                    cancellationToken);
            }
        }

        var response =
            await GetAsync(
                uri,
                userAgent,
                cancellationToken);

        _lastSecRequestCompletedAt =
            DateTimeOffset.UtcNow;

        return response;
    }

    private async Task<string> GetAsync(
        string uri,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        using var request =
            new HttpRequestMessage(
                HttpMethod.Get,
                uri);

        request.Headers.Accept.ParseAdd(
            "application/json");

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                userAgent);
        }

        using var response =
            await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        var content =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Public-data provider returned HTTP {(int)response.StatusCode} for {uri}.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                $"Public-data provider returned an empty response for {uri}.");
        }

        return content;
    }
}

public static class SecSubmissionParser
{
    public static IReadOnlyList<SecFilingMetadata> Parse(
        string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document =
            JsonDocument.Parse(json);

        var root =
            document.RootElement;

        var cik =
            ReadScalarAsString(
                root.GetProperty("cik"));

        if (!root.TryGetProperty(
                "filings",
                out var filings) ||
            !filings.TryGetProperty(
                "recent",
                out var recent))
        {
            throw new InvalidOperationException(
                "SEC submissions payload does not contain filings.recent.");
        }

        var accession =
            recent.GetProperty("accessionNumber");
        var forms =
            recent.GetProperty("form");
        var reportDates =
            recent.GetProperty("reportDate");
        var filingDates =
            recent.GetProperty("filingDate");
        var acceptance =
            recent.GetProperty("acceptanceDateTime");
        var primaryDocuments =
            recent.GetProperty("primaryDocument");

        var count =
            new[]
            {
                accession.GetArrayLength(),
                forms.GetArrayLength(),
                reportDates.GetArrayLength(),
                filingDates.GetArrayLength(),
                acceptance.GetArrayLength(),
                primaryDocuments.GetArrayLength()
            }.Min();

        var result =
            new List<SecFilingMetadata>();

        for (var index = 0;
             index < count;
             index++)
        {
            var form =
                forms[index].GetString()
                ?? string.Empty;

            if (!string.Equals(
                    form,
                    "10-Q",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    form,
                    "10-K",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var accessionNumber =
                RequireString(
                    accession[index],
                    "SEC accession number");
            var reportDate =
                ParseDateOnly(
                    RequireString(
                        reportDates[index],
                        "SEC report date"));
            var filingDate =
                ParseDateOnly(
                    RequireString(
                        filingDates[index],
                        "SEC filing date"));
            var acceptedAt =
                ParseDateTimeOffset(
                    RequireString(
                        acceptance[index],
                        "SEC acceptance timestamp"));
            var primaryDocument =
                RequireString(
                    primaryDocuments[index],
                    "SEC primary document");

            result.Add(
                new SecFilingMetadata(
                    cik.PadLeft(10, '0'),
                    accessionNumber,
                    form.ToUpperInvariant(),
                    reportDate,
                    filingDate,
                    acceptedAt,
                    primaryDocument,
                    BuildArchiveUri(
                        cik,
                        accessionNumber,
                        primaryDocument)));
        }

        return result
            .OrderBy(item => item.AcceptedAt)
            .ThenBy(
                item => item.AccessionNumber,
                StringComparer.Ordinal)
            .ToArray();
    }

    private static Uri BuildArchiveUri(
        string cik,
        string accessionNumber,
        string primaryDocument)
    {
        var numericCik =
            cik.TrimStart('0');

        if (numericCik.Length == 0)
        {
            numericCik = "0";
        }

        var compactAccession =
            accessionNumber.Replace(
                "-",
                string.Empty,
                StringComparison.Ordinal);

        return new Uri(
            $"https://www.sec.gov/Archives/edgar/data/{numericCik}/{compactAccession}/{primaryDocument}");
    }

    private static string ReadScalarAsString(
        JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String =>
                value.GetString() ?? string.Empty,
            JsonValueKind.Number =>
                value.GetRawText(),
            _ =>
                throw new InvalidOperationException(
                    "SEC CIK has an unsupported JSON type.")
        };

    private static string RequireString(
        JsonElement value,
        string fieldName)
    {
        var text =
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{fieldName} is missing.");
        }

        return text.Trim();
    }

    private static DateOnly ParseDateOnly(
        string value)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result))
        {
            throw new InvalidOperationException(
                $"Invalid SEC date '{value}'.");
        }

        return result;
    }

    private static DateTimeOffset ParseDateTimeOffset(
        string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal |
                DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            throw new InvalidOperationException(
                $"Invalid SEC acceptance timestamp '{value}'.");
        }

        return result.ToUniversalTime();
    }
}

public static class SecHistoricalAvailabilityPolicy
{
    public static DateTimeOffset Resolve(
        SecFilingMetadata filing)
    {
        ArgumentNullException.ThrowIfNull(filing);

        var eastern =
            FindEasternTimeZone();

        var localBoundary =
            new DateTime(
                filing.FilingDate.Year,
                filing.FilingDate.Month,
                filing.FilingDate.Day,
                22,
                0,
                0,
                DateTimeKind.Unspecified);

        var utcBoundary =
            new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(
                    localBoundary,
                    eastern),
                TimeSpan.Zero);

        if (utcBoundary <
            filing.AcceptedAt.ToUniversalTime())
        {
            throw new InvalidOperationException(
                $"The conservative SEC historical availability boundary for {filing.AccessionNumber} precedes its AcceptedAt source timestamp.");
        }

        return utcBoundary;
    }

    private static TimeZoneInfo FindEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                "America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                "Eastern Standard Time");
        }
    }
}

public static class SecCompanyFactsParser
{
    private static readonly (string Concept, string Unit)[]
        SupportedFacts =
        [
            (
                "RevenueFromContractWithCustomerExcludingAssessedTax",
                "USD"),
            (
                "SalesRevenueNet",
                "USD"),
            (
                "Revenues",
                "USD"),
            (
                "EarningsPerShareDiluted",
                "USD/shares"),
            (
                "NetIncomeLoss",
                "USD"),
            (
                "ProfitLoss",
                "USD"),
            (
                "GrossProfit",
                "USD"),
            (
                "OperatingIncomeLoss",
                "USD")
        ];

    private static readonly string[]
        RevenueConceptPreference =
        [
            "RevenueFromContractWithCustomerExcludingAssessedTax",
            "SalesRevenueNet",
            "Revenues"
        ];

    public static IReadOnlyList<SecStructuredFiling>
        CreateStructuredFilings(
            string companyFactsJson,
            IReadOnlyList<SecFilingMetadata> filings,
            string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            companyFactsJson);
        ArgumentNullException.ThrowIfNull(filings);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        using var document =
            JsonDocument.Parse(
                companyFactsJson);

        var root =
            document.RootElement;

        var factsRoot =
            root.GetProperty("facts")
                .GetProperty("us-gaap");

        var result =
            new List<SecStructuredFiling>();

        foreach (var filing in filings)
        {
            var period =
                FindFiscalPeriod(
                    factsRoot,
                    filing);

            var structuredFacts =
                new List<SecStructuredFact>();

            foreach (
                var (concept, unit)
                in SupportedFacts)
            {
                foreach (
                    var entry
                    in ReadFactEntries(
                        factsRoot,
                        concept,
                        unit))
                {
                    if (!IsExactFilingPeriod(
                            entry,
                            filing,
                            period))
                    {
                        continue;
                    }

                    if (structuredFacts.Any(
                            existing =>
                                string.Equals(
                                    existing.Concept,
                                    concept,
                                    StringComparison.Ordinal)
                                && string.Equals(
                                    existing.Unit,
                                    unit,
                                    StringComparison.OrdinalIgnoreCase)
                                && existing.Value ==
                                   entry.Value
                                && existing.PeriodStart ==
                                   entry.Start
                                && existing.PeriodEnd ==
                                   entry.End))
                    {
                        continue;
                    }

                    structuredFacts.Add(
                        new SecStructuredFact(
                            "us-gaap",
                            concept,
                            unit,
                            entry.Value,
                            entry.Start!.Value,
                            entry.End,
                            filing.AccessionNumber,
                            filing.FormType));
                }
            }

            if (structuredFacts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No exact XBRL facts found for accession {filing.AccessionNumber}.");
            }

            var fiscalPeriod =
                FormatFiscalPeriod(
                    period,
                    filing);

            // Historical replay deliberately uses a conservative
            // availability boundary derived from SEC filing-date
            // metadata. It is not an exact dissemination timestamp.
            result.Add(
                new SecStructuredFiling(
                    $"sec:{symbol.ToUpperInvariant()}:{filing.AccessionNumber}",
                    symbol.ToUpperInvariant(),
                    fiscalPeriod,
                    filing.Cik,
                    filing.AccessionNumber,
                    filing.FormType,
                    filing.SourceUri,
                    filing.AcceptedAt,
                    period.Start,
                    period.End,
                    "USD",
                    structuredFacts,
                    PubliclyAvailableAt:
                        SecHistoricalAvailabilityPolicy
                            .Resolve(filing)));
        }

        return result;
    }

    private static CompanyFactPeriod FindFiscalPeriod(
        JsonElement factsRoot,
        SecFilingMetadata filing)
    {
        var candidates =
            new List<CompanyFactEntry>();

        foreach (
            var concept
            in RevenueConceptPreference)
        {
            candidates.AddRange(
                ReadFactEntries(
                        factsRoot,
                        concept,
                        "USD")
                    .Where(
                        entry =>
                            IsExactAccession(
                                entry,
                                filing)
                            && entry.Start.HasValue));
        }

        if (candidates.Count == 0)
        {
            foreach (
                var fallback
                in new[]
                {
                    "NetIncomeLoss",
                    "OperatingIncomeLoss"
                })
            {
                candidates.AddRange(
                    ReadFactEntries(
                            factsRoot,
                            fallback,
                            "USD")
                        .Where(
                            entry =>
                                IsExactAccession(
                                    entry,
                                    filing)
                                && entry.Start.HasValue));
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No duration XBRL fact can establish the fiscal interval for accession {filing.AccessionNumber}.");
        }

        var bounded =
            candidates
                .Where(
                    item =>
                    {
                        var days =
                            item.End.DayNumber -
                            item.Start!.Value.DayNumber +
                            1;

                        return string.Equals(
                                   filing.FormType,
                                   "10-Q",
                                   StringComparison.OrdinalIgnoreCase)
                            ? days is >= 45 and <= 150
                            : days is >= 250 and <= 450;
                    })
                .ToArray();

        var usable =
            bounded.Length > 0
                ? bounded
                : candidates.ToArray();

        var selected =
            string.Equals(
                filing.FormType,
                "10-Q",
                StringComparison.OrdinalIgnoreCase)
                ? usable
                    .OrderBy(
                        item =>
                            item.End.DayNumber -
                            item.Start!.Value.DayNumber)
                    .ThenByDescending(
                        item => item.End)
                    .First()
                : usable
                    .OrderByDescending(
                        item =>
                            item.End.DayNumber -
                            item.Start!.Value.DayNumber)
                    .ThenByDescending(
                        item => item.End)
                    .First();

        return new CompanyFactPeriod(
            selected.Start!.Value,
            selected.End,
            selected.FiscalYear,
            selected.FiscalPeriod);
    }

    private static IEnumerable<CompanyFactEntry>
        ReadFactEntries(
            JsonElement factsRoot,
            string concept,
            string unit)
    {
        if (!factsRoot.TryGetProperty(
                concept,
                out var conceptNode)
            || !conceptNode.TryGetProperty(
                "units",
                out var units)
            || !units.TryGetProperty(
                unit,
                out var values)
            || values.ValueKind !=
               JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (!value.TryGetProperty(
                    "accn",
                    out var accessionNode)
                || !value.TryGetProperty(
                    "form",
                    out var formNode)
                || !value.TryGetProperty(
                    "end",
                    out var endNode)
                || !value.TryGetProperty(
                    "val",
                    out var valueNode))
            {
                continue;
            }

            var accession =
                accessionNode.GetString();
            var form =
                formNode.GetString();
            var endText =
                endNode.GetString();

            if (string.IsNullOrWhiteSpace(accession)
                || string.IsNullOrWhiteSpace(form)
                || string.IsNullOrWhiteSpace(endText)
                || !DateOnly.TryParseExact(
                    endText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var end)
                || !TryReadDecimal(
                    valueNode,
                    out var numericValue))
            {
                continue;
            }

            DateOnly? start = null;

            if (value.TryGetProperty(
                    "start",
                    out var startNode))
            {
                var startText =
                    startNode.GetString();

                if (!string.IsNullOrWhiteSpace(
                        startText)
                    && DateOnly.TryParseExact(
                        startText,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var parsedStart))
                {
                    start = parsedStart;
                }
            }

            int? fiscalYear = null;

            if (value.TryGetProperty(
                    "fy",
                    out var fyNode)
                && fyNode.ValueKind ==
                   JsonValueKind.Number
                && fyNode.TryGetInt32(
                    out var fy))
            {
                fiscalYear = fy;
            }

            string? fiscalPeriod = null;

            if (value.TryGetProperty(
                    "fp",
                    out var fpNode)
                && fpNode.ValueKind ==
                   JsonValueKind.String)
            {
                fiscalPeriod =
                    fpNode.GetString();
            }

            yield return new CompanyFactEntry(
                accession.Trim(),
                form.Trim().ToUpperInvariant(),
                start,
                end,
                numericValue,
                fiscalYear,
                fiscalPeriod);
        }
    }

    private static bool IsExactFilingPeriod(
        CompanyFactEntry entry,
        SecFilingMetadata filing,
        CompanyFactPeriod period) =>
        IsExactAccession(
            entry,
            filing)
        && entry.Start ==
           period.Start
        && entry.End ==
           period.End;

    private static bool IsExactAccession(
        CompanyFactEntry entry,
        SecFilingMetadata filing) =>
        string.Equals(
            entry.AccessionNumber,
            filing.AccessionNumber,
            StringComparison.Ordinal)
        && string.Equals(
            entry.FormType,
            filing.FormType,
            StringComparison.OrdinalIgnoreCase);

    private static string FormatFiscalPeriod(
        CompanyFactPeriod period,
        SecFilingMetadata filing)
    {
        if (period.FiscalYear.HasValue
            && !string.IsNullOrWhiteSpace(
                period.FiscalPeriod))
        {
            return $"FY{period.FiscalYear.Value}-{period.FiscalPeriod!.Trim().ToUpperInvariant()}";
        }

        return $"FY{filing.ReportDate.Year}-{filing.FormType}";
    }

    private static bool TryReadDecimal(
        JsonElement value,
        out decimal result)
    {
        if (value.ValueKind ==
            JsonValueKind.Number
            && value.TryGetDecimal(
                out result))
        {
            return true;
        }

        if (value.ValueKind ==
            JsonValueKind.String)
        {
            return decimal.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }

        result = default;
        return false;
    }

    private sealed record CompanyFactEntry(
        string AccessionNumber,
        string FormType,
        DateOnly? Start,
        DateOnly End,
        decimal Value,
        int? FiscalYear,
        string? FiscalPeriod);

    private sealed record CompanyFactPeriod(
        DateOnly Start,
        DateOnly End,
        int? FiscalYear,
        string? FiscalPeriod);
}

public static class AlphaVantageDailyParser
{
    public static IReadOnlyList<MarketDataBar> Parse(
        string json,
        InstrumentReference instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(instrument);

        using var document =
            JsonDocument.Parse(json);

        var root =
            document.RootElement;

        if (!root.TryGetProperty(
                "Time Series (Daily)",
                out var series)
            || series.ValueKind !=
               JsonValueKind.Object)
        {
            var providerMessage =
                root.TryGetProperty(
                    "Information",
                    out var information)
                    ? information.GetString()
                    : root.TryGetProperty(
                        "Note",
                        out var note)
                        ? note.GetString()
                        : null;

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(
                    providerMessage)
                    ? "Alpha Vantage response does not contain Time Series (Daily)."
                    : $"Alpha Vantage response does not contain Time Series (Daily): {providerMessage}");
        }

        var eastern =
            FindEasternTimeZone();
        var bars =
            new List<MarketDataBar>();

        foreach (
            var property
            in series.EnumerateObject())
        {
            if (!DateOnly.TryParseExact(
                    property.Name,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                throw new InvalidOperationException(
                    $"Invalid Alpha Vantage trading date '{property.Name}'.");
            }

            var data =
                property.Value;

            var open =
                ReadDecimal(
                    data,
                    "1. open");
            var high =
                ReadDecimal(
                    data,
                    "2. high");
            var low =
                ReadDecimal(
                    data,
                    "3. low");
            var close =
                ReadDecimal(
                    data,
                    "4. close");

            bars.Add(
                new MarketDataBar(
                    instrument,
                    MarketDataBarPeriod.Daily,
                    ConvertNewYorkToUtc(
                        date,
                        9,
                        30,
                        eastern),
                    ConvertNewYorkToUtc(
                        date,
                        16,
                        0,
                        eastern),
                    open,
                    high,
                    low,
                    close));
        }

        return bars
            .OrderBy(item => item.OpenTime)
            .ToArray();
    }

    private static decimal ReadDecimal(
        JsonElement data,
        string property)
    {
        if (!data.TryGetProperty(
                property,
                out var node))
        {
            throw new InvalidOperationException(
                $"Alpha Vantage daily bar is missing '{property}'.");
        }

        var text =
            node.GetString();

        if (!decimal.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || value <= 0m)
        {
            throw new InvalidOperationException(
                $"Alpha Vantage daily bar contains invalid '{property}'.");
        }

        return value;
    }

    private static DateTimeOffset ConvertNewYorkToUtc(
        DateOnly date,
        int hour,
        int minute,
        TimeZoneInfo timeZone)
    {
        var local =
            new DateTime(
                date.Year,
                date.Month,
                date.Day,
                hour,
                minute,
                0,
                DateTimeKind.Unspecified);

        var utc =
            TimeZoneInfo.ConvertTimeToUtc(
                local,
                timeZone);

        return new DateTimeOffset(
            utc,
            TimeSpan.Zero);
    }

    private static TimeZoneInfo FindEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                "America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                "Eastern Standard Time");
        }
    }
}

public static class PublicDemoCache
{
    public const string DefaultRoot =
        ".tradeops/public-demo-cache";

    private const string ManifestFile =
        "manifest.json";
    private const string SubmissionsFile =
        "sec-submissions.json";
    private const string CompanyFactsFile =
        "sec-companyfacts.json";
    private const string MarketDataFile =
        "alpha-vantage-ibm-daily.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            WriteIndented =
                true
        };

    public static bool Exists(
        string root) =>
        File.Exists(
            Path.Combine(
                root,
                ManifestFile));

    public static PublicDemoCacheManifest Save(
        string root,
        PublicDemoRawSnapshot snapshot,
        IReadOnlyList<string> accessionNumbers,
        DateOnly marketDataStart,
        DateOnly marketDataEnd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(accessionNumbers);

        Directory.CreateDirectory(root);

        var submissionsPath =
            Path.Combine(root, SubmissionsFile);
        var factsPath =
            Path.Combine(root, CompanyFactsFile);
        var marketPath =
            Path.Combine(root, MarketDataFile);

        File.WriteAllText(
            submissionsPath,
            snapshot.SecSubmissionsJson,
            new UTF8Encoding(false));
        File.WriteAllText(
            factsPath,
            snapshot.SecCompanyFactsJson,
            new UTF8Encoding(false));
        File.WriteAllText(
            marketPath,
            snapshot.AlphaVantageDailyJson,
            new UTF8Encoding(false));

        var resources =
            new[]
            {
                Resource(
                    "SEC",
                    PublicDemoDownloader.SecSubmissionsUri,
                    "IBM",
                    "0000051143",
                    SubmissionsFile,
                    snapshot.SecSubmissionsJson,
                    snapshot.RetrievedAt),
                Resource(
                    "SEC",
                    PublicDemoDownloader.SecCompanyFactsUri,
                    "IBM",
                    "0000051143",
                    CompanyFactsFile,
                    snapshot.SecCompanyFactsJson,
                    snapshot.RetrievedAt),
                Resource(
                    "Alpha Vantage",
                    PublicDemoDownloader.AlphaVantageDailyUri,
                    "IBM",
                    null,
                    MarketDataFile,
                    snapshot.AlphaVantageDailyJson,
                    snapshot.RetrievedAt)
            };

        var manifest =
            new PublicDemoCacheManifest(
                Version: 1,
                Symbol: "IBM",
                Cik: "0000051143",
                RetrievedAt:
                    snapshot.RetrievedAt
                        .ToUniversalTime(),
                AccessionNumbers:
                    accessionNumbers.ToArray(),
                MarketDataStart:
                    marketDataStart,
                MarketDataEnd:
                    marketDataEnd,
                Resources:
                    resources);

        File.WriteAllText(
            Path.Combine(
                root,
                ManifestFile),
            JsonSerializer.Serialize(
                manifest,
                JsonOptions),
            new UTF8Encoding(false));

        return manifest;
    }

    public static (
        PublicDemoRawSnapshot Snapshot,
        PublicDemoCacheManifest Manifest)
        Load(
            string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var manifestPath =
            Path.Combine(
                root,
                ManifestFile);

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "Public-demo cache manifest was not found.",
                manifestPath);
        }

        var manifest =
            JsonSerializer.Deserialize<PublicDemoCacheManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions)
            ?? throw new InvalidOperationException(
                "Public-demo cache manifest is invalid.");

        if (manifest.Version != 1
            || !string.Equals(
                manifest.Symbol,
                "IBM",
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.Cik,
                "0000051143",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Public-demo cache manifest is not the supported IBM v1 snapshot.");
        }

        var submissions =
            ReadVerified(
                root,
                manifest,
                SubmissionsFile);
        var companyFacts =
            ReadVerified(
                root,
                manifest,
                CompanyFactsFile);
        var marketData =
            ReadVerified(
                root,
                manifest,
                MarketDataFile);

        return (
            new PublicDemoRawSnapshot(
                manifest.RetrievedAt,
                submissions,
                companyFacts,
                marketData),
            manifest);
    }

    public static string ComputeSha256(
        string content)
    {
        var bytes =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    content));

        return Convert.ToHexString(bytes)
            .ToLowerInvariant();
    }

    private static string ReadVerified(
        string root,
        PublicDemoCacheManifest manifest,
        string relativePath)
    {
        var resource =
            manifest.Resources.SingleOrDefault(
                item =>
                    string.Equals(
                        item.RelativePath,
                        relativePath,
                        StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Cache manifest does not include '{relativePath}'.");

        var path =
            Path.Combine(
                root,
                relativePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Cached public-data payload '{relativePath}' is missing.",
                path);
        }

        var content =
            File.ReadAllText(path);
        var actual =
            ComputeSha256(content);

        if (!string.Equals(
                actual,
                resource.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Checksum mismatch for cached public-data payload '{relativePath}'.");
        }

        return content;
    }

    private static PublicDemoCacheResource Resource(
        string provider,
        string sourceUri,
        string symbol,
        string? cik,
        string relativePath,
        string content,
        DateTimeOffset retrievedAt) =>
        new(
            provider,
            sourceUri,
            retrievedAt.ToUniversalTime(),
            symbol,
            cik,
            relativePath,
            ComputeSha256(content));
}
