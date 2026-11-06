using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit.Fix;

/// <summary>Immutable supported profile. Credentials are deliberately not part of this record.</summary>
public sealed record DeribitFixOptionsV1(OmsFixSessionKeyV1 Session,int HeartbeatSeconds=30,int MaximumFrameBytes=32768)
{
    public string Host=>Session.Scope.Environment==OmsEnvironment.Mainnet?"fix.deribit.com":"fix-test.deribit.com";
    public int Port=>9883;
    public void Validate()
    {Session.Validate();if(HeartbeatSeconds is <10 or >60||MaximumFrameBytes is <1024 or >32768)throw new ArgumentException("Unsupported FIX session bounds.");}
}

public static class DeribitFixLogonV1
{
    // Timestamp is durably reserved; clock rollback/restart must not reuse an earlier timestamp.
    public static (string RawData,string Password) Sign(long timestamp,ReadOnlySpan<byte> nonce,string secret)
    {
        if(timestamp<=0||nonce.Length is <32 or >512||string.IsNullOrWhiteSpace(secret)||secret.Any(char.IsControl))throw new ArgumentException("Invalid FIX authentication input.");
        var raw=timestamp.ToString(CultureInfo.InvariantCulture)+"."+Convert.ToBase64String(nonce);
        var bytes=Encoding.UTF8.GetBytes(raw+secret);
        try{return(raw,Convert.ToBase64String(SHA256.HashData(bytes)));}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
}

/// <summary>Bounded framing before the engine can queue an out-of-order message. Owns no socket.</summary>
internal sealed class DeribitFixFramerV1(int maximum=32768)
{
    private readonly List<byte> pending=[];
    internal bool HasPending => pending.Count != 0;
    public IReadOnlyList<string> Add(ReadOnlySpan<byte> bytes)
    {
        var frames=new List<string>();
        // Process incrementally, so many short frames in one read do not create an oversized buffer.
        foreach(var b in bytes)
        {
            if(b!=1&&(b<32||b>126))throw new InvalidOperationException("Non-ASCII FIX frame refused.");
            pending.Add(b);if(pending.Count>maximum)throw new InvalidOperationException("FIX frame capacity exceeded.");
            if(pending.Count<16)continue;
            var prefix="8=FIX.4.4\u00019="u8;
            for(var i=0;i<prefix.Length;i++)if(pending[i]!=prefix[i])throw new InvalidOperationException("Invalid FIX prefix.");
            var end=pending.IndexOf(1,prefix.Length);if(end<0){if(pending.Count>22)throw new InvalidOperationException("Invalid FIX length.");continue;}
            var text=Encoding.ASCII.GetString(pending.GetRange(prefix.Length,end-prefix.Length).ToArray());
            if(text.Length is <1 or >5||text.Any(c=>c<'0'||c>'9')||!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var body)||body<5||body>maximum-23)
                throw new InvalidOperationException("Invalid FIX body length.");
            var total=end+1+body+7;
            if(pending.Count<total)continue;
            var trailer=end+1+body;
            if(pending[trailer]!=(byte)'1'||pending[trailer+1]!=(byte)'0'||pending[trailer+2]!=(byte)'='||pending[total-1]!=1)
                throw new InvalidOperationException("Invalid FIX trailer.");
            var checksum=0;for(var i=0;i<trailer;i++)checksum=(checksum+pending[i])%256;
            var check=Encoding.ASCII.GetString(pending.GetRange(trailer+3,3).ToArray());
            if(check.Any(c=>c<'0'||c>'9')||int.Parse(check,CultureInfo.InvariantCulture)!=checksum)throw new InvalidOperationException("Invalid FIX checksum.");
            frames.Add(Encoding.ASCII.GetString(pending.ToArray()));pending.Clear();
            if(frames.Count>256)throw new InvalidOperationException("FIX receive batch capacity exceeded.");
        }
        return frames;
    }
    public void Complete(){if(pending.Count!=0)throw new InvalidOperationException("Truncated FIX stream.");}
}
