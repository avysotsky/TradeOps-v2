using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TradeOps.Connectors.Deribit.Fix;

internal interface IDeribitFixWireV1:IAsyncDisposable
{
    Task WriteAsync(ReadOnlyMemory<byte> frame,CancellationToken ct);
    ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct);
    void Abort();
}

/// <summary>Fixed Deribit TLS endpoints, platform chain/name/revocation validation, no reconnect.</summary>
internal sealed class DeribitFixTlsWireV1:IDeribitFixWireV1
{
    private readonly TcpClient client;
    private readonly SslStream stream;
    private DeribitFixTlsWireV1(TcpClient client,SslStream stream){this.client=client;this.stream=stream;}
    internal static Task<DeribitFixTlsWireV1> OpenAsync(DeribitFixOptionsV1 options,CancellationToken ct)
    {options.Validate();return ConnectAsync(options.Host,options.Port,null,ct);}
    // Test access is internal and loopback-only. Production options expose no trust/endpoint override.
    internal static Task<DeribitFixTlsWireV1> OpenLoopbackForTestAsync(int port,byte[]? certificateSha256,CancellationToken ct)
        =>ConnectAsync("localhost",port,certificateSha256,ct);
    private static async Task<DeribitFixTlsWireV1> ConnectAsync(string host,int port,byte[]? testPin,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var client=new TcpClient(){NoDelay=true};SslStream? tls=null;
        try
        {
            await client.ConnectAsync(host,port,deadline.Token);
            if(testPin is not null)
            {
                if(host!="localhost"||client.Client.RemoteEndPoint is not IPEndPoint ep||testPin.Length!=32)
                    throw new InvalidOperationException("Test TLS pin is restricted to loopback.");
                var address=ep.Address.IsIPv4MappedToIPv6?ep.Address.MapToIPv4():ep.Address;
                if(!IPAddress.IsLoopback(address))throw new InvalidOperationException("Test TLS pin is restricted to loopback.");
                tls=new(client.GetStream(),false,(_,cert,_,errors)=>cert is not null&&
                    (errors&~SslPolicyErrors.RemoteCertificateChainErrors)==SslPolicyErrors.None&&
                    CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()),testPin));
            }
            else tls=new(client.GetStream(),false);
            // Let Schannel/OpenSSL select a supported protocol (Windows 10 has no TLS 1.3).
            // No application/auth bytes are sent until the negotiated minimum is checked.
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=host,EnabledSslProtocols=SslProtocols.None,
                CertificateRevocationCheckMode=X509RevocationMode.Online,AllowRenegotiation=false},deadline.Token);
            if(tls.SslProtocol is not(SslProtocols.Tls12 or SslProtocols.Tls13))throw new AuthenticationException("FIX requires TLS 1.2 or newer.");
            return new(client,tls);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){tls?.Dispose();client.Dispose();throw;}
        catch(Exception error){tls?.Dispose();client.Dispose();throw new InvalidOperationException("FIX TLS connection refused.",error);}
    }
    public Task WriteAsync(ReadOnlyMemory<byte> frame,CancellationToken ct)=>stream.WriteAsync(frame,ct).AsTask();
    public ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct)=>stream.ReadAsync(buffer,ct);
    public void Abort()=>client.Dispose();
    public async ValueTask DisposeAsync(){Abort();await stream.DisposeAsync();}
}
