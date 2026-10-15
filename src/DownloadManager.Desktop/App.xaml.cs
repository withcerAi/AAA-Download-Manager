using DownloadManager.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DownloadManager.Desktop;

public partial class App : Application
{
    internal bool SuppressStartupForUiTests { get; set; }
    public DownloadEngine Engine { get; private set; } = null!;
    public LocaleCatalog Locale { get; private set; } = null!;
    public SettingsSchema Schema { get; private set; } = null!;
    public UserSettings Settings { get; private set; } = null!;
    public BrowserHandoffService BrowserTransfers { get; private set; } = null!;
    public BrowserHandoffs BrowserHandoffs { get; private set; } = null!;
    public QueueScheduler Scheduler { get; private set; } = null!;
    private BrowserPipeServer? _browser;
    private DesktopIntegration? _desktop;
    private WindowsDialTransport? _dial;
    private FileStream? _instanceLock;
    public string DataDirectory { get; private set; } = "";
    private string _settingsPath = "";
    private bool _engineDisposed;
    public bool EngineIsDisposed => _engineDisposed;
    public bool HideMainToTray()=>_desktop?.HideMain()==true;
    public bool HideDownloadToTray(ProgressWindow window,DownloadJob job)=>_desktop?.HideDownload(window,job)==true;
    public bool CanHideProgressToTray => _desktop?.CanHideProgress==true;

    protected override async void OnStartup(StartupEventArgs args)
    {
        if (SuppressStartupForUiTests) return;
        base.OnStartup(args);
        try
        {
            var customData = Argument(args.Args, "--data-dir");
            var data = customData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager");
            Directory.CreateDirectory(data); DataDirectory = data;
            if (!args.Args.Contains("--headless") && !args.Args.Contains("--render-ui"))
            {
                try { _instanceLock = new FileStream(Path.Combine(data, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    using var existing = new System.IO.Pipes.NamedPipeClientStream(".", BrowserProtocol.PipeName(data), System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                    await existing.ConnectAsync(10000); await BrowserProtocol.WriteAsync(existing, new BrowserRequest { Action = "activate" }); Shutdown(); return;
                }
            }
            _settingsPath = Path.Combine(data, "settings.json");
            Settings = UserSettings.Load(_settingsPath);
            if (customData is not null && !File.Exists(_settingsPath)) Settings.DownloadDirectory = Path.Combine(data, "Downloads");
            Locale = new LocaleCatalog(Path.Combine(AppContext.BaseDirectory, "locales")); Locale.Select(Settings.Language);
            Schema = SettingsSchema.Load(Path.Combine(AppContext.BaseDirectory, "settings.schema.json"));
            foreach (var item in Schema.Defaults) if (!Settings.Preferences.ContainsKey(item.Key)) Settings.Preferences[item.Key] = item.Value?.DeepClone();
            BrowserHandoffs=new BrowserHandoffs(data); Settings.ApplyTheme(); Engine = new DownloadEngine(data); Engine.Configure(Settings.TransferPolicy()); Scheduler = new QueueScheduler(Engine, data) { GlobalParallel = ParallelLimit() }; BrowserTransfers=new BrowserHandoffService(Engine,Scheduler,BrowserHandoffs);
            DispatcherUnhandledException += (_, eventArgs) => { File.AppendAllText(Path.Combine(data, "errors.log"), eventArgs.Exception + Environment.NewLine); AppDialog.Show(Locale.S("errors.operation", ("detail", eventArgs.Exception.GetType().Name)), Locale.S("ui.download_manager")); eventArgs.Handled = true; };
            if (args.Args.Contains("--headless"))
            {
                var url = Argument(args.Args, "--url");
                if (url is null) throw new ArgumentException("errors.invalid_url");
                var job = await Engine.AddAsync(url, Argument(args.Args, "--directory") ?? Settings.DownloadDirectory, Argument(args.Args, "--filename"));
                DownloadJob result;
                do { await Task.Delay(100); result = (await Engine.SnapshotAsync()).Single(item => item.Id == job.Id); } while (result.State is DownloadState.Queued or DownloadState.Running);
                var report = Argument(args.Args, "--report") ?? Path.Combine(data, "result.json");
                File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                await DisposeEngineAsync(); Shutdown(result.State == DownloadState.Completed ? 0 : 1); return;
            }
            var main = new MainWindow(this); MainWindow = main;
            if (Argument(args.Args, "--render-ui") is { } image)
            {
                await main.RefreshAsync(); main.SelectFirstForRender();
                var content = (FrameworkElement)main.Content; content.Measure(new Size(1260, 780)); content.Arrange(new Rect(0, 0, 1260, 780)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1260, 780, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(image)) encoder.Save(output);
                using (var output = File.Create(image + ".json"))
                {
                    var settingsWindow = new SettingsWindow(Settings, Locale, Schema, ApplySettings, Argument(args.Args, "--settings-section") ?? "general");
                    if (Argument(args.Args, "--settings-query") is { } query) settingsWindow.SetSearchForRender(query);
                    var settingsContent = (FrameworkElement)settingsWindow.Content; settingsContent.Measure(new Size(990, 700)); settingsContent.Arrange(new Rect(0, 0, 990, 700)); settingsContent.UpdateLayout();
                    var settingsBitmap = new RenderTargetBitmap(990, 700, 96, 96, PixelFormats.Pbgra32); var backdrop = new DrawingVisual(); using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle((Brush)Resources["Chrome"], null, new Rect(0, 0, 990, 700)); settingsBitmap.Render(backdrop); settingsBitmap.Render(settingsContent); var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap)); using (var settingsOutput = File.Create(image + ".settings.png")) settingsEncoder.Save(settingsOutput);
                    var smoke = new SettingsWindow(Settings, Locale, Schema, _ => { }); var uxChecks = smoke.VerifyUx();
                    var import = new ImportLinksWindow(Locale, Settings.DownloadDirectory, [], "https://example.test/Project.Resources.zip\nhttps://example.test/Design.Handbook.pdf\nhttps://example.test/Project.Resources.zip"); SaveDialog(import, image + ".import.png", 920, 720);
                    var scheduler = new SchedulerWindow(this); await scheduler.LoadAsync(); SaveDialog(scheduler,image+".scheduler.png",1110,850);
                    var browser = new BrowserIntegrationWindow(this); SaveDialog(browser,image+".browser.png",760,640);
                    var about = new AboutWindow(Locale); SaveDialog(about, image + ".about.png", 630, 560);
                    var terms = new LegalDocumentWindow(Locale,Locale.S("about.legal_title"),"legal/TERMS-OF-USE.txt"); SaveDialog(terms,image + ".terms.png",800,600);
                    var add = new AddDownloadWindow(Locale, Settings.DownloadDirectory); SaveDialog(add, image + ".add.png", 680, 430);
                    if ((await Engine.SnapshotAsync()).FirstOrDefault() is { } job) { var progress = new ProgressWindow(Engine, Locale, job.Id);
                        if (Argument(args.Args, "--progress-capture") is { } capturedPath)
                        {
                            var captured = JsonSerializer.Deserialize<DownloadJob>(File.ReadAllText(capturedPath))!;
                            if (Argument(args.Args, "--speed-samples") is { } samplesPath) progress.SetCapturedSamples(JsonSerializer.Deserialize<double[]>(File.ReadAllText(samplesPath)) ?? []);
                            progress.RenderJob(captured);
                        }
                        else await progress.RefreshForRenderAsync();
                        SaveDialog(progress, image + ".progress.png", 690, 450); progress.SelectTabForRender(1); SaveDialog(progress, image + ".speed.png", 690, 450); progress.SelectTabForRender(2); SaveDialog(progress, image + ".completion.png", 690, 450); }
                    JsonSerializer.Serialize(output, new { uxChecks, direction = Locale.Direction.ToString(), schemaSections = Schema.Sections.Count, schemaFields = Schema.Sections.Sum(section => section.Fields.Count), languages = Locale.Languages.Select(info => info.Code).ToArray(), rows = (await Engine.SnapshotAsync()).Count });
                }
                await DisposeEngineAsync(); Shutdown(); return;
            }
            if(!Settings.SequentialDefaultsApplied){await Scheduler.SetSequentialDefaultsAsync();Settings.Preferences["parallel"]=1;Settings.SequentialDefaultsApplied=true;Settings.Save(_settingsPath);}
            main.Show(); _dial=new WindowsDialTransport();var prerequisite=new ConnectionPrerequisite(_dial);Engine.BeforeTransfer=token=>prerequisite.EnsureAsync(Settings.DialPolicy(),token);Scheduler.Start(); _desktop=new DesktopIntegration(this,main);
            _browser = new BrowserPipeServer(data, (request, token) => Dispatcher.InvokeAsync(() => main.HandleBrowserAsync(request,token)).Task.Unwrap().WaitAsync(token)); _browser.Start();
        }
        catch (Exception exception)
        {
            if (Argument(args.Args, "--report") is { } report) File.WriteAllText(report, JsonSerializer.Serialize(new { error = exception.GetType().Name, detail = exception.ToString() }));
            else AppDialog.Show(Locale is not null ? Locale.S("errors.startup", ("detail", exception.GetType().Name)) : exception.GetType().Name);
            if (Engine is not null) await DisposeEngineAsync(); Shutdown(1);
        }
    }

    private void SaveDialog(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)Resources["Chrome"], null, new Rect(0, 0, width, height)); bitmap.Render(background); bitmap.Render(content); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); png.Save(output);
    }

    public void ApplySettings(UserSettings next)
    {
        var policy = next.TransferPolicy(); next.DialPolicy().Validate(); if(_desktop is not null) DesktopIntegration.ConfigureStartup(next.Preferences["startup"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true); next.Save(_settingsPath); Engine.Configure(policy); Settings = next; Settings.ApplyTheme(); Locale.Select(Settings.Language); _desktop?.Update(); if (Scheduler is not null) Scheduler.GlobalParallel = ParallelLimit();
    }
    private int ParallelLimit() => int.TryParse(Settings.Preferences["parallel"]?.ToString(), out var count) ? Math.Clamp(count, 1, 10) : 1;
    public async Task DisposeEngineAsync() { if (_engineDisposed) return; _engineDisposed = true; _desktop?.Dispose(); if (_browser is not null) await _browser.DisposeAsync(); if (Scheduler is not null) await Scheduler.DisposeAsync(); await Engine.DisposeAsync(); _dial?.Dispose(); _instanceLock?.Dispose(); }
    protected override void OnExit(ExitEventArgs e) { _instanceLock?.Dispose(); base.OnExit(e); }
    private static string? Argument(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
}

