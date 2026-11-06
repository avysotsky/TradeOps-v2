using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsAccountSubscriptionProfile { OrdersAndTradesV1 = 1, EconomicAccountV2 }

/// <summary>Opaque process-local evidence of the current physical capture owner's subscription ACK.
/// Never deserialize this as runtime authority; it is not a sender lease or a READY decision.</summary>
public sealed class OmsPrivateAccountSubscriptions
{
    internal OmsPrivateCaptureCoordinator Owner { get; }
    internal OmsPrivateCaptureSession Session { get; }
    internal IDisposable Runner { get; }
    internal long RunnerGeneration { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public AccountKey Scope => Session.Scope;
    public Guid Epoch { get; }
    public DateTimeOffset AcknowledgedAtUtc { get; }
    public IReadOnlyList<string> Channels { get; }
    public OmsAccountSubscriptionProfile Profile { get; }
    internal OmsPrivateAccountSubscriptions(OmsPrivateCaptureCoordinator owner, OmsPrivateCaptureSession session,
        IDisposable runner, Guid epoch, DateTimeOffset at, IReadOnlyList<string> channels, OmsAccountSubscriptionProfile profile)
    {
        Owner = owner; Session = session; Runner = runner; RunnerGeneration = session.RunnerGeneration;
        Epoch = epoch; AcknowledgedAtUtc = at; Channels = Array.AsReadOnly(channels.Order(StringComparer.Ordinal).ToArray()); Profile = profile;
    }
}

public sealed partial class OmsPrivateCaptureCoordinator
{
    private OmsPrivateAccountSubscriptions? accountSubscriptions;

    /// <summary>Only the actual current runner can register a verified native ACK while subscribing.
    /// The connector calls this after account pin and exact subscription-response correlation checks.</summary>
    public async Task ConfirmAccountSubscriptionsAsync(OmsPrivateCaptureSession session, IDisposable runner,
        Guid expectedEpoch, IReadOnlyList<string> nativeAcknowledgedChannels, DateTimeOffset acknowledgedAtUtc, CancellationToken ct = default)
    {
        string[] required = ["user.orders.any.any.raw", "user.trades.any.any.100ms"];
        string[] economic = ["user.changes.any.any.raw", "user.orders.any.any.raw", "user.portfolio.any", "user.trades.any.any.100ms"];
        var ordered = nativeAcknowledgedChannels.Order(StringComparer.Ordinal).ToArray();
        var profile = ordered.SequenceEqual(economic) ? OmsAccountSubscriptionProfile.EconomicAccountV2 : OmsAccountSubscriptionProfile.OrdersAndTradesV1;
        if (!(ordered.SequenceEqual(economic) || ordered.SequenceEqual(required)) ||
            acknowledgedAtUtc.Offset != TimeSpan.Zero || acknowledgedAtUtc <= DateTimeOffset.UnixEpoch)
            throw new ArgumentException("Exact account subscription ACK required.");
        await gate.WaitAsync(ct);
        try
        {
            lock (replaySync)
            {
                RequireActive(session);
                var now = replayTime.GetUtcNow();
                if (!ReplayTrackingEnabled || epoch != expectedEpoch || expectedEpoch == Guid.Empty ||
                    !session.IsCurrentRunner(runner) || epochRunnerGeneration != session.RunnerGeneration ||
                    status?.Phase != OmsPrivateStreamPhase.Subscribing || status.Problem != OmsPrivateStreamProblem.AwaitingReconciliation ||
                    accountSubscriptions is not null || replayFault is not null || acknowledgedAtUtc > now.AddSeconds(5) || acknowledgedAtUtc < now.AddSeconds(-5))
                    throw new InvalidOperationException("Subscription ACK does not belong to the active capture owner.");
                accountSubscriptions = new(this, session, runner, epoch, acknowledgedAtUtc, nativeAcknowledgedChannels, profile);
            }
        }
        finally { gate.Release(); }
    }

    public OmsPrivateAccountSubscriptions? ReadAccountSubscriptions(OmsPrivateCaptureSession session, Guid expectedEpoch)
    {
        lock (replaySync)
            return accountSubscriptions is { } proof && proof.Session == session && proof.Epoch == expectedEpoch && Valid(proof) ? proof : null;
    }

    public bool ValidateAccountSubscriptions(OmsPrivateAccountSubscriptions proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        lock (replaySync) return Valid(proof);
    }

    private bool Valid(OmsPrivateAccountSubscriptions proof) => ReferenceEquals(proof.Owner, this) &&
        ReferenceEquals(proof, accountSubscriptions) && IsActive(proof.Session) && epoch == proof.Epoch &&
        proof.Session.IsCurrentRunner(proof.Runner) && proof.RunnerGeneration == epochRunnerGeneration &&
        status?.Phase == OmsPrivateStreamPhase.Recovering && status.Problem == OmsPrivateStreamProblem.AwaitingReconciliation && replayFault is null;
}
