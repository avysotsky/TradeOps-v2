using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>No expiring lease or forced takeover. An OS-held exclusive file remains locked until
/// the owning process exits or ALL its physical transports have been aborted and drained.
/// Immutable DB node/root pin refuses other hosts. Multi-host failover is deliberately unsupported.</summary>
internal sealed class OmsSingleHostPhysicalFence : IAsyncDisposable
{
    private readonly FileStream handle;
    private readonly SemaphoreSlim serial = new(1,1);
    private readonly object authoritySync=new();
    private readonly List<Task> sessionWrites=[];
    private int revoked;
    private int waiting;
    public Guid OwnerId { get; } = Guid.NewGuid();
    private OmsSingleHostPhysicalFence(FileStream handle) => this.handle = handle;
    public bool IsCurrent => Volatile.Read(ref revoked) == 0 && !handle.SafeFileHandle.IsClosed;
    public static string NodeSha256()
    {
        string identity;
        if (OperatingSystem.IsWindows())
            identity = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string
                ?? throw new InvalidOperationException("OS machine identity unavailable.");
        else if (OperatingSystem.IsLinux()) identity = File.ReadAllText("/etc/machine-id").Trim();
        else throw new PlatformNotSupportedException("Execution ownership supports provisioned Windows/Linux hosts only.");
        if (string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException("OS machine identity empty.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.OSVersion.Platform + ":" + identity)));
    }
    public static string CanonicalRoot(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (DirectoryInfo? d = new(full); d is not null; d = d.Parent)
            if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Provisioned local non-link lock root required.");
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
    public static async Task<OmsSingleHostPhysicalFence> AcquireAsync(NpgsqlDataSource source, AccountKey scope, string root, CancellationToken ct = default)
    {
        root = CanonicalRoot(root); var node = NodeSha256();
        var rootHash=OmsExecutionNodeProvisioning.RootIdentity(root);
        await using var q = source.CreateCommand("SELECT schema_version,node_sha256,lock_root,root_sha256,lock_sha256 FROM oms_execution_bindings_v2 WHERE venue_id=@v AND environment=@e");
        q.Parameters.AddWithValue("v",scope.Venue.Value); q.Parameters.AddWithValue("e",(short)scope.Environment);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.GetInt32(0) != 2 || r.GetString(1) != node || r.GetString(2) != root||r.GetString(3)!=rootHash)
            throw new InvalidOperationException("Provisioned execution node/root binding required; cross-host takeover refused.");
        var path = OmsExecutionNodeProvisioning.LockPath(root,scope);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked owner lock refused.");
        var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        try
        {
            if(file.Length!=0||OmsExecutionNodeProvisioning.FileIdentity(file.SafeFileHandle)!=r.GetString(4)||OmsExecutionNodeProvisioning.RootIdentity(root)!=rootHash)
                throw new InvalidOperationException("Pinned physical fence object changed; takeover refused.");
            return new(file);
        }
        catch{await file.DisposeAsync();throw;}
    }
    internal async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if(Interlocked.Increment(ref waiting)>128){Interlocked.Decrement(ref waiting);throw new InvalidOperationException("Physical sender capacity exceeded.");}
        try
        {
            await serial.WaitAsync(ct);
            try { if (!IsCurrent) throw new InvalidOperationException("Physical owner revoked."); return await operation(); }
            finally { serial.Release(); }
        }
        finally {Interlocked.Decrement(ref waiting);}
    }
    internal OmsPhysicalSessionAuthority CreateSessionAuthority()=>new(write=>
    {
        lock(authoritySync)
        {
            if(!IsCurrent)throw new InvalidOperationException("Physical session owner revoked.");
            if(sessionWrites.Count>=128)throw new InvalidOperationException("Physical session write capacity exceeded.");
            var operation=write();sessionWrites.Add(operation);return TrackSessionWriteAsync(operation);
        }
    });
    private async Task TrackSessionWriteAsync(Task operation)
    {
        try{await operation.ConfigureAwait(false);}
        finally{lock(authoritySync)sessionWrites.Remove(operation);}
    }
    internal void Revoke(){lock(authoritySync)Interlocked.Exchange(ref revoked,1);}
    public async ValueTask DisposeAsync()
    {
        Revoke(); await serial.WaitAsync();
        try
        {
            Task[] pending;lock(authoritySync)pending=sessionWrites.ToArray();
            // Even a caller that forgot transport drain cannot hand over while a physical write
            // is still pending. Failed writes are drained too; their caller retains UNKNOWN.
            foreach(var write in pending){try{await write.ConfigureAwait(false);}catch{}}
            await handle.DisposeAsync();
        }
        finally { serial.Release(); }
        // Never unlink: another process must lock the very same inode after handover.
    }
}
