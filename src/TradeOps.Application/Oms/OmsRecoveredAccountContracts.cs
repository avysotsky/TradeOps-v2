using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record OmsRecoveryPublication(OmsRecoveryRunToken Token, long EvidenceVersion, string StateSha256);
public sealed record OmsRecoveredAccountArchive(OmsRecoveryPublication Publication, OmsRecoveredAccountStateV1 State);

public interface IOmsRecoveredAccountStore
{
    Task<OmsRecoveryPublication> PublishAsync(OmsRecoveryRunToken token, DeribitRecoveryRunDocumentV1 document,
        OmsQualifiedRecovery qualified, CancellationToken ct = default);
    Task<OmsRecoveredAccountArchive?> ReadPublishedAsync(AccountKey scope, CancellationToken ct = default);
    Task<bool> IsPublishedCurrentAsync(OmsRecoveryPublication publication, CancellationToken ct = default);
}

/// <summary>Read-only eligibility for consuming a recovered baseline. Neither a risk approval nor
/// a physical-send lease. Restart/deserialization cannot recreate the required live qualification.</summary>
public sealed class OmsRuntimeRecoveryGate(IOmsRecoveredAccountStore store, OmsQualifiedRecovery qualified,
    OmsRecoveryPublication publication)
{
    public async Task<bool> CanUseRecoveredStateAsync(CancellationToken ct = default)
    {
        if (!Bound() || !qualified.IsCurrent) return false;
        var current = await store.IsPublishedCurrentAsync(publication, ct);
        return current && qualified.IsCurrent;
    }

    private bool Bound() => publication.Token.Start == qualified.Start &&
        publication.EvidenceVersion >= qualified.ExpectedEvidenceVersion &&
        publication.StateSha256 == Convert.ToHexString(qualified.StateSha256);
}
