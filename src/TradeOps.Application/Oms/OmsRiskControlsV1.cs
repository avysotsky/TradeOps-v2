using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsRiskControlLevel { Global = 1, Venue, Account }
public enum OmsRiskControlKind { KillSwitch = 1, CircuitBreaker }

/// <summary>Global means all tenants/environments. Venue means one tenant/venue/environment.
/// Account includes the optional subaccount. Empty components are canonical, never wildcards.</summary>
public sealed record OmsRiskControlScopeV1(OmsRiskControlLevel Level, string Tenant, string Venue,
    short Environment, string Account, string Subaccount)
{
    public static OmsRiskControlScopeV1 Global { get; } = new(OmsRiskControlLevel.Global, "", "", 0, "", "");
    public static OmsRiskControlScopeV1 ForVenue(AccountKey scope) => new(OmsRiskControlLevel.Venue,
        scope.Tenant.Value, scope.Venue.Value, (short)scope.Environment, "", "");
    public static OmsRiskControlScopeV1 ForAccount(AccountKey scope) => new(OmsRiskControlLevel.Account,
        scope.Tenant.Value, scope.Venue.Value, (short)scope.Environment, scope.Account.Value, scope.Subaccount?.Value ?? "");

    public void Validate()
    {
        if (!Enum.IsDefined(Level) || Tenant is null || Venue is null || Account is null || Subaccount is null ||
            Tenant.Length > 128 || Venue.Length > 64 || Account.Length > 128 || Subaccount.Length > 128 ||
            new[] { Tenant, Venue, Account, Subaccount }.Any(x => x != x.Trim() || x.Any(char.IsControl)) ||
            Level == OmsRiskControlLevel.Global && (Tenant != "" || Venue != "" || Environment != 0 || Account != "" || Subaccount != "") ||
            Level != OmsRiskControlLevel.Global && (Tenant == "" || Venue == "" || Environment is < 1 or > 4) ||
            Level == OmsRiskControlLevel.Venue && (Account != "" || Subaccount != "") ||
            Level == OmsRiskControlLevel.Account && Account == "") throw new ArgumentException("Invalid risk control scope.");
    }
}

public sealed record OmsRiskControlRequestV1(OmsRiskControlScopeV1 Scope, OmsRiskControlKind Kind,
    Guid DecisionId, long ExpectedRevision, bool Blocked, string Reason)
{
    public void Validate()
    {
        Scope.Validate();
        if (!Enum.IsDefined(Kind) || DecisionId == Guid.Empty || ExpectedRevision < 0 ||
            string.IsNullOrWhiteSpace(Reason) || Reason.Length > 1024 || Reason != Reason.Trim() || Reason.Any(char.IsControl))
            throw new ArgumentException("Invalid risk control decision.");
    }
}

public sealed record OmsRiskControlEventV1(int SchemaVersion, OmsRiskControlRequestV1 Request,
    long Revision, string ActorRole, DateTimeOffset RecordedAtUtc);

// Every change, including activate -> clear, invalidates the prior stamp. These are NOT send credentials.
public sealed record OmsRiskControlStampV1(int SchemaVersion, AccountKey Scope,
    long GlobalKill, long GlobalCircuit, long VenueKill, long VenueCircuit, long AccountKill, long AccountCircuit);
