using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

public sealed record DeribitPrivateCaptureDiagnosticSnapshot(int Epochs, int Authenticated,
    int AccountsVerified, int SubscriptionsConfirmed, int HeartbeatChallenges, int HeartbeatReplies,
    int NormalizedEvents, int CaptureReturns, int Problems, OmsPrivateStreamProblem? LastProblem);

/// <summary>Aggregate protocol evidence only: no scope, IDs, frames, keys, tokens or venue payloads.</summary>
public sealed class DeribitPrivateCaptureDiagnostics
{
    private readonly object gate = new();
    private int epochs, authenticated, accounts, subscriptions, challenges, replies, events, returns, problems;
    private OmsPrivateStreamProblem? lastProblem;
    public DeribitPrivateCaptureDiagnosticSnapshot Snapshot
    {
        get { lock (gate) return new(epochs, authenticated, accounts, subscriptions, challenges, replies,
            events, returns, problems, lastProblem); }
    }
    internal void Epoch() { lock (gate) epochs++; }
    internal void Authenticated() { lock (gate) authenticated++; }
    internal void AccountVerified() { lock (gate) accounts++; }
    internal void Subscribed() { lock (gate) subscriptions++; }
    internal void Challenge() { lock (gate) challenges++; }
    internal void Reply() { lock (gate) replies++; }
    internal void Event() { lock (gate) events++; }
    internal void CaptureReturned() { lock (gate) returns++; }
    internal void Problem(OmsPrivateStreamProblem problem) { lock (gate) { problems++; lastProblem = problem; } }
}
