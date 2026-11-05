using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.DeribitTestnetOrderQualification;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

internal static class BoundedOmsScenario
{
    internal sealed class Input // Never generated ToString for credentials.
    {
        public string Purpose { get; set; } = "";
        public string Environment { get; set; } = "";
        public bool OwnerLaunched { get; set; }
        public string StageId { get; set; } = "";
        public long ExpectedAccountId { get; set; }
        public string AccountType { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        internal void Validate()
        {
            if (Purpose != "OWNER_BOUNDED_MAINNET_OMS_V1" || Environment != "Mainnet" || !OwnerLaunched ||
                StageId != DeribitBoundedQualificationPolicy.CurrentStage || ExpectedAccountId <= 0 || AccountType is not ("main" or "subaccount") ||
                string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 512 || ClientId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(ClientSecret) || ClientSecret.Length > 4096 || ClientSecret.Any(char.IsControl))
                throw new ArgumentException("Explicit pinned owner launch required.");
        }
    }

    internal sealed record Diagnostics(string Status, string Stage, bool DatabaseUsed, bool PersistentStore,
        int? StagePlacementsUsed, int PlacementAttempts, int CancellationAttempts, int ReceiptsRecorded,
        bool OpenEventCommitted, bool CancelEventCommitted, bool UnexpectedFill, bool OwnOrderMayRemain,
        bool BaselinePositionPreserved, bool CurrentCommandsValidated, bool AssessmentCompleted,
        int RemainingCommandIssues, IReadOnlyList<string> RecoveryBlockers, int SchemaVersion = 1,
        string Instrument = "ETH-PERPETUAL", decimal MaximumNativeUsd = 1, int MaximumStagePlacements = 3,
        decimal MinimumDistanceFraction = 0.10m, bool PostOnly = true, bool AutomaticFlatten = false,
        bool AccountReady = false, bool TradingReady = false, bool DurableProjectionPublished = false);

    internal static async Task<int> RunFromStdinAsync(CancellationToken ct)
    {
        Input? input = null; using var bytes = new MemoryStream(); var chunk = new byte[4096];
        try
        {
            int n; var stdin = Console.OpenStandardInput();
            while ((n = await stdin.ReadAsync(chunk, ct)) != 0)
            { if (bytes.Length + n > 16384) throw new InvalidOperationException(); bytes.Write(chunk, 0, n); }
            input = JsonSerializer.Deserialize<Input>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), new JsonSerializerOptions {
                MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException();
            var directory = System.Environment.GetEnvironmentVariable("TRADEOPS_BOUNDED_OMS_DIRECTORY") ?? throw new InvalidOperationException();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RunAsync(input, http, directory, ct);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result.Status is "OMS_SCENARIO_PASS" or "OMS_SCENARIO_SKIPPED" ? 0 : 1;
        }
        catch
        {
            Console.WriteLine(JsonSerializer.Serialize(Empty("OMS_SCENARIO_REFUSED", "Input")));
            return 1;
        }
        finally
        { if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; } CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(bytes.GetBuffer()); }
    }

    internal static async Task<Diagnostics> RunAsync(Input input, HttpClient http, string directory, CancellationToken ct,
        Func<CancellationToken, Task<BoundedOmsStore>>? openStore = null,
        Func<DeribitOptions, DeribitPrivateWebSocketStream>? createStream = null)
    {
        var stage = "Input"; var database = false; var persistent = false; int? spent = null; var placements = 0; var cancellations = 0;
        var receipts = 0; var open = false; var closed = false; var fill = false; var mayRemain = false; var baseline = false;
        var validated = false; var assessed = false; var issues = 0; IReadOnlyList<string> blockers = [];
        try
        {
            input.Validate();
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Permanent qualification directory required.");
            Directory.CreateDirectory(directory);
            using var local = new FileStream(Path.Combine(directory, "owner-run.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            stage = "Store";
            database = true;
            await using var store = await (openStore?.Invoke(ct) ?? BoundedOmsStore.OpenExistingAsync(ct)); persistent = store.Persistent;
            var scope = RecoveryStore.Scope(input.ExpectedAccountId, input.AccountType);
            var reader = new OmsPostgresRecoveryLedgerReader(store.Reader);
            var privateReader = new OmsPostgresPrivateRecoveryReader(store.Reader);
            await using var admission = await OmsPostgresBoundedQualificationLease.AcquireAsync(store.Journal, reader, scope, ct);
            await using var captureOwner = await OmsPostgresPrivateCaptureOwner.AcquireAsync(store.Capture, scope, ct);
            stage = "Metadata";
            var rpc = new BoundedOmsRpc(http);
            var metadata = (await rpc.CallAsync("public/get_instrument", new { instrument_name = "ETH-PERPETUAL" }, ct)).Result;
            var book = (await rpc.CallAsync("public/get_order_book", new { instrument_name = "ETH-PERPETUAL", depth = 1 }, ct)).Result;
            decimal price;
            try { price = BoundedOmsRiskPolicy.BuyPrice(metadata, book, DateTimeOffset.UtcNow); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
            { return Finish("OMS_SCENARIO_SKIPPED"); }
            stage = "Authentication";
            var sessionName = "session:bounded_oms_" + Guid.NewGuid().ToString("N");
            var auth = (await rpc.CallAsync("public/auth", new { grant_type = "client_credentials", client_id = input.ClientId,
                client_secret = input.ClientSecret, scope = sessionName + " expires:300 trade:read_write account:read wallet:none block_trade:none block_rfq:none" }, ct)).Result;
            TestnetOrderQualification.ValidateGrant(auth.GetProperty("scope").GetString()!, sessionName);
            if (auth.GetProperty("expires_in").GetInt32() is < 150 or > 300) throw new InvalidOperationException();
            rpc.Token = auth.GetProperty("access_token").GetString() ?? throw new InvalidOperationException();
            await Identity();
            await NoOpenOrders();
            var before = PositionFingerprint((await rpc.CallAsync("private/get_positions", new { currency = "ETH" }, ct)).Result, input.ExpectedAccountId);
            var native = new DeribitPrivateInstrument("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD");
            var plan = new DeribitPrivateCapturePlan(input.ExpectedAccountId, input.AccountType == "subaccount", [native]);
            var options = new DeribitOptions { WebSocketUrl = "wss://www.deribit.com/ws/api/v2", ClientId = input.ClientId, ClientSecret = input.ClientSecret };
            var captureMonitor = new BoundedOmsCapture(captureOwner, scope);
            await using var coordinator = new OmsPrivateCaptureCoordinator(captureMonitor);
            var session = await coordinator.SwitchAsync(scope, ct);
            var diagnostics = new DeribitPrivateCaptureDiagnostics();
            var stream = createStream?.Invoke(options) ?? new(options, NullLogger<DeribitPrivateWebSocketStream>.Instance);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = stream.RunCaptureAsync(coordinator, session, plan, stop.Token, diagnostics);
            try
            {
                stage = "Capture";
                await Wait(async () => { await Task.CompletedTask; return diagnostics.Snapshot.SubscriptionsConfirmed == 1; });
                var epoch = coordinator.Status?.Epoch ?? Guid.Empty;
                if (epoch == Guid.Empty) throw new InvalidOperationException();
                stage = "Preflight";
                await Identity(); await NoOpenOrders();
                metadata = (await rpc.CallAsync("public/get_instrument", new { instrument_name = "ETH-PERPETUAL" }, ct)).Result;
                book = (await rpc.CallAsync("public/get_order_book", new { instrument_name = "ETH-PERPETUAL", depth = 1 }, ct)).Result;
                try { price = BoundedOmsRiskPolicy.BuyPrice(metadata, book, DateTimeOffset.UtcNow); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
                { return Finish("OMS_SCENARIO_SKIPPED"); }
                var localId = new OmsLocalOrderId(Guid.NewGuid()); var intent = new OrderIntentId(Guid.NewGuid());
                var label = "to-oms-" + Guid.NewGuid().ToString("N");
                captureMonitor.TrackLabel(label);
                var place = new OmsJournalCommandRequest(scope, localId, intent, new(Guid.NewGuid()), new(0),
                    new(OmsJournalCommandKind.Place, "ETH-PERPETUAL", OrderSide.Buy, OrderType.Limit, 1, OmsQuantityUnit.QuoteAsset,
                        price, null, "good_til_cancelled", false, true, DeribitBoundedQualificationPolicy.Provenance(input.StageId),
                        DeribitBoundedQualificationPolicy.Operator, "bounded-usd1-distance10-v1", DateTimeOffset.UtcNow, TargetClientOrderId: label));
                var journal = new OmsPostgresJournal(store.Journal);
                var receiptJournal = new OmsPostgresDeribitCommandReceiptJournal(store.Journal);
                stage = "Admission"; spent = await admission.ReserveAsync(input.StageId, place, ct);
                stage = "Placement";
                var accepted = await Send(place);
                mayRemain = accepted.Order.OrderState == "open";
                if (accepted.Order.FilledNativeAmount != 0) { fill = true; throw new InvalidOperationException(); }
                if (accepted.Order.OrderState != "open") throw new InvalidOperationException();
                mayRemain = true;
                stage = "OpenCapture"; await Wait(() => Observed("open", accepted.Order.ExchangeOrderId)); open = true;
                stage = "BeforeCancel";
                var current = await ReadState(accepted.Order.ExchangeOrderId);
                Check(current.Facts, accepted.Order.ExchangeOrderId);
                if (current.Facts.OrderState != "open") throw new InvalidOperationException();
                var cancel = place with { CommandId = new(Guid.NewGuid()), ExpectedAggregateVersion = new(1), Payload = place.Payload with {
                    Kind = OmsJournalCommandKind.Cancel, RequestedAtUtc = DateTimeOffset.UtcNow, TargetExchangeOrderId = accepted.Order.ExchangeOrderId } };
                if (await journal.PersistAsync(cancel, cancellationToken: ct) != OmsJournalWriteResult.Created) throw new InvalidOperationException();
                stage = "Cancellation"; var cancelled = await Send(cancel);
                mayRemain = cancelled.Order.OrderState != "cancelled";
                Check(cancelled.Order, accepted.Order.ExchangeOrderId);
                if (cancelled.Order.OrderState != "cancelled") throw new InvalidOperationException(); mayRemain = false;
                stage = "CancelCapture"; await Wait(() => Observed("cancelled", accepted.Order.ExchangeOrderId)); closed = true;
                stage = "TerminalRead";
                current = await ReadState(accepted.Order.ExchangeOrderId); Check(current.Facts, accepted.Order.ExchangeOrderId);
                if (current.Facts.OrderState != "cancelled") throw new InvalidOperationException();
                foreach (var historical in new[] { false, true })
                {
                    var reply = await rpc.CallAsync("private/get_user_trades_by_order", new { order_id = accepted.Order.ExchangeOrderId, sorting = "asc", historical }, ct);
                    var trades = DeribitAddressedQualificationParser.Trades(reply.Result, native, scope, accepted.Order.ExchangeOrderId, epoch, reply.ReceivedAtUtc);
                    foreach (var trade in trades) await captureOwner.CaptureAsync(trade, ct);
                    if (trades.Count != 0) { fill = true; throw new InvalidOperationException(); }
                }
                await Identity(); await NoOpenOrders();
                baseline = before == PositionFingerprint((await rpc.CallAsync("private/get_positions", new { currency = "ETH" }, ct)).Result, input.ExpectedAccountId);
                if (!baseline) throw new InvalidOperationException();
                stage = "Recovery";
                var binding = new DeribitPrivateCaptureBinding(scope, options, plan);
                var now = DateTimeOffset.UtcNow;
                var gate = new DeribitRecoveryEvidenceGate(privateReader, new DeribitRecoveryQueries(binding, http), new DeribitKnownOrderReader(binding, http));
                var assessment = await gate.AssessWithLedgerAsync(new(new(scope, epoch, "ETH-PERPETUAL", now.AddMinutes(-10), now,
                    TimeSpan.FromMinutes(2), MaximumPagesPerSource: 5, MaximumFacts: 2000), "ETH", OmsPrivateAmountUnit.Usd, "USD"), reader, ct);
                assessed = true; issues = assessment.CommandIssues.Count; blockers = assessment.Orders.Blockers.Select(x => x.ToString()).ToArray();
                validated = assessment.BoundCommands >= 2 && !assessment.CommandIssues.Any(x => x.CommandId == place.CommandId || x.CommandId == cancel.CommandId) &&
                    assessment.Orders.Blockers.All(x => x is DeribitRecoveryEvidenceBlocker.HistoricalUnfilledCoverageGap or
                        DeribitRecoveryEvidenceBlocker.OrderHistoryIncomplete or DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable or
                        DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable or DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven or
                        DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved);
                if (!validated) throw new InvalidOperationException();
                current = await ReadState(accepted.Order.ExchangeOrderId); Check(current.Facts, accepted.Order.ExchangeOrderId);
                if (current.Facts.OrderState != "cancelled") throw new InvalidOperationException();
                if (!await Observed("cancelled", accepted.Order.ExchangeOrderId)) throw new InvalidOperationException();
                baseline = before == PositionFingerprint((await rpc.CallAsync("private/get_positions", new { currency = "ETH" }, ct)).Result, input.ExpectedAccountId);
                if (!baseline) throw new InvalidOperationException();
                Healthy(); stage = "Completed"; return Finish("OMS_SCENARIO_PASS");

                async Task<DeribitJsonRpcCommandReceipt> Send(OmsJournalCommandRequest command)
                {
                    await admission.CheckAsync(ct); Healthy();
                    if (command.Payload.Kind == OmsJournalCommandKind.Cancel &&
                        !await Observed("open", command.Payload.TargetExchangeOrderId!)) throw new InvalidOperationException();
                    await Identity();
                    if (await journal.MarkDispatchStartedAsync(scope, command.CommandId, cancellationToken: ct) != OmsJournalAttemptResult.Started)
                        throw new InvalidOperationException();
                    try
                    {
                        await admission.CheckAsync(ct); Healthy();
                        BoundedOmsRpc.Reply reply;
                        if (command.Payload.Kind == OmsJournalCommandKind.Place)
                        {
                            var freshMetadata = (await rpc.CallAsync("public/get_instrument", new { instrument_name = "ETH-PERPETUAL" }, ct)).Result;
                            var freshBook = (await rpc.CallAsync("public/get_order_book", new { instrument_name = "ETH-PERPETUAL", depth = 1 }, ct)).Result;
                            var ceiling = BoundedOmsRiskPolicy.BuyPrice(freshMetadata, freshBook, DateTimeOffset.UtcNow);
                            if (price > ceiling || price % freshMetadata.GetProperty("tick_size").GetDecimal() != 0) throw new InvalidOperationException();
                            var time = (await rpc.CallAsync("public/get_time", new { }, ct)).Result.GetInt64();
                            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - time) > 5000) throw new InvalidOperationException();
                            await admission.CheckAsync(ct); Healthy();
                            // Clock/lease checks can take time: enforce quote age again at the send boundary.
                            if (price > BoundedOmsRiskPolicy.BuyPrice(freshMetadata, freshBook, DateTimeOffset.UtcNow))
                                throw new InvalidOperationException();
                            placements++; mayRemain = true;
                            reply = await rpc.CallAsync("private/buy", new { instrument_name = "ETH-PERPETUAL", amount = 1m, type = "limit", price,
                                time_in_force = "good_til_cancelled", post_only = true, reject_post_only = true, label, valid_until = time + 5000 }, ct);
                        }
                        else { cancellations++; reply = await rpc.CallAsync("private/cancel", new { order_id = command.Payload.TargetExchangeOrderId }, ct); }
                        var receipt = DeribitJsonRpcCommandReceiptParser.Parse(command, native, reply.SessionId, reply.Id, reply.Method,
                            reply.SentAtUtc, reply.ReceivedAtUtc, reply.Root);
                        captureMonitor.TrackNativeId(receipt.Order.ExchangeOrderId);
                        fill |= receipt.Order.FilledNativeAmount is > 0;
                        mayRemain = receipt.Order.OrderState == "open";
                        if (await receiptJournal.RecordAsync(receipt, ct) is not (DeribitCommandReceiptWriteResult.Inserted or DeribitCommandReceiptWriteResult.Duplicate))
                            throw new InvalidOperationException();
                        receipts++;
                        if (!await journal.RecordOutcomeAsync(scope, command.CommandId, OmsJournalDispatchOutcome.Acknowledged, ct)) throw new InvalidOperationException();
                        return receipt;
                    }
                    catch
                    {
                        using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await journal.RecordOutcomeAsync(scope, command.CommandId, OmsJournalDispatchOutcome.Ambiguous, save.Token); } catch { }
                        throw; // No send retry, cancel-by-label, finally-cancel or flatten.
                    }
                }
                async Task<OmsPrivateEventEnvelope> ReadState(string id)
                {
                    var reply = await rpc.CallAsync("private/get_order_state", new { order_id = id }, ct);
                    var envelope = DeribitAddressedQualificationParser.Order(reply.Result, native, scope, id, epoch, reply.ReceivedAtUtc);
                    await captureOwner.CaptureAsync(envelope, ct); return envelope;
                }
                void Check(OmsPrivateVenueFacts facts, string id)
                {
                    if (facts.FilledNativeAmount is > 0) { fill = true; throw new InvalidOperationException(); }
                    if (facts.ExchangeOrderId != id || facts.NativeInstrument != "ETH-PERPETUAL" || facts.Side != OrderSide.Buy ||
                        facts.NativeAmount != 1 || facts.Price != price || facts.FilledNativeAmount != 0 || facts.ClientLabel != label)
                        throw new InvalidOperationException();
                }
                async Task<bool> Observed(string state, string id)
                {
                    var evidence = await privateReader.ReadAsync(scope, cancellationToken: ct);
                    if (!evidence.IsUsable) throw new InvalidOperationException();
                    var facts = evidence.Observations.Where(x => x.Envelope.Facts.ExchangeOrderId == id && x.Envelope.Facts.Currency == "ETH").ToArray();
                    if (facts.Any(x => x.Envelope.Facts.Kind == OmsJournalEventKind.Execution || x.Envelope.Facts.FilledNativeAmount is > 0))
                    { fill = true; throw new InvalidOperationException(); }
                    return facts.Any(x => x.Envelope.Observation.Source == OmsPrivateCaptureSource.WebSocket && x.Envelope.Observation.StreamEpoch == epoch &&
                        x.Envelope.Facts.OrderState == state && x.Envelope.Facts.FilledNativeAmount == 0);
                }
            }
            finally
            {
                stop.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { if (stage == "Completed") throw new InvalidOperationException("Capture shutdown unproven."); }
            }
            void Healthy()
            {
                if (captureMonitor.FillObserved) { fill = true; throw new InvalidOperationException(); }
                var d = diagnostics.Snapshot;
                if (running.IsCompleted || d.Problems != 0 || d.Epochs != 1 || d.AccountsVerified != 1 || d.SubscriptionsConfirmed != 1) throw new InvalidOperationException();
            }
            async Task Wait(Func<Task<bool>> ready)
            {
                var watch = Stopwatch.StartNew();
                while (!await ready()) { if (running.IsCompleted || diagnostics.Snapshot.Problems != 0 || watch.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException(); await Task.Delay(150, ct); }
            }
            async Task Identity()
            {
                var account = (await rpc.CallAsync("private/get_account_summary", new { currency = "ETH", extended = true }, ct)).Result;
                if (account.GetProperty("id").GetInt64() != input.ExpectedAccountId || account.GetProperty("type").GetString() != input.AccountType ||
                    account.GetProperty("total_equity_usd").GetDecimal() is <= 0 or > 20 ||
                    account.GetProperty("total_margin_balance_usd").GetDecimal() - account.GetProperty("total_initial_margin_usd").GetDecimal() < 1)
                    throw new InvalidOperationException();
            }
            async Task NoOpenOrders()
            { var orders = (await rpc.CallAsync("private/get_open_orders_by_currency", new { currency = "ETH" }, ct)).Result; if (orders.ValueKind != JsonValueKind.Array || orders.GetArrayLength() != 0) throw new InvalidOperationException(); }
        }
        catch { return Finish(fill ? "OMS_SCENARIO_STOPPED_FILL" : placements != 0 ? "OMS_SCENARIO_UNRESOLVED" : "OMS_SCENARIO_REFUSED"); }
        Diagnostics Finish(string status) => new(status, stage, database, persistent, spent, placements, cancellations, receipts,
            open, closed, fill, mayRemain, baseline, validated, assessed, issues, blockers);
    }

    private static Diagnostics Empty(string status, string stage) => new(status, stage, false, false, null, 0, 0, 0,
        false, false, false, false, false, false, false, 0, []);

    internal static string PositionFingerprint(JsonElement positions, long nativeAccount)
    {
        if (positions.ValueKind != JsonValueKind.Array || positions.GetArrayLength() > 256) throw new InvalidOperationException();
        var rows = positions.EnumerateArray().Select(x => new { Name = x.GetProperty("instrument_name").GetString(),
            Side = x.GetProperty("direction").GetString(), Size = x.GetProperty("size").GetDecimal(), Account = x.GetProperty("user_id").GetInt64() }).ToArray();
        if (rows.Any(x => x.Account != nativeAccount || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 160 || x.Name.Any(char.IsControl) ||
            x.Side is not ("buy" or "sell" or "zero")) || rows.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new InvalidOperationException();
        return JsonSerializer.Serialize(rows.OrderBy(x => x.Name, StringComparer.Ordinal));
    }
}
