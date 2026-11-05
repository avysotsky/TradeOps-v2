using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.DeribitMainnetEthQualification;

// A label may conservatively STOP this local qualification; it never authorizes an identity binding.
internal sealed class BoundedOmsCapture(IOmsPrivateEventCapture capture, AccountKey scope) : IOmsPrivateEventCapture
{
    private string? label;
    private string? nativeId;
    private int fill;
    internal bool FillObserved => Volatile.Read(ref fill) != 0;
    internal void TrackLabel(string value) => Volatile.Write(ref label, value);
    internal void TrackNativeId(string value) => Volatile.Write(ref nativeId, value);
    public Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope, CancellationToken ct = default)
    {
        envelope.Validate();
        var f = envelope.Facts;
        if (envelope.Scope == scope && f.Currency == "ETH" &&
            ((Volatile.Read(ref nativeId) is { } id && id == f.ExchangeOrderId) ||
             (Volatile.Read(ref label) is { } expected && expected == f.ClientLabel)) &&
            (f.Kind == OmsJournalEventKind.Execution || f.FilledNativeAmount is > 0))
            Interlocked.Exchange(ref fill, 1);
        return capture.CaptureAsync(envelope, ct);
    }
}
