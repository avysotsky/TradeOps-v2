using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;

namespace TradeOps.Infrastructure.Exchange;

/// <summary>
/// One-shot, account-free, process-local synthetic runtime check of the
/// broker-neutral reconciliation decision path. It does NOT inspect live
/// orders, send requests, access databases, or represent broker evidence.
/// </summary>
public static class OfflineReconciliationShadowCanary
{
    private const string Account = "to-v2-18-synthetic-account";
    private const string Coid = "to-v2-18-synthetic-order";
    private const string BrokerOrderId = "to-v2-18-synthetic-broker-order";
    private const string Instrument = "BTCUSDT";
    private const long Sequence = 19L;
    private const decimal Quantity = 5m;

    public static async Task<OfflineReconciliationShadowCanaryResult> RunAsync(
        IBrokerReconciliationDecisionEngine engine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        cancellationToken.ThrowIfCancellationRequested();

        // Mock is a frozen synthetic fixture, NOT the live MockExchangeClient
        // whose GetOrderAsync currently advances fills as a side effect.
        var executions = new[]
        {
            new NormalizedBrokerExecutionEvidence("shadow-fill-1", Account,
                Coid, BrokerOrderId, 2m),
            new NormalizedBrokerExecutionEvidence("shadow-fill-2", Account,
                Coid, BrokerOrderId, 3m)
        };
        var mockOrder = new NormalizedBrokerOrderEvidence(
            Account, Coid, BrokerOrderId, "MOCK:BTCUSDT",
            OrderSide.Buy, Quantity, Quantity, OrderStatus.Filled, executions);
        var mockFull = new MockBrokerReconciliationSource(
            new MockBrokerEvidenceSnapshot(Account, Sequence, true, true, [mockOrder]));

        // Verified synthetic cursor continuation, never an IBKR native cursor.
        var collector = new IbkrOfflineEvidencePageCollector(
            Account, "to-v2-18-synthetic-collection", Sequence);
        collector.Append(new IbkrOfflineEvidencePage(
            Account, "to-v2-18-synthetic-collection", Sequence,
            0, null, "shadow-page-2", false, []));
        collector.Append(new IbkrOfflineEvidencePage(
            Account, "to-v2-18-synthetic-collection", Sequence,
            1, "shadow-page-2", null, true,
            [new IbkrOfflineBrokerOrderEvidence(
                Account, Coid, BrokerOrderId, Instrument,
                OrderSide.Buy, Quantity, Quantity,
                IbkrOfflineBrokerOrderStatus.Filled, true,
                [
                    new IbkrOfflineExecutionEvidence("shadow-fill-1",
                        Account, Coid, BrokerOrderId, 2m),
                    new IbkrOfflineExecutionEvidence("shadow-fill-2",
                        Account, Coid, BrokerOrderId, 3m)
                ])]));
        var ibkrFull = new IbkrOfflineBrokerReconciliationSource(collector);

        // Each provider traverses the SAME Application engine, with
        // different provider-specific source adapters.
        var mockContext = Context(
            MockBrokerReconciliationSource.ProviderKey, "MOCK:BTCUSDT");
        var ibkrContext = Context(
            IbkrOfflineBrokerReconciliationSource.ProviderKey,
            IbkrOfflineBrokerReconciliationSource.MakeSyntheticInstrumentKey(Instrument));

        var mockCandidate = await EvaluateAsync(mockFull, mockContext, engine,
            cancellationToken);
        var ibkrCandidate = await EvaluateAsync(ibkrFull, ibkrContext, engine,
            cancellationToken);

        if (mockCandidate.Availability != BrokerEvidenceAvailability.Complete
            || ibkrCandidate.Availability != BrokerEvidenceAvailability.Complete
            || mockCandidate.Decision.Kind != BrokerReconciliationDecisionKind.Candidate
            || ibkrCandidate.Decision.Kind != mockCandidate.Decision.Kind
            || mockCandidate.Decision.CandidateStatus != OrderStatus.Filled
            || mockCandidate.Decision.CandidateStatus != ibkrCandidate.Decision.CandidateStatus
            || mockCandidate.Decision.CandidateFilledQuantity != Quantity
            || mockCandidate.Decision.CandidateFilledQuantity
                != ibkrCandidate.Decision.CandidateFilledQuantity)
        {
            throw CanaryFailed();
        }

        var mockMissing = await EvaluateAsync(
            new MockBrokerReconciliationSource(
                new MockBrokerEvidenceSnapshot(Account, Sequence, true, false, [])),
            mockContext, engine, cancellationToken);

        var emptyComplete = new IbkrOfflineEvidencePageCollector(
            Account, "to-v2-18-empty-collection", Sequence);
        emptyComplete.Append(new IbkrOfflineEvidencePage(
            Account, "to-v2-18-empty-collection", Sequence,
            0, null, null, true, []));
        var ibkrMissing = await EvaluateAsync(
            new IbkrOfflineBrokerReconciliationSource(emptyComplete),
            ibkrContext, engine, cancellationToken);

        if (mockMissing.Availability != BrokerEvidenceAvailability.NotFound
            || ibkrMissing.Availability != BrokerEvidenceAvailability.NotFound
            || mockMissing.Decision.Kind != BrokerReconciliationDecisionKind.Unresolved
            || ibkrMissing.Decision.Kind != BrokerReconciliationDecisionKind.Unresolved
            || mockMissing.Decision.CandidateStatus is not null
            || ibkrMissing.Decision.CandidateStatus is not null)
        {
            throw CanaryFailed();
        }

        var mockPartial = await EvaluateAsync(
            new MockBrokerReconciliationSource(
                new MockBrokerEvidenceSnapshot(Account, Sequence, false, false, [])),
            mockContext, engine, cancellationToken);

        var interrupted = new IbkrOfflineEvidencePageCollector(
            Account, "to-v2-18-interrupted-collection", Sequence);
        interrupted.Append(new IbkrOfflineEvidencePage(
            Account, "to-v2-18-interrupted-collection", Sequence,
            0, null, "missing-terminal", false, []));
        var ibkrPartial = await EvaluateAsync(
            new IbkrOfflineBrokerReconciliationSource(interrupted),
            ibkrContext, engine, cancellationToken);

        if (mockPartial.Availability != BrokerEvidenceAvailability.Incomplete
            || ibkrPartial.Availability != BrokerEvidenceAvailability.Incomplete
            || mockPartial.Decision.Kind != BrokerReconciliationDecisionKind.Unresolved
            || ibkrPartial.Decision.Kind != BrokerReconciliationDecisionKind.Unresolved
            || mockPartial.Decision.CandidateStatus is not null
            || ibkrPartial.Decision.CandidateStatus is not null)
        {
            throw CanaryFailed();
        }

        return new OfflineReconciliationShadowCanaryResult(
            Providers: 2,
            Evaluations: 6,
            Candidates: 2,
            Unresolved: 4,
            DatabaseWrites: 0,
            BrokerRequests: 0);
    }

    private static BrokerOrderReconciliationContext Context(
        string provider, string instrument) =>
        new(provider, Account, Coid, null, instrument,
            OrderSide.Buy, Quantity, OrderStatus.Unknown, 0m, 0L);

    private static async Task<BrokerReconciliationShadowResult> EvaluateAsync(
        IBrokerReconciliationSource source,
        BrokerOrderReconciliationContext local,
        IBrokerReconciliationDecisionEngine engine,
        CancellationToken cancellationToken) =>
        await new BrokerReconciliationShadowEvaluator(source, engine)
            .EvaluateAsync(local, cancellationToken);

    private static InvalidOperationException CanaryFailed() =>
        new("Synthetic shadow reconciliation invariant failed; no mutation performed.");
}

public sealed record OfflineReconciliationShadowCanaryResult(
    int Providers,
    int Evaluations,
    int Candidates,
    int Unresolved,
    int DatabaseWrites,
    int BrokerRequests);
