using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

/// <summary>
/// A purely observational, explicit opt-in bridge between a read-only
/// venue evidence source and the unified decision policy. It intentionally
/// exposes NO database writes, state mutations, order commands or alerts.
/// This is NOT registered in the running API/Worker DI container.
/// </summary>
public sealed class BrokerReconciliationShadowEvaluator(
    IBrokerReconciliationSource source,
    IBrokerReconciliationDecisionEngine decisionEngine)
{
    public async Task<BrokerReconciliationShadowResult> EvaluateAsync(
        BrokerOrderReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // A failing source or failed evidence validation propagates rather
        // than silently becoming "NotFound", "NoChange" or permission
        // to resubmit. This evaluator never persists an observation.
        var evidence = await source.FetchOrderEvidenceAsync(
            context, cancellationToken);

        var decision = decisionEngine.Evaluate(context, evidence);

        return new BrokerReconciliationShadowResult(
            evidence.Availability,
            evidence.ObservationSequence,
            decision);
    }
}

/// <summary>
/// Observation only; Candidate does not imply an executable trade command.
/// </summary>
public sealed record BrokerReconciliationShadowResult(
    BrokerEvidenceAvailability Availability,
    long? ObservationSequence,
    BrokerReconciliationDecision Decision);
