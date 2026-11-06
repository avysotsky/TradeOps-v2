using TradeOps.Application.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

public sealed partial class OmsPostgresRecoveryRunStore
{
    public async Task<OmsRecoveryExternalOrderReview> ReviewAsync(OmsRecoveryExternalOrderReviewRequest request, CancellationToken ct = default)
    {
        if (request.DecisionId == Guid.Empty || request.SourceRunId == Guid.Empty || !Enum.IsDefined(request.Decision) ||
            request.ExpectedEvidenceVersion < 0 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1024 ||
            request.Reason != request.Reason.Trim() || request.Reason.Any(char.IsControl)) throw new ArgumentException("Invalid operator review.");
        var document = await ReadResultAsync(request.Scope, request.SourceRunId, ct) ?? throw new InvalidOperationException("Observed recovery result required.");
        var now = DateTimeOffset.UtcNow;
        if (document.Coherence is null || now < document.Coherence.AssessedAtUtc || now-document.Coherence.AssessedAtUtc > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Fresh operator-review evidence required.");
        var evidence = OmsRecoveryExternalOrderEvidencePolicy.Find(document, request.Currency, request.ExchangeOrderId)
            ?? throw new InvalidOperationException("Explicit native order evidence required.");
        var digest = OmsRecoveryExternalOrderEvidencePolicy.Digest(evidence);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var command = Command("""
SELECT actor_role::text,recorded_at,evidence_version FROM public.oms_append_recovery_external_review_v1(
 @tenant,@venue,@environment,@account,@subaccount,@decision,@run,@currency,@order,@hash,@action,@reason,@version)
""", db, null, request.Scope);
        command.Parameters.AddWithValue("decision", request.DecisionId); command.Parameters.AddWithValue("run", request.SourceRunId);
        command.Parameters.AddWithValue("currency", request.Currency); command.Parameters.AddWithValue("order", request.ExchangeOrderId);
        command.Parameters.AddWithValue("hash", Convert.FromHexString(digest)); command.Parameters.AddWithValue("action", (short)request.Decision);
        command.Parameters.AddWithValue("reason", request.Reason); command.Parameters.AddWithValue("version", request.ExpectedEvidenceVersion);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Operator decision was not committed.");
        return new(1, request.Scope, request.DecisionId, request.SourceRunId, request.Currency, request.ExchangeOrderId, digest,
            request.Decision, rows.GetString(0), request.Reason, new(rows.GetDateTime(1)), rows.GetInt64(2));
    }
}
