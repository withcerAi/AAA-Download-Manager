using System.Diagnostics;
using System.IO;
using System.Windows;
using DownloadManager.Core;
namespace DownloadManager.Desktop;
public static class DownloadFileActions
{
    public static ProcessStartInfo Plan(DownloadJob job,bool folder)
    {
        if(!Path.IsPathFullyQualified(job.FinalPath))throw new FileNotFoundException();
        if(folder){var path=Path.GetDirectoryName(job.FinalPath);if(path is null||!Directory.Exists(path))throw new DirectoryNotFoundException();var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=true};start.ArgumentList.Add(path);return start;}
        if(job.State!=DownloadState.Completed)throw new InvalidOperationException("files.not_ready");
        if(!File.Exists(job.FinalPath))throw new FileNotFoundException();
        return new ProcessStartInfo(job.FinalPath){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(job.FinalPath)!};
    }
    public static void Open(Window owner,LocaleCatalog locale,DownloadJob job,bool folder)
    {
        try{Process.Start(Plan(job,folder));}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException){var key=error switch{DirectoryNotFoundException=>"files.folder_missing",FileNotFoundException=>"files.file_missing",InvalidOperationException=>"files.not_ready",_=>"files.open_failed"};AppDialog.Show(owner,locale.S(key,("path",folder?Path.GetDirectoryName(job.FinalPath):job.FinalPath)),locale.S("ui.download_manager"),MessageBoxButton.OK,MessageBoxImage.Error);}
    }
}
