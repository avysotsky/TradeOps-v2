using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Immutable server pin, never derived from UI account selection.</summary>
public sealed class DeribitPrivateCaptureBinding
{
    public AccountKey Scope { get; }
    public DeribitOptions Options { get; }
    public DeribitPrivateCapturePlan Plan { get; }

    public DeribitPrivateCaptureBinding(AccountKey scope, DeribitOptions options, DeribitPrivateCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var value in new[] { scope.Tenant.Value, scope.Account.Value, scope.Subaccount?.Value })
            if (value is not null && (value.Length > 128 || value.Any(char.IsControl)))
                throw new ArgumentException("Invalid private capture account binding.");
        _ = plan.Validate(options, scope);
        Scope = scope;
        Options = options;
        Plan = plan;
    }
}
