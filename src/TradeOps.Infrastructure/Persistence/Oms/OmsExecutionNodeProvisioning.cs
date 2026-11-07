using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using TradeOps.Domain.Oms;
namespace TradeOps.Infrastructure.Persistence.Oms;

public sealed record OmsExecutionNodeDescriptorV2(int SchemaVersion,string NodeSha256,string LockRoot,string RootSha256,string LockSha256);

/// <summary>Explicit operator aid: prepares a local empty fence file, never database rights or trading authority.</summary>
public static class OmsExecutionNodeProvisioning
{
    /// <summary>Read existing object identities only. Never creates a file or grants ownership.</summary>
    public static OmsExecutionNodeDescriptorV2 Inspect(string root,AccountKey scope)
    {
        root=OmsSingleHostPhysicalFence.CanonicalRoot(root);var rootHash=RootIdentity(root);var path=LockPath(root,scope);
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Linked fence refused.");
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        if(file.Length!=0||RootIdentity(root)!=rootHash||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("Pinned empty fence object required.");
        return new(2,OmsSingleHostPhysicalFence.NodeSha256(),root,rootHash,FileIdentity(file.SafeFileHandle));
    }
    public static OmsExecutionNodeDescriptorV2 Prepare(string root,AccountKey scope)
    {
        root=OmsSingleHostPhysicalFence.CanonicalRoot(root);var rootHash=RootIdentity(root);var path=LockPath(root,scope);
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Linked fence refused.");
        using var file=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(file.Length!=0)throw new InvalidOperationException("An empty dedicated fence file is required.");
        var fileHash=FileIdentity(file.SafeFileHandle);
        if(RootIdentity(root)!=rootHash)throw new InvalidOperationException("Fence root changed during inspection.");
        return new(2,OmsSingleHostPhysicalFence.NodeSha256(),root,rootHash,fileHash);
    }
    internal static string LockPath(string root,AccountKey scope)=>Path.Combine(root,Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"oms-physical-owner-v1:{scope.Venue.Value}:{(short)scope.Environment}")))+".lock");
    internal static string RootIdentity(string root)
    {
        if(OperatingSystem.IsWindows())
        {
            if(root.StartsWith(@"\\",StringComparison.Ordinal)||new DriveInfo(Path.GetPathRoot(root)!).DriveType!=DriveType.Fixed)
                throw new InvalidOperationException("Physical fence requires a local fixed filesystem.");
            using var directory=CreateFileW(root,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
            if(directory.IsInvalid)throw new InvalidOperationException("OS directory identity unavailable.");
            return WindowsIdentity(directory);
        }
        if(!OperatingSystem.IsLinux()||IntPtr.Size!=8)throw new PlatformNotSupportedException("Unsupported physical fence filesystem.");
        var fs=new byte[256];if(Statfs(root,fs)!=0)throw new InvalidOperationException("Local filesystem identity unavailable.");
        var type=BinaryPrimitives.ReadInt64LittleEndian(fs);
        if(type is not(0xEF53 or 0x58465342 or 0x9123683E or 0x01021994))
            throw new InvalidOperationException("Physical fence requires local ext4/XFS/Btrfs/tmpfs; remote/overlay filesystems refused.");
        return LinuxIdentity(-100,root,0,true);
    }
    internal static string FileIdentity(SafeFileHandle file)
    {
        if(OperatingSystem.IsWindows())return WindowsIdentity(file);
        if(!OperatingSystem.IsLinux()||IntPtr.Size!=8)throw new PlatformNotSupportedException();
        var held=false;
        try {file.DangerousAddRef(ref held);return LinuxIdentity(file.DangerousGetHandle().ToInt32(),"",0x1000,false);}
        finally{if(held)file.DangerousRelease();}
    }
    private static string LinuxIdentity(int fd,string path,int flags,bool directory)
    {
        var data=new byte[256];
        if(Statx(fd,path,flags,0x7ff,data)!=0||(BinaryPrimitives.ReadUInt32LittleEndian(data)&0x101)!=0x101||
            (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28))&0xf000)!=(directory?0x4000:0x8000))throw new InvalidOperationException("Physical fence object identity unavailable.");
        var identity=new byte[16];data.AsSpan(32,8).CopyTo(identity);data.AsSpan(136,8).CopyTo(identity.AsSpan(8));
        return Convert.ToHexString(SHA256.HashData(identity));
    }
    private static string WindowsIdentity(SafeFileHandle handle)
    {
        var data=new byte[24];if(!GetFileInformationByHandleEx(handle,18,data,(uint)data.Length))throw new InvalidOperationException("OS file identity unavailable.");
        return Convert.ToHexString(SHA256.HashData(data));
    }
    [DllImport("libc",EntryPoint="statx",SetLastError=true)]private static extern int Statx(int fd,[MarshalAs(UnmanagedType.LPUTF8Str)]string path,int flags,uint mask,[Out]byte[] buffer);
    [DllImport("libc",EntryPoint="statfs",SetLastError=true)]private static extern int Statfs([MarshalAs(UnmanagedType.LPUTF8Str)]string path,[Out]byte[] buffer);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int kind,[Out]byte[] data,uint size);
}
