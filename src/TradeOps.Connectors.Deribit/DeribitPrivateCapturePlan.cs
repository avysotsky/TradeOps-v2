using System.Collections.Frozen;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Trusted server instrument metadata, never inferred from UI state or a bare symbol.</summary>
public sealed record DeribitPrivateInstrument(string Name, string Kind, string Currency,
    OmsPrivateAmountUnit AmountUnit, string AmountCurrency);

public sealed record DeribitPrivateStreamLimits
{
    public int MaxMessageBytes { get; init; } = 262144;
    public int MaxEventsPerMessage { get; init; } = 128;
    public int BufferCapacity { get; init; } = 256;
    public int HeartbeatSeconds { get; init; } = 20;
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan CaptureTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReconnectMinimum { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaximum { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxConnectionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        if (MaxMessageBytes is < 1024 or > 1048576 || MaxEventsPerMessage is < 1 or > 2048 ||
            BufferCapacity is < 1 or > 4096 || HeartbeatSeconds is < 10 or > 60 ||
            RpcTimeout < TimeSpan.FromMilliseconds(10) || RpcTimeout > TimeSpan.FromMinutes(1) ||
            ReceiveTimeout < TimeSpan.FromMilliseconds(10) || ReceiveTimeout > TimeSpan.FromMinutes(5) ||
            CaptureTimeout < TimeSpan.FromMilliseconds(10) || CaptureTimeout > TimeSpan.FromMinutes(1) ||
            ReconnectMinimum < TimeSpan.Zero || ReconnectMaximum < ReconnectMinimum ||
            ReconnectMaximum > TimeSpan.FromMinutes(1) || MaxConnectionLifetime < TimeSpan.FromMilliseconds(50) ||
            MaxConnectionLifetime > TimeSpan.FromMinutes(30))
            throw new ArgumentException("Invalid bounded private-stream limits.");
    }
}

/// <summary>Explicit coverage and credential-to-account mapping; not a promise of full account coverage.</summary>
public sealed class DeribitPrivateCapturePlan
{
    public long ExpectedAccountId { get; }
    public bool IsSubaccount { get; }
    public DeribitPrivateStreamLimits Limits { get; }
    public int InstrumentCount => Instruments.Count;
    internal FrozenDictionary<string, DeribitPrivateInstrument> Instruments { get; }
    internal string[] Channels { get; }
    public bool CapturesAllCurrenciesAndKinds { get; }

    public DeribitPrivateCapturePlan(long expectedAccountId, bool isSubaccount,
        IEnumerable<DeribitPrivateInstrument> instruments, DeribitPrivateStreamLimits? limits = null)
        : this(expectedAccountId, isSubaccount, instruments, limits, false) { }

    public static DeribitPrivateCapturePlan ForAccountRecovery(long expectedAccountId, bool isSubaccount,
        IEnumerable<DeribitPrivateInstrument> instruments, DeribitPrivateStreamLimits? limits = null) =>
        new(expectedAccountId, isSubaccount, instruments, limits, true);

    private DeribitPrivateCapturePlan(long expectedAccountId, bool isSubaccount,
        IEnumerable<DeribitPrivateInstrument> instruments, DeribitPrivateStreamLimits? limits, bool accountRecovery)
    {
        if (expectedAccountId <= 0) throw new ArgumentException("Expected remote account ID is required.");
        ExpectedAccountId = expectedAccountId;
        IsSubaccount = isSubaccount;
        Limits = limits ?? new();
        Limits.Validate();
        var items = instruments.Take(4097).ToArray();
        if (items.Length is < 1 or > 4096 || items.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("A bounded unique instrument manifest is required.");
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name != item.Name.Trim() || item.Name.Length > 160 || item.Name.Any(char.IsControl) ||
                item.Kind is not ("future" or "option") || !Token(item.Currency) || !Token(item.AmountCurrency) ||
                !Enum.IsDefined(item.AmountUnit) || (item.AmountUnit == OmsPrivateAmountUnit.Usd && item.AmountCurrency != "USD"))
                throw new ArgumentException("Invalid trusted instrument metadata.");
        }
        Instruments = items.ToFrozenDictionary(x => x.Name, StringComparer.Ordinal);
        CapturesAllCurrenciesAndKinds = accountRecovery;
        Channels = accountRecovery ? ["user.changes.any.any.raw", "user.orders.any.any.raw", "user.portfolio.any", "user.trades.any.any.100ms"] : items.SelectMany(x => new[] { $"user.orders.{x.Kind}.{x.Currency}.raw",
            $"user.trades.{x.Kind}.{x.Currency}.100ms" }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal Uri Validate(DeribitOptions options, AccountKey scope)
    {
        var host = scope.Environment switch { OmsEnvironment.Testnet => "test.deribit.com",
            OmsEnvironment.Mainnet => "www.deribit.com", _ => "" };
        if (scope.Venue.Value != "DERIBIT" || scope.Subaccount.HasValue != IsSubaccount || host.Length == 0 ||
            !Uri.TryCreate(options.WebSocketUrl, UriKind.Absolute, out var uri) || uri.Scheme != "wss" ||
            uri.Host != host || uri.Port != 443 || uri.AbsolutePath != "/ws/api/v2" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new ArgumentException("Private capture endpoint/credentials do not match the trusted account binding.");
        return uri;
    }

    private static bool Token(string value) => !string.IsNullOrEmpty(value) && value.Length <= 32 &&
        value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');
}

internal sealed class DeribitCaptureException(OmsPrivateStreamProblem problem, bool retryable = true) : Exception("Deribit private capture stopped: " + problem)
{
    public OmsPrivateStreamProblem Problem { get; } = problem;
    public bool Retryable { get; } = retryable;
}
