using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;

namespace TradeOps.Connectors.Deribit;

/// <summary>Established, pinned trading socket. The only order write consumes a one-use physical
/// permit at the socket boundary. No reconnect/resend, credential exposure, cancel-all or timers.</summary>
public sealed class DeribitOmsWebSocketTransport : IOmsAccountOrderTransport
{
    private readonly DeribitPrivateCaptureBinding binding;
    private readonly IDeribitPrivateSocket socket;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Guid sessionId = Guid.NewGuid();
    private long id; private int stopped;
    private DateTimeOffset expires;
    private DeribitOmsWebSocketTransport(DeribitPrivateCaptureBinding binding, IDeribitPrivateSocket socket)
    { this.binding = binding; this.socket = socket; }
    public static Task<DeribitOmsWebSocketTransport> OpenAsync(DeribitPrivateCaptureBinding binding, CancellationToken ct = default)
        => OpenAsync(binding, new DeribitPrivateSocket(), ct);
    internal static async Task<DeribitOmsWebSocketTransport> OpenAsync(DeribitPrivateCaptureBinding binding, IDeribitPrivateSocket socket, CancellationToken ct)
    {
        var transport = new DeribitOmsWebSocketTransport(binding, socket);
        try
        {
            _ = binding.Plan.Validate(binding.Options, binding.Scope);
            if (binding.Plan.IsSubaccount) throw new InvalidOperationException("Execution v1 supports pinned main-account credentials only.");
            await socket.ConnectAsync(new(binding.Options.WebSocketUrl), ct);
            var session = "session:oms_owner_" + transport.sessionId.ToString("N");
            var auth = await transport.ReadRpc("public/auth", new { grant_type = "client_credentials", client_id = binding.Options.ClientId,
                client_secret = binding.Options.ClientSecret, scope = session + " expires:300 trade:read_write account:read wallet:none block_trade:none block_rfq:none" }, ct);
            var scope = auth.GetProperty("scope").GetString()!;
            if (!scope.Split(' ').Contains("trade:read_write") || !DeribitRecoveryQueries.ValidGrant(scope.Replace("trade:read_write", "trade:read", StringComparison.Ordinal), session) ||
                auth.GetProperty("expires_in").GetInt32() is < 120 or > 300) throw new InvalidOperationException("Unexpected execution session grant.");
            var summary = await transport.ReadRpc("private/get_account_summary", new { currency = binding.Options.AccountCurrency, extended = true }, ct);
            if (summary.GetProperty("id").GetInt64() != binding.Plan.ExpectedAccountId || summary.GetProperty("type").GetString() != "main" ||
                summary.GetProperty("currency").GetString() != binding.Options.AccountCurrency) throw new InvalidOperationException("Execution native pin mismatch.");
            transport.expires = DateTimeOffset.UtcNow.AddSeconds(auth.GetProperty("expires_in").GetInt32() - 5);
            return transport;
        }
        catch { await transport.DisposeAsync(); throw new InvalidOperationException("Owned trading socket could not be established."); }
    }

    public async Task<DeribitJsonRpcCommandReceipt> SendAsync(OmsJournalCommandRequest command, OmsPhysicalWritePermit permit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permit);
        await serial.WaitAsync(ct);
        try
        {
            command.Validate(); var p = command.Payload;
            if (Volatile.Read(ref stopped) != 0 || DateTimeOffset.UtcNow >= expires || command.Scope != binding.Scope ||
                !binding.Plan.Instruments.TryGetValue(p.Instrument, out var metadata) || metadata.Kind != "future" || metadata.AmountUnit != OmsPrivateAmountUnit.Usd ||
                p.Kind is not (OmsJournalCommandKind.Place or OmsJournalCommandKind.Cancel) || p.OrderType != OrderType.Limit || p.QuantityUnit != TradeOps.Domain.Oms.OmsQuantityUnit.QuoteAsset ||
                p.TimeInForce is not ("GTC" or "good_til_cancelled") ||
                (p.Kind==OmsJournalCommandKind.Place ? p.TargetClientOrderId is null||p.TargetExchangeOrderId is not null : p.TargetExchangeOrderId is null) ||
                p.TriggerPrice is not null || p.FixClOrdId is not null || p.FixOrigClOrdId is not null)
                throw new InvalidOperationException("Unsupported owned WS order terms.");
            var rpcId = ++id; var method = p.Kind==OmsJournalCommandKind.Cancel?"private/cancel":p.Side == OrderSide.Buy ? "private/buy" : "private/sell";
            object parameters=p.Kind==OmsJournalCommandKind.Cancel?new{order_id=p.TargetExchangeOrderId}:
                new { instrument_name = p.Instrument, amount = p.Quantity, price = p.LimitPrice, type = "limit", time_in_force = "good_til_cancelled",
                    post_only = p.PostOnly, reject_post_only = p.PostOnly, reduce_only = p.ReduceOnly, label = p.TargetClientOrderId };
            var data = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = rpcId, method,@params=parameters });
            var sent = DateTimeOffset.UtcNow;
            await permit.InitiateAsync(command, () => socket.SendAsync(data, ct));
            var response = await socket.ReceiveAsync(32768, ct); var received = DateTimeOffset.UtcNow;
            using var doc = JsonDocument.Parse(response);
            return DeribitJsonRpcCommandReceiptParser.Parse(command, metadata, sessionId, rpcId, method, sent, received, doc.RootElement);
        }
        catch { Abort(); throw; }
        finally { serial.Release(); }
    }

    private async Task<JsonElement> ReadRpc(string method, object parameters, CancellationToken ct)
    {
        if (method is not ("public/auth" or "private/get_account_summary")) throw new InvalidOperationException();
        var rpcId = ++id;
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = rpcId, method, @params = parameters }), ct);
        using var doc = JsonDocument.Parse(await socket.ReceiveAsync(32768, ct)); var r = doc.RootElement;
        if (r.GetProperty("jsonrpc").GetString() != "2.0" || r.GetProperty("id").GetInt64() != rpcId || r.TryGetProperty("error", out _)) throw new InvalidOperationException();
        return r.GetProperty("result").Clone();
    }
    public void Abort() { Interlocked.Exchange(ref stopped, 1); socket.Abort(); }
    public async ValueTask DisposeAsync()
    {
        Abort(); await serial.WaitAsync(); try { socket.Dispose(); } finally { serial.Release(); }
    }
}
