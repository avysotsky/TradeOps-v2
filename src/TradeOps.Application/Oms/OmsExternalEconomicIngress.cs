namespace TradeOps.Application.Oms;

/// <summary>Process-local ingress barrier, independent of WS epochs and receipt ordinals.
/// Completion means framing, engine gap drain and durable native capture have all finished.</summary>
public sealed class OmsExternalEconomicIngress : IDisposable
{
    private readonly OmsPrivateCaptureCoordinator owner;
    internal OmsExternalEconomicIngress(OmsPrivateCaptureCoordinator owner) => this.owner = owner;
    public void Complete() => owner.EndExternalIngress(this, true);
    public void Dispose() => owner.EndExternalIngress(this, false);
}

public sealed partial class OmsPrivateCaptureCoordinator
{
    private readonly HashSet<OmsExternalEconomicIngress> externalIngress = [];

    public OmsExternalEconomicIngress BeginExternalIngress(OmsPrivateCaptureSession session)
    {
        lock (replaySync)
        {
            RequireActive(session);
            if (replaySettings is null) throw new InvalidOperationException("Replay tracking is required for native ingress.");
            economicGeneration = checked(economicGeneration + 1);
            replayWindow?.Fail(OmsPrivateReplayProblem.AccountMutation);
            if (externalIngress.Count >= replaySettings.MaximumPendingReceipts)
            { FailReplay(OmsPrivateReplayProblem.BufferOverflow); throw new InvalidOperationException("External ingress capacity exceeded."); }
            var ticket = new OmsExternalEconomicIngress(this);
            externalIngress.Add(ticket);
            return ticket;
        }
    }

    internal void EndExternalIngress(OmsExternalEconomicIngress ticket, bool committed)
    {
        lock (replaySync)
        {
            if (!externalIngress.Remove(ticket)) return;
            if (!committed) FailReplay(OmsPrivateReplayProblem.CaptureFailed);
        }
    }
}
