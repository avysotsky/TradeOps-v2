using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitFixTlsTests
{
    [Theory]
    [InlineData("valid-pin")][InlineData("untrusted")][InlineData("wrong-name")][InlineData("wrong-pin")]
    public async Task ActualTlsValidatesCertificateAndNameBeforeAnyFixData(string variant)
    {
        using var key=RSA.Create(2048);var subject=variant=="wrong-name"?"elsewhere.invalid":"localhost";
        var request=new CertificateRequest("CN="+subject,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName(subject);request.CertificateExtensions.Add(san.Build());
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
        // Schannel cannot use an ephemeral server private key. Reimport a temporary owned
        // key (without PersistKeySet); disposing this certificate deletes its key container.
        // Nothing is installed in a certificate/trust store and no OS setting is changed.
        using var certificate=LoadTemporaryServerCertificate(generated);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;var received=0;Exception? serverFailure=null;
        var server=Task.Run(async()=>
        {
            try
            {
                using var tcp=await listener.AcceptTcpClientAsync(stop.Token);await using var tls=new SslStream(tcp.GetStream(),false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate,EnabledSslProtocols=SslProtocols.None},stop.Token);
                var b=new byte[16];
                while(received<b.Length){var n=await tls.ReadAsync(b.AsMemory(received),stop.Token);if(n==0)break;received+=n;}
                if(received>0)await tls.WriteAsync(b.AsMemory(0,received),stop.Token);
            }
            catch(Exception e)when(e is IOException or AuthenticationException or OperationCanceledException){serverFailure=e;}
        },stop.Token);
        try
        {
            var pin=variant=="untrusted"?null:SHA256.HashData(certificate.RawData);if(variant=="wrong-pin")pin=new byte[32];
            if(variant=="valid-pin")
            {
                DeribitFixTlsWireV1 connected;
                try{connected=await DeribitFixTlsWireV1.OpenLoopbackForTestAsync(port,pin,stop.Token);}
                catch(InvalidOperationException error)
                {
                    await server.WaitAsync(stop.Token);
                    throw new Xunit.Sdk.XunitException("Loopback TLS client: "+error.InnerException?.ToString()+"; server: "+serverFailure?.ToString());
                }
                await using var wire=connected;
                var data=Encoding.ASCII.GetBytes("FIX-TLS-LOOPBACK");await wire.WriteAsync(data,stop.Token);var response=new byte[data.Length];var length=0;
                while(length<response.Length){var n=await wire.ReadAsync(response.AsMemory(length),stop.Token);if(n==0)throw new EndOfStreamException();length+=n;}
                Assert.Equal(data,response);
            }
            else await Assert.ThrowsAsync<InvalidOperationException>(()=>DeribitFixTlsWireV1.OpenLoopbackForTestAsync(port,pin,stop.Token));
            await server.WaitAsync(stop.Token);Assert.Equal(variant=="valid-pin"?16:0,received);
        }
        finally{stop.Cancel();listener.Stop();try{await server;}catch(OperationCanceledException){}}
    }
    private static X509Certificate2 LoadTemporaryServerCertificate(X509Certificate2 generated)
    {
        var pfx=generated.Export(X509ContentType.Pkcs12);
        try{return X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.DefaultKeySet);}
        finally{CryptographicOperations.ZeroMemory(pfx);}
    }
}
