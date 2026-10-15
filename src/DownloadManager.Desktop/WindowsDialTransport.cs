using System.IO;
using System.Runtime.InteropServices;
using DownloadManager.Core;

namespace DownloadManager.Desktop;

// RAS owns credential handling. Passwords are never placed in process arguments.
public sealed class WindowsDialTransport:IDialTransport,IDisposable
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct Parameters{
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=257)]public string Entry;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=129)]public string Phone;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=129)]public string Callback;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=257)]public string User;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=257)]public string Password;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=16)]public string Domain;
        public uint SubEntry;public UIntPtr CallbackId;public uint InterfaceIndex;
    }
    [DllImport("rasapi32.dll",CharSet=CharSet.Unicode)]private static extern uint RasGetEntryDialParams(string? phonebook,ref Parameters parameters,out bool hasPassword);
    [DllImport("rasapi32.dll",CharSet=CharSet.Unicode)]private static extern uint RasDial(IntPtr extensions,string? phonebook,ref Parameters parameters,uint notifierType,IntPtr notifier,ref IntPtr connection);
    [DllImport("rasapi32.dll")]private static extern uint RasHangUp(IntPtr connection);
    private readonly Dictionary<string,IntPtr> _references=[];
    private bool _disposed;
    public async Task ConnectAsync(DialPolicy policy,CancellationToken cancellation)
    {
        policy.Validate();ObjectDisposedException.ThrowIf(_disposed,this);cancellation.ThrowIfCancellationRequested();
        var handle=IntPtr.Zero;var released=0;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var watchdog=new Timer(_=>{var current=Volatile.Read(ref handle);if(deadline.IsCancellationRequested&&current!=IntPtr.Zero&&Interlocked.Exchange(ref released,1)==0)RasHangUp(current);},null,200,200);
        var result=await Task.Run(()=>{
            var parameters=new Parameters{Size=Marshal.SizeOf<Parameters>(),Entry=policy.Name,Phone="",Callback="",User="",Password="",Domain=""};
            var saved=RasGetEntryDialParams(null,ref parameters,out _);
            if(saved!=0&&policy.User.Length==0)return saved;
            if(policy.User.Length>0){parameters.User=policy.User;parameters.Password=policy.Password;}
            parameters.Entry=policy.Name;return RasDial(IntPtr.Zero,null,ref parameters,0,IntPtr.Zero,ref handle);
        },CancellationToken.None).ConfigureAwait(false);
        await watchdog.DisposeAsync();
        if(result!=0||deadline.IsCancellationRequested||_disposed){if(handle!=IntPtr.Zero&&Interlocked.Exchange(ref released,1)==0)RasHangUp(handle);cancellation.ThrowIfCancellationRequested();throw new IOException("RAS "+result);}
        lock(_references){if(_references.TryGetValue(policy.Name,out var previous)){if(previous==handle){RasHangUp(handle);return;}RasHangUp(previous);}_references[policy.Name]=handle;}
    }
    public void Dispose(){_disposed=true;lock(_references){foreach(var handle in _references.Values)RasHangUp(handle);_references.Clear();}}
}
