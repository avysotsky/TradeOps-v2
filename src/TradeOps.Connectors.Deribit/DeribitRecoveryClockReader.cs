using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>One fixed-origin public time measurement. It never changes a system clock or authenticates.</summary>
public sealed class DeribitRecoveryClockReader(DeribitPrivateCaptureBinding binding, HttpClient http,
    TimeProvider? timeProvider = null) : IDeribitRecoveryClockReader
{
    public async Task<DeribitRecoveryClockObservation> ReadAsync(DeribitRecoveryClockRequest q, CancellationToken ct = default)
    {
        _ = binding.Plan.Validate(binding.Options, binding.Scope);
        if (q.Scope != binding.Scope || q.Epoch == Guid.Empty || http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new ArgumentException("Invalid native recovery time binding.");
        var time = timeProvider ?? TimeProvider.System; var before = time.GetUtcNow(); DateTimeOffset? native = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var rpc = new DeribitRecoveryRpc(http, q.Scope.Environment);
            native = DateTimeOffset.FromUnixTimeMilliseconds((await rpc.Call("public/get_time", new { }, deadline.Token)).GetInt64());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { native = null; } // No exception text, credentials, retry or inferred venue timestamp.
        return new(q.Scope, q.Epoch, before, time.GetUtcNow(), native);
    }
}
