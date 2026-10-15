using DownloadManager.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace DownloadManager.Desktop;
public sealed class CompletionWindow : Window
{
    private readonly CompletionCountdown _countdown;
    private readonly App _app;
    private readonly TextBlock _remaining = new() { FontSize = 34, Margin = new Thickness(0, 15, 0, 15) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _ticking;
    public CompletionWindow(App app, CompletionTicket ticket, ICompletionExecutor? executor = null)
    {
        _app = app; _countdown = new(ticket); executor ??= new WindowsCompletionExecutor(app);
        Width = 540; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; FlowDirection = app.Locale.Direction; app.Locale.Bind(this, TitleProperty, "completion.title");
        var body = new StackPanel { Margin = new Thickness(24) }; body.SetResourceReference(BackgroundProperty, "Chrome"); Content = body;
        var heading = Ui.Label(app.Locale, "completion." + ticket.Options.Action.ToString().ToLowerInvariant()); heading.FontSize = 21; body.Children.Add(heading); body.Children.Add(_remaining); body.Children.Add(Ui.Label(app.Locale, "completion.notice", true)); body.Children.Add(_error); body.Children.Add(Ui.Button(app.Locale, "ui.cancel", (_, _) => Close()));
        _timer.Tick += async (_, _) =>
        {
            if (_ticking) return; _ticking = true;
            try
            {
                _remaining.Text = app.Locale.S("completion.seconds", ("count", _countdown.Remaining));
                var busy = (await app.Engine.SnapshotAsync()).Any(j => j.State == DownloadState.Running) || (await app.Scheduler.SnapshotAsync()).Any(q => q.IsRunning);
                if (await _countdown.TickAsync(busy, executor) || _countdown.Finished) Close();
            }
            catch (Exception error) { _timer.Stop(); _countdown.Cancel(); _error.Text = app.Locale.S("errors.operation", ("detail", error.GetType().Name)); }
            finally { _ticking = false; }
        };
        _remaining.Text = app.Locale.S("completion.seconds", ("count", _countdown.Remaining)); Loaded += (_, _) => _timer.Start(); Closed += (_, _) => { _timer.Stop(); _countdown.Cancel(); };
    }
}
internal sealed class WindowsCompletionExecutor(App app) : ICompletionExecutor
{
    [DllImport("Powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
    public async Task ExecuteAsync(CompletionOptions options)
    {
        options.Validate();
        if (options.Action == CompletionAction.None) return;
        if ((await app.Engine.SnapshotAsync()).Any(job => job.State == DownloadState.Running)) throw new InvalidOperationException("completion.busy");
        switch (options.Action)
        {
            case CompletionAction.ExitApplication: if(app.MainWindow is MainWindow main)main.RequestExit();else app.MainWindow.Close(); break;
            case CompletionAction.ShutDown:
                var shutdown = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true }; shutdown.ArgumentList.Add("/s"); shutdown.ArgumentList.Add("/t"); shutdown.ArgumentList.Add("0"); if (options.Force) shutdown.ArgumentList.Add("/f"); await RunAsync(shutdown); break;
            case CompletionAction.Sleep: case CompletionAction.Hibernate:
                if (!SetSuspendState(options.Action == CompletionAction.Hibernate, false, false)) throw new Win32Exception(Marshal.GetLastWin32Error()); break;
            case CompletionAction.Disconnect:
                var disconnect = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "rasdial.exe")) { UseShellExecute = false, CreateNoWindow = true }; disconnect.ArgumentList.Add(options.ConnectionName); disconnect.ArgumentList.Add("/disconnect"); await RunAsync(disconnect); break;
            case CompletionAction.OpenFile: Process.Start(new ProcessStartInfo(options.FilePath) { UseShellExecute = true }); break;
        }
    }
    private static async Task RunAsync(ProcessStartInfo start) { using var process = Process.Start(start) ?? throw new InvalidOperationException(); await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode); }
}
