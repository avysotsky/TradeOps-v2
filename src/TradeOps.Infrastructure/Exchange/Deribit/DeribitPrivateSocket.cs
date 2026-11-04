using System.Net.WebSockets;
using System.Text;
using TradeOps.Application.Oms;

namespace TradeOps.Infrastructure.Exchange.Deribit;

internal interface IDeribitPrivateSocket : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);
    Task<string> ReceiveAsync(int maximumBytes, CancellationToken ct);
    void Abort();
}

internal sealed class DeribitPrivateSocket : IDeribitPrivateSocket
{
    private readonly ClientWebSocket socket = new();
    public Task ConnectAsync(Uri uri, CancellationToken ct) => socket.ConnectAsync(uri, ct);
    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
        socket.SendAsync(message, WebSocketMessageType.Text, true, ct).AsTask();
    public async Task<string> ReceiveAsync(int maximumBytes, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(16384, maximumBytes + 1)];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > maximumBytes)
                throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        try { return new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, (int)message.Length); }
        catch (DecoderFallbackException) { throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation); }
    }
    public void Abort() => socket.Abort();
    public void Dispose() => socket.Dispose();
}
