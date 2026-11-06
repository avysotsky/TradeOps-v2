namespace TradeOps.Application.Oms;

public enum OmsPrivateAccountMutation { OrdersOrTrades = 1, Positions, Portfolio }

public sealed partial class OmsPrivateCaptureCoordinator
{
    private long economicGeneration;

    /// <summary>Invalidate immediately on authenticated account activity outside the order/fill
    /// journal. No fake order events, transport gap, SQL cursor or discarded account changes.</summary>
    public void ObserveAccountMutation(OmsPrivateCaptureSession session, IDisposable runner, Guid expectedEpoch, OmsPrivateAccountMutation mutation)
    {
        if (!Enum.IsDefined(mutation)) throw new ArgumentException("Unknown native account mutation.");
        lock (replaySync)
        {
            RequireActive(session);
            if (epoch != expectedEpoch || expectedEpoch == Guid.Empty || !session.IsCurrentRunner(runner) ||
                epochRunnerGeneration != session.RunnerGeneration || status?.Phase is not (OmsPrivateStreamPhase.Subscribing or OmsPrivateStreamPhase.Recovering))
                throw new InvalidOperationException("Native account activity is not bound to the active capture owner.");
            economicGeneration = checked(economicGeneration + 1);
            replayWindow?.Fail(OmsPrivateReplayProblem.AccountMutation);
        }
    }
}
