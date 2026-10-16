using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DownloadManager.Core;
using Microsoft.Win32;

namespace DownloadManager.Desktop;

// Constructed only in interactive mode; never by preview rendering or tests.
public sealed class DesktopIntegration : IDisposable, IDownloadTraySink
{
    private readonly App _app;
    private readonly Window _window;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly Dictionary<string,DownloadState> _states=[];
    private readonly Dictionary<string,bool> _queues=[];
    private sealed class FileIcon{public IconData Data;public DownloadState State;public int Percent;}
    private readonly Dictionary<uint,FileIcon> _fileIcons=[];
    private readonly HashSet<ProgressWindow> _watchedProgress=[];
    private readonly DownloadTrayRegistry _downloadTray;
    private IconData _icon;
    private bool _added,_busy,_disposed;
    private IReadOnlyList<DownloadJob> _latestJobs=[];
    public bool CanHideProgress => _added && Enabled("progressMinimizeToTray");
    private readonly uint _taskbarCreated;
    private IntPtr _wakeTimer;
    private DateTimeOffset? _wakeAt;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct IconData {
        public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message,ref IconData data);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern uint ExtractIconEx(string file,int index,out IntPtr large,out IntPtr small,uint count);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string message);
    [DllImport("winmm.dll",CharSet=CharSet.Unicode)]private static extern bool PlaySound(string sound,IntPtr module,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWaitableTimer(IntPtr security,bool manual,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr callback,IntPtr argument,bool resume);
    [DllImport("kernel32.dll")]private static extern bool CancelWaitableTimer(IntPtr timer);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    public DesktopIntegration(App app,Window window)
    {
        _app=app;_window=window;
        _source=new HwndSource(new HwndSourceParameters("DownloadManager.Tray"){Width=0,Height=0,WindowStyle=0});_source.AddHook(Messages);
        _downloadTray=new DownloadTrayRegistry(this,id=>{if(_window is MainWindow main)main.ShowDownloadProgress(id);});
        ExtractIconEx(Environment.ProcessPath!,0,out var large,out var small,1);if(large!=IntPtr.Zero)DestroyIcon(large);
        _icon=new(){Size=(uint)Marshal.SizeOf<IconData>(),Window=_source.Handle,Id=1,Flags=1|2|4,Callback=0x802d,Icon=small,Tip=app.Locale.S("ui.download_manager"),Info="",InfoTitle=""};
        _taskbarCreated=RegisterWindowMessage("TaskbarCreated");Update();
        window.StateChanged+=StateChanged;_timer.Tick+=async(_,_)=>await PollAsync();_timer.Start();
    }
    public bool HideMain()
    {
        if(_disposed)return false;
        if(!_added)_added=Shell_NotifyIcon(0,ref _icon);
        if(!_added)return false;
        _window.Hide();return true;
    }
    public bool HideDownload(ProgressWindow window,DownloadJob job)
    {
        if(_disposed||!_downloadTray.Add(job))return false;
        if(_watchedProgress.Add(window)){
            window.IsVisibleChanged+=(_,_)=>{if(window.IsVisible)_downloadTray.Remove(job.Id);};
            window.Closed+=(_,_)=>{_downloadTray.Remove(job.Id);_watchedProgress.Remove(window);};
        }
        window.Hide();return true;
    }
    bool IDownloadTraySink.Add(uint id,DownloadJob job)
    {
        var handle=DownloadTrayArt.Create(job.State,job.Progress);if(handle==IntPtr.Zero)return false;
        var data=new IconData{Size=(uint)Marshal.SizeOf<IconData>(),Window=_source.Handle,Id=id,Flags=1|2|4,Callback=0x802d,Icon=handle,Tip=DownloadTrayText.Format(job,_app.Locale),Info="",InfoTitle=""};
        if(!Shell_NotifyIcon(0,ref data)){DestroyIcon(handle);return false;}
        _fileIcons.Add(id,new(){Data=data,State=job.State,Percent=(int)job.Progress});return true;
    }
    void IDownloadTraySink.Update(uint id,DownloadJob job)
    {
        if(!_fileIcons.TryGetValue(id,out var entry))return;
        var old=entry.Data;var changed=entry.State!=job.State||entry.Percent!=(int)job.Progress;var handle=changed?DownloadTrayArt.Create(job.State,job.Progress):old.Icon;
        if(handle==IntPtr.Zero)handle=old.Icon;
        var data=old;data.Icon=handle;data.Tip=DownloadTrayText.Format(job,_app.Locale);
        if(Shell_NotifyIcon(1,ref data)){entry.Data=data;entry.State=job.State;entry.Percent=(int)job.Progress;if(handle!=old.Icon)DestroyIcon(old.Icon);}else if(handle!=old.Icon)DestroyIcon(handle);
    }
    void IDownloadTraySink.Remove(uint id){if(_fileIcons.Remove(id,out var entry)){Shell_NotifyIcon(2,ref entry.Data);DestroyIcon(entry.Data.Icon);}}
    private bool Enabled(string key)=>_app.Settings.Preferences[key]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true;
    public static void ConfigureStartup(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(enabled)key.SetValue("DownloadManager",StartupLaunch.Command(Environment.ProcessPath!));
        else if(StartupLaunch.OwnsCommand(key.GetValue("DownloadManager") as string,Environment.ProcessPath!))key.DeleteValue("DownloadManager",false);
    }
    public static void MigrateStartupCommand()
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);
            if(key?.GetValue("DownloadManager") is string existing && StartupLaunch.OwnsCommand(existing,Environment.ProcessPath!) && !existing.Equals(StartupLaunch.Command(Environment.ProcessPath!),StringComparison.OrdinalIgnoreCase))
                key.SetValue("DownloadManager",StartupLaunch.Command(Environment.ProcessPath!));
        }
        catch(Exception error) when(error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { Debug.WriteLine(error.GetType().Name); }
    }
    public void Update()
    {
        if(_disposed)return;var show=Enabled("showTray");
        if(show&&!_added)_added=Shell_NotifyIcon(0,ref _icon);
        else if(!show&&_added&&_window.IsVisible){Shell_NotifyIcon(2,ref _icon);_added=false;ShowWindow();}
    }
    private void StateChanged(object? sender,EventArgs e){if(_added&&Enabled("minimizeToTray")&&_window.WindowState==WindowState.Minimized)_window.Hide();}
    private void ShowWindow(){_window.Show();if(_window.WindowState==WindowState.Minimized)_window.WindowState=WindowState.Normal;_window.Activate();}
    private IntPtr Messages(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if((uint)message==_taskbarCreated){_added=false;Update();if(!_window.IsVisible&&!HideMain())ShowWindow();foreach(var pair in _fileIcons.ToArray())if(!Shell_NotifyIcon(0,ref pair.Value.Data))_downloadTray.Restore(pair.Key);}
        if(message==0x802d&&wParam.ToInt64()!=1){var id=(uint)wParam.ToInt64();var action=lParam.ToInt64()&0xffff;
            if(action is 0x202 or 0x203 or 0x400 or 0x401)_downloadTray.Restore(id);
            else if(action==0x205&&_downloadTray.Entries.Any(e=>e.IconId==id)){var menu=new ContextMenu();var restore=new MenuItem{Header=_app.Locale.S("tray.show_download")};restore.Click+=(_,_)=>_downloadTray.Restore(id);menu.Items.Add(restore);var main=new MenuItem{Header=_app.Locale.S("native.show_application")};main.Click+=(_,_)=>ShowWindow();menu.Items.Add(main);menu.IsOpen=true;}
            handled=true;return IntPtr.Zero;
        }
        if(message==0x802d){var action=lParam.ToInt64()&0xffff;if(action is 0x203 or 0x202)ShowWindow();else if(action==0x205){var menu=new ContextMenu();var open=new MenuItem{Header=_app.Locale.S("native.show_application")};open.Click+=(_,_)=>ShowWindow();menu.Items.Add(open);var downloads=new MenuItem{Header=_app.Locale.S("ui.downloads")};foreach(var job in _latestJobs.OrderBy(j=>j.State==DownloadState.Running?0:1).Take(20)){var id=job.Id;var item=new MenuItem{Header=job.FileName};item.Click+=(_,_)=>{if(_window is MainWindow main)main.ShowDownloadProgress(id);};downloads.Items.Add(item);}downloads.IsEnabled=downloads.HasItems;menu.Items.Add(downloads);var exit=new MenuItem{Header=_app.Locale.S("native.exit")};exit.Click+=(_,_)=>{if(_window is MainWindow main)main.RequestExit();else _window.Close();};menu.Items.Add(exit);menu.IsOpen=true;}handled=true;}
        return IntPtr.Zero;
    }
    private async Task PollAsync()
    {
        if(_busy||_disposed)return;_busy=true;
        try{
            _latestJobs=await _app.Engine.SnapshotAsync();_downloadTray.Update(_latestJobs);
            foreach(var job in _latestJobs){
                if(_states.TryGetValue(job.Id,out var previous)&&previous!=job.State){
                    if(job.State==DownloadState.Completed){Sound("soundComplete");if(Enabled("scannerEnabled"))_ = ScanWithLoggingAsync(job.FinalPath);}
                    else if(job.State==DownloadState.Failed)Sound("soundFailed");
                }_states[job.Id]=job.State;
            }
            var queues=await _app.Scheduler.SnapshotAsync();foreach(var queue in queues){if(_queues.TryGetValue(queue.Id,out var running)&&running!=queue.IsRunning)Sound(queue.IsRunning?"soundQueueStart":"soundQueueEnd");_queues[queue.Id]=queue.IsRunning;}
            var next=Enabled("wakeScheduled")?queues.Where(q=>q.Schedule.Enabled).Select(q=>q.Schedule.Next(DateTimeOffset.UtcNow)).Where(t=>t.HasValue).Min():null;
            if(next!=_wakeAt){if(_wakeTimer!=IntPtr.Zero)CancelWaitableTimer(_wakeTimer);_wakeAt=next;if(next is {} at){if(_wakeTimer==IntPtr.Zero)_wakeTimer=CreateWaitableTimer(IntPtr.Zero,false,null);var due=at.UtcDateTime.ToFileTimeUtc();if(_wakeTimer==IntPtr.Zero||!SetWaitableTimer(_wakeTimer,ref due,0,IntPtr.Zero,IntPtr.Zero,true))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}}
        }catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException){File.AppendAllText(Path.Combine(_app.DataDirectory,"integration.log"),DateTimeOffset.UtcNow+" "+error.GetType().Name+Environment.NewLine);}
        finally{_busy=false;}
    }
    private void Sound(string key)
    {
        if(!Enabled(key))return;var path=_app.Settings.Preferences[key+"File"]?.ToString();
        if(string.IsNullOrWhiteSpace(path)||path=="System default")PlaySound(key=="soundFailed"?"SystemExclamation":"SystemAsterisk",IntPtr.Zero,0x10000|1|2);
        else if(File.Exists(path))PlaySound(Path.GetFullPath(path),IntPtr.Zero,0x20000|1|2);
    }
    private async Task ScanWithLoggingAsync(string file){try{await ScanAsync(file);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException){try{File.AppendAllText(Path.Combine(_app.DataDirectory,"scanner.log"),DateTimeOffset.UtcNow+" "+error.GetType().Name+Environment.NewLine);}catch(Exception logError)when(logError is IOException or UnauthorizedAccessException){}}}
    private async Task ScanAsync(string file)
    {
        var path=_app.Settings.Preferences["scannerPath"]?.ToString()??"";if(!Path.IsPathFullyQualified(path)||!File.Exists(path))throw new IOException("Scanner missing");
        var tokens=ProcessArguments.Parse(_app.Settings.Preferences["scannerArguments"]?.ToString()??"[File]");var start=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true};
        foreach(var token in tokens)start.ArgumentList.Add(token.Replace("[File]",file,StringComparison.Ordinal));
        if(!tokens.Any(token=>token.Contains("[File]",StringComparison.Ordinal)))start.ArgumentList.Add(file);
        using var process=Process.Start(start)??throw new IOException();await process.WaitForExitAsync();
        File.AppendAllText(Path.Combine(_app.DataDirectory,"scanner.log"),DateTimeOffset.UtcNow+" exit="+process.ExitCode+Environment.NewLine);
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_timer.Stop();_downloadTray.Clear();_watchedProgress.Clear();_window.StateChanged-=StateChanged;if(_added)Shell_NotifyIcon(2,ref _icon);if(_icon.Icon!=IntPtr.Zero)DestroyIcon(_icon.Icon);if(_wakeTimer!=IntPtr.Zero){CancelWaitableTimer(_wakeTimer);CloseHandle(_wakeTimer);}_source.Dispose();}
}

public static class ProcessArguments
{
    public static IReadOnlyList<string> Parse(string text)
    {
        var result=new List<string>();var token=new System.Text.StringBuilder();var quoted=false;var started=false;
        for(var i=0;i<text.Length;i++){var c=text[i];if(c=='"'){quoted=!quoted;started=true;}else if(char.IsWhiteSpace(c)&&!quoted){if(started){result.Add(token.ToString());token.Clear();started=false;}}else{token.Append(c);started=true;}}
        if(quoted)throw new ArgumentException("Unclosed argument quote");if(started)result.Add(token.ToString());return result;
    }
}
