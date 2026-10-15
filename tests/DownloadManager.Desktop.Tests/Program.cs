using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DownloadManager.Desktop;
using DownloadManager.Core;
using System.Windows.Shell;
using System.IO;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void Check(bool value, string code) { _assertions++; if (!value) throw new InvalidOperationException(code); }
    private static string Serialized(object value) => JsonSerializer.Serialize(value);
    private static void Release(SettingsWindow window) { window.GetType().GetField("_allowClose", Hidden)!.SetValue(window, true); window.Close(); }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root) { yield return root; for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(root, index))) yield return child; }
    private static void CheckTextFit(DependencyObject root,string context){foreach(var text in Visuals(root).OfType<TextBox>().Where(t=>t.Visibility==Visibility.Visible&&t.Text.Length>0&&t.ActualHeight>0)){text.ApplyTemplate();var host=text.Template.FindName("PART_ContentHost",text) as ScrollViewer;if(host is not null){var rectangle=text.GetRectFromCharacterIndex(0);Check(host.ViewportHeight+1>=rectangle.Height,"input_text_clipped:"+context+":"+text.Text+":"+host.ViewportHeight+":"+rectangle.Height);}}}
    private static void Render(Window window,string path,int width,int height){var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);var backdrop=new System.Windows.Media.DrawingVisual();using(var drawing=backdrop.RenderOpen())drawing.DrawRectangle((System.Windows.Media.Brush)Application.Current.Resources["Chrome"],null,new Rect(0,0,width,height));bitmap.Render(backdrop);bitmap.Render(content);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void QueueCategorySave()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var dialog=Application.Current.Windows.OfType<CategoryEditWindow>().Single();
            dialog.NameBox.Text="Fixture category";dialog.ExtensionsBox.Text="ABCXYZ";
            Invoke(dialog,"Finish");
        });
    }
    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Path.GetFullPath(args[0]); Directory.CreateDirectory(directory);
        var app = new App(); typeof(App).GetProperty("SuppressStartupForUiTests", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var catalog = new LocaleCatalog(Path.Combine(AppContext.BaseDirectory, "locales"));
            foreach(var language in new[]{"en","fa"})foreach(var mode in new[]{"light","dark"}){
                catalog.Select(language);new UserSettings{Palette="classic",Appearance=mode}.ApplyTheme();
                Check(catalog.S("ui.download_manager") == "AAA Download Manager", "brand_localized:" + language);
                var about = new AboutWindow(catalog); Render(about, Path.Combine(directory,"about-"+language+"-"+mode+".png"), 630, 560);
                var aboutTexts=Visuals((DependencyObject)about.Content).OfType<TextBlock>().Select(t=>t.Text).ToArray();
                Check(aboutTexts.Contains("AAA Download Manager") && aboutTexts.Any(t=>t.Contains(typeof(AboutWindow).Assembly.GetName().Version!.ToString(3))) && !aboutTexts.Any(t=>t.Contains("0.1.0")), "about_brand_version_current");
                Check(about.FlowDirection==catalog.Direction && Visuals((DependencyObject)about.Content).OfType<Button>().Count()==4,"about_legal_links_and_rtl"); about.Close();
                var legal=new LegalDocumentWindow(catalog,catalog.S("about.legal_title"),"legal/TERMS-OF-USE.txt");
                Render(legal,Path.Combine(directory,"terms-"+language+"-"+mode+".png"),800,600);
                var legalText=Visuals((DependencyObject)legal.Content).OfType<TextBox>().Single();
                Check(legalText.IsReadOnly && legalText.FlowDirection==FlowDirection.LeftToRight && legalText.Text.Contains("AAA DOWNLOAD MANAGER") && legalText.Text.Contains("NON-WAIVABLE RIGHTS") && legalText.Text.Contains("CORRUPTED OR LOST"),"legal_document_complete_readonly_english"); legal.Close();
                var deletion=new RemoveDownloadsWindow(catalog,new[]{new DownloadJob{State=DownloadState.Completed,FileName="Example.Download.zip",FinalPath="C:\\Downloads\\Example.Download.zip"}});
                Check(!deletion.DeleteFiles,"delete_dialog_default_keeps_files");Check(deletion.FlowDirection==catalog.Direction,"delete_dialog_direction");
                Render(deletion,Path.Combine(directory,"delete-"+language+"-"+mode+".png"),650,490);
                var radios=Visuals((DependencyObject)deletion.Content).OfType<RadioButton>().ToArray();
                Check(radios.Length==2&&radios[0].IsChecked==true&&radios[1].IsChecked!=true,"delete_choices_default");
                Render(deletion,Path.Combine(directory,"delete-"+language+"-"+mode+".png"),650,490);
                var error=new AppDialog(catalog,catalog.S("errors.invalid_url"),catalog.S("ui.download_manager"),icon:MessageBoxImage.Error);Render(error,Path.Combine(directory,"error-"+language+"-"+mode+".png"),540,240);
                deletion.Close();error.Close();
            }
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
            foreach (var language in new[] { "en", "fa" }) foreach (var mode in new[] { "light", "dark" })
            {
                catalog.Select(language); new UserSettings { Palette = "classic", Appearance = mode }.ApplyTheme();
                var responseRequest = new BrowserRequest { Urls = ["https://example.test/post.php"], Context = new SessionRequest { Url = "https://example.test/post.php", Method = "POST" }, ResponseMetadata = new BrowserResponseMetadata { Url = "https://example.test/post.php", ContentDisposition = "attachment; filename=Post.Video.mp4", ContentType = "video/mp4", Size = 42 } };
                var responseReview = BrowserDownloadReview.CreateSingle(catalog, new UserSettings(), [new QueueChoice("main", "Main")], responseRequest);
                Pump((Task)typeof(AddDownloadWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(responseReview, [catalog])!);
                Check(responseReview.FileNameBox.Text == "Post.Video.mp4" && responseReview.PreviewMetadata?.Size == 42 && BrowserDownloadReview.AcceptedRequest(responseRequest, responseReview).Context?.Method == "POST", "browser_post_response_metadata_preview_before_start"); responseReview.Close();
                var metadataWindow = new AddDownloadWindow(catalog, directory);
                metadataWindow.Inspector = (_, _) => Task.FromResult(new LinkMetadata("Resolved.Video.mp4", 12345678, "video/mp4", 200, "ready"));
                metadataWindow.UrlBox.Text = "https://example.test/remote_control.php";
                var inspectMethod = typeof(AddDownloadWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Pump((Task)inspectMethod.Invoke(metadataWindow, [catalog])!);
                Check(metadataWindow.FileNameBox.Text == "Resolved.Video.mp4" && metadataWindow.PreviewMetadata?.Size == 12345678, "filename_resolved_before_start:" + language);
                typeof(AddDownloadWindow).GetField("_manualName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(metadataWindow, true);
                metadataWindow.FileNameBox.Text = "My name.mkv";
                Pump((Task)inspectMethod.Invoke(metadataWindow, [catalog])!);
                Check(metadataWindow.FileNameBox.Text == "My name.mkv", "preview_overwrites_custom_name");
                Render(metadataWindow, Path.Combine(directory, "metadata-preview-" + language + "-" + mode + ".png"), 680, 490); CheckTextFit((DependencyObject)metadataWindow.Content, "metadata"); metadataWindow.Close();
                var batch = new ImportLinksWindow(catalog, directory, [], string.Join("\n", Enumerable.Range(1, 8).Select(i => "https://example.test/item" + i + ".php")));
                var running = 0; var peak = 0; var calls = 0;
                batch.Inspector = async (url, token) => { calls++; peak = Math.Max(peak, ++running); try { await Task.Delay(12, token); return new("Video.mp4", 12345678, "video/mp4", 200, "ready"); } finally { running--; } };
                var checkMethod = typeof(ImportLinksWindow).GetMethod("CheckLinksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Pump((Task)checkMethod.Invoke(batch, [])!);
                var rows = Visuals((DependencyObject)batch.Content).OfType<DataGrid>().Single().ItemsSource.Cast<ImportLinkRow>().ToArray();
                Check(calls == 8 && peak <= 3 && peak > 1 && rows.All(r => r.Name == "Video.mp4" && r.Size.Length > 0 && !r.Status.StartsWith("inspect.")), "batch_metadata_bounded_and_localized");
                Render(batch, Path.Combine(directory, "batch-check-" + language + "-" + mode + ".png"), 940, 760); Pump(Task.Delay(25)); Render(batch, Path.Combine(directory, "batch-check-" + language + "-" + mode + ".png"), 940, 760); CheckTextFit((DependencyObject)batch.Content, "batch-check");
                calls = 0; batch.Inspector = async (_, token) => { calls++; await Task.Delay(200, token); return new("Later.mp4", 1, "video/mp4", 200, "ready"); };
                var cancelledCheck = (Task)checkMethod.Invoke(batch, [])!; Pump(Task.Delay(5)); Pump((Task)checkMethod.Invoke(batch, [])!); Pump(cancelledCheck);
                Check(calls <= 3 && rows.All(row => row.CheckState != "checking") && Get<Button>(batch, "_start").IsEnabled, "batch_cancel_releases_controls_and_pending_requests");
                batch.Close();
            }
            SynchronizationContext.SetSynchronizationContext(null);
            var openFixtureFolder=Path.Combine(directory,"open-file-fixture");Directory.CreateDirectory(openFixtureFolder);var openFixturePath=Path.Combine(openFixtureFolder,"Movie example.mkv");File.WriteAllText(openFixturePath,"fixture");var completedJob=new DownloadJob{State=DownloadState.Completed,FinalPath=openFixturePath};Check(DownloadFileActions.Plan(completedJob,false).FileName==openFixturePath,"open_file_wrong_target");Check(DownloadFileActions.Plan(completedJob,true).ArgumentList.Single()==openFixtureFolder,"open_folder_unquoted_path");File.Delete(openFixturePath);try{DownloadFileActions.Plan(completedJob,false);throw new Exception("missing_file_opened");}catch(FileNotFoundException){_assertions++;}Directory.Delete(openFixtureFolder);try{DownloadFileActions.Plan(completedJob,true);throw new Exception("missing_folder_opened");}catch(DirectoryNotFoundException){_assertions++;}
            foreach(var language in new[]{"en","fa"})foreach(var mode in new[]{"light","dark"}){
                catalog.Select(language);new UserSettings{Appearance=mode}.ApplyTheme();
                var category=new CategoryEditWindow(catalog,Path.Combine(directory,"default-downloads"),null,[]);
                Check(!(bool)Invoke(category,"TryBuildResult")!,"category_empty_name_rejected");
                category.NameBox.Text="Research";category.ExtensionsBox.Text="*.abcxyz, .DEFXYZ abcxyz";
                Check((bool)Invoke(category,"TryBuildResult")! && category.Result!["extensions"]=="ABCXYZ DEFXYZ" && category.Result["folder"]=="","category_new_normalizes_extensions_and_default_folder");
                var defaultPolicy=new UserSettings{DownloadDirectory=Path.Combine(directory,"default-downloads")};
                defaultPolicy.Preferences["categories"]=new JsonArray(new JsonObject{{"id",category.Result!["id"]},{"name","Research"},{"extensions","ABCXYZ DEFXYZ"},{"folder",""}});
                Check(DownloadDestination.Resolve(defaultPolicy.DownloadDirectory,"paper.abcxyz",defaultPolicy.TransferPolicy())==defaultPolicy.DownloadDirectory,"category_empty_folder_uses_default");
                category.FolderBox.Text=Path.Combine(directory,"separate-drive-fixture","Research");Check((bool)Invoke(category,"TryBuildResult")!,"category_absolute_folder_rejected");
                var savedCategory=category.Result!;defaultPolicy.Preferences["categories"]![0]!["folder"]=savedCategory["folder"];
                var categorySettingsPath=Path.Combine(directory,"category-settings-"+language+mode+".json");defaultPolicy.Save(categorySettingsPath);var restored=UserSettings.Load(categorySettingsPath);
                Check(DownloadDestination.Resolve(restored.DownloadDirectory,"paper.abcxyz",restored.TransferPolicy())==savedCategory["folder"],"category_folder_persists_and_routes");
                var editCategory=new CategoryEditWindow(catalog,defaultPolicy.DownloadDirectory,savedCategory,[savedCategory]);editCategory.NameBox.Text="Research updated";editCategory.FolderBox.Clear();Check((bool)Invoke(editCategory,"TryBuildResult")!&&editCategory.Result!["id"]==savedCategory["id"]&&editCategory.Result["folder"]=="","category_edit_keeps_id_and_can_clear_folder");editCategory.Close();
                var duplicate=new CategoryEditWindow(catalog,defaultPolicy.DownloadDirectory,null,[savedCategory]);duplicate.NameBox.Text="Another";duplicate.ExtensionsBox.Text="abcxyz";Check(!(bool)Invoke(duplicate,"TryBuildResult")!,"category_overlapping_extension_rejected");duplicate.Close();
                Render(category,Path.Combine(directory,"category-edit-"+language+"-"+mode+".png"),680,490);CheckTextFit((DependencyObject)category.Content,"category-editor");category.Close();
            }
            var legacySettingsPath=Path.Combine(directory,"legacy-path-settings.json");var legacySettings=new UserSettings();legacySettings.Preferences["temporaryFolder"]=@"D:\Downloads\.temp";legacySettings.Preferences["categories"]=new JsonArray(new JsonObject{{"id","video"},{"folder",@"D:\Downloads\Video"},{"extensions","MKV"}});legacySettings.Save(legacySettingsPath);var normalized=UserSettings.Load(legacySettingsPath);Check(normalized.Preferences["temporaryFolder"]!.ToString()==""&&normalized.Preferences["categories"]![0]!["folder"]!.ToString()=="Video","legacy_placeholder_paths_visible");
            var removalRoot=Path.Combine(directory,"conditional-removal");var removalEngine=new DownloadEngine(removalRoot);
            try{
                foreach(var state in new[]{DownloadState.Queued,DownloadState.Paused,DownloadState.Failed,DownloadState.Running}){
                    var incomplete=removalEngine.AddAsync("https://example.test/partial.zip",removalRoot,start:false).GetAwaiter().GetResult();incomplete.State=state;
                    File.WriteAllText(incomplete.PartialPath,"partial fixture");File.WriteAllText(incomplete.FinalPath,"unrelated final path must survive");
                    Check(!DownloadRemoval.NeedsDiskChoice([incomplete]),"unfinished_disk_question:"+state);
                    DownloadRemoval.ExecuteAsync(removalEngine,[incomplete],true).GetAwaiter().GetResult();Check(!File.Exists(incomplete.PartialPath),"unfinished_temp_left:"+state);Check(File.Exists(incomplete.FinalPath),"unfinished_final_deleted_without_choice:"+state);
                }
                var done=removalEngine.AddAsync("https://example.test/done.zip",removalRoot,start:false).GetAwaiter().GetResult();done.State=DownloadState.Completed;File.WriteAllText(done.FinalPath,"completed fixture");
                var pending=removalEngine.AddAsync("https://example.test/pending.zip",removalRoot,start:false).GetAwaiter().GetResult();File.WriteAllText(pending.PartialPath,"partial fixture");
                Check(DownloadRemoval.NeedsDiskChoice([done,pending]),"mixed_selection_no_completed_choice");DownloadRemoval.ExecuteAsync(removalEngine,[done,pending],false).GetAwaiter().GetResult();Check(File.Exists(done.FinalPath)&&!File.Exists(pending.PartialPath),"mixed_keep_completed_or_temp_cleanup_failed");
                File.Delete(done.FinalPath);Check(!DownloadRemoval.NeedsDiskChoice([done]),"missing_completed_file_asked_disk_question");
            }finally{removalEngine.DisposeAsync().AsTask().GetAwaiter().GetResult();}
            var activationTarget = new Window { ShowActivated = false };
            BrowserWindowActivation.Prepare(activationTarget);
            Check(activationTarget.Topmost && activationTarget.ShowActivated, "browser_review_first_frame_visible");
            typeof(Window).GetMethod("OnContentRendered", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activationTarget, [EventArgs.Empty]);
            Check(!activationTarget.Topmost && !activationTarget.ShowActivated, "browser_review_restores_window_flags");
            activationTarget.Topmost = true;
            typeof(Window).GetMethod("OnContentRendered", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activationTarget, [EventArgs.Empty]);
            Check(activationTarget.Topmost, "browser_review_activation_is_one_shot"); activationTarget.Close();
            var cancelledActivation = new Window(); BrowserWindowActivation.Prepare(cancelledActivation); cancelledActivation.Close();
            Check(!cancelledActivation.Topmost, "browser_review_early_close_restores_topmost");
            var foregroundOwner = new Window { Width = 240, Height = 120, ShowInTaskbar = false, ShowActivated = false };
            foregroundOwner.Show();
            var foregroundReview = new AddDownloadWindow(catalog, directory) { Owner = foregroundOwner, ShowInTaskbar = false };
            foregroundReview.UrlBox.Text = "https://example.test/foreground-fixture.zip";
            BrowserWindowActivation.Prepare(foregroundReview);
            var foregroundReviewRendered = false;
            foregroundReview.ContentRendered += (_, _) =>
            {
                foregroundReviewRendered = true;
                Check(foregroundReview.IsVisible && !foregroundReview.Topmost && foregroundReview.WindowState != WindowState.Minimized, "browser_foregroundReview_visible_and_not_permanently_topmost");
                foregroundReview.DialogResult = false;
            };
            foregroundOwner.WindowState = WindowState.Minimized;
            // The browser handler restores the owner before opening its modal review.
            foregroundOwner.WindowState = WindowState.Normal;
            Check(foregroundReview.ShowDialog() == false && foregroundReviewRendered, "browser_foregroundReview_renders_without_waiting_for_metadata"); foregroundOwner.Close();
            var browserRequest=new BrowserRequest{Id="single-review-fixture",Urls=["https://example.test/Video.mkv"],Context=new SessionRequest{Url="https://example.test/Video.mkv",Method="POST",BodyBase64=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("token=fixture")),Headers=new(){{"cookie","sid=fixture"}}}};
            Check(BrowserDownloadReview.IsSingle(browserRequest),"single_browser_link_uses_batch");Check(!BrowserDownloadReview.IsSingle(new BrowserRequest{Urls=["https://example.test/a.zip","https://example.test/b.zip"]}),"multiple_browser_links_use_single");
            var review=BrowserDownloadReview.CreateSingle(catalog,new UserSettings(),[new QueueChoice("main","Main")],browserRequest);Check(review.Topmost && review.ShowActivated,"browser_factory_requests_foreground");Check(review.UrlBox.Text==browserRequest.Urls[0]&&review.UrlBox.IsReadOnly,"browser_single_source_not_prefilled");var acceptedBrowser=BrowserDownloadReview.AcceptedRequest(browserRequest,review);Check(acceptedBrowser.Context?.Method=="POST"&&acceptedBrowser.Context.Headers["cookie"]=="sid=fixture"&&acceptedBrowser.Id==browserRequest.Id,"browser_single_session_lost");review.Close();
            var traySink=new FakeDownloadTraySink();var restoredTrayJobs=new List<string>();var tray=new DownloadTrayRegistry(traySink,id=>restoredTrayJobs.Add(id));
            var trayJob1=new DownloadJob{Id="tray-one",FileName=new string('x',180)+".mkv",State=DownloadState.Running,TotalBytes=1000,ReceivedBytes=250,BytesPerSecond=32768};var trayJob2=new DownloadJob{Id="tray-two",FileName="second.zip",State=DownloadState.Running};
            Check(tray.Add(trayJob1)&&tray.Add(trayJob1)&&traySink.Added.Count==1,"duplicate_download_tray_icon");Check(tray.Add(trayJob2)&&tray.Entries.Count==2,"separate_download_tray_icons_missing");
            trayJob1.ReceivedBytes=750;tray.Update([trayJob1,trayJob2]);Check(traySink.Updated.Any(j=>j.Id==trayJob1.Id&&j.Progress==75),"tray_progress_not_updated");
            var firstTrayId=tray.Entries.Single(e=>e.JobId==trayJob1.Id).IconId;Check(tray.Restore(firstTrayId)&&restoredTrayJobs.SequenceEqual(new[]{trayJob1.Id})&&tray.Entries.Single().JobId==trayJob2.Id,"tray_restored_wrong_download");
            tray.Update([]);Check(tray.Entries.Count==0&&traySink.Removed.Count==2,"removed_download_tray_icon_leaked");traySink.Reject=true;Check(!tray.Add(trayJob1)&&tray.Entries.Count==0,"failed_icon_registration_hides_download");tray.Clear();Check(traySink.Removed.Count==2,"tray_double_remove");
            foreach(var language in new[]{"en","fa"}){catalog.Select(language);var tip=DownloadTrayText.Format(trayJob1,catalog);Check(tip.Length<=127&&tip.Contains(new DownloadRow(trayJob1,catalog).ProgressText)&&tip.Contains(new DownloadRow(trayJob1,catalog).Speed),"tray_tooltip_loses_progress_or_speed");}
            foreach(var state in Enum.GetValues<DownloadState>()){var bitmap=DownloadTrayArt.Render(state,65);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var iconFile=File.Create(Path.Combine(directory,"tray-"+state+".png")))encoder.Save(iconFile);var handle=DownloadTrayArt.Create(state,65);Check(handle!=IntPtr.Zero,"native_download_icon_creation_failed");Check(DownloadTrayArt.DestroyIcon(handle),"native_download_icon_release_failed");}
            var memory=new ClipboardUrlMemory();
            Check(memory.Read(()=>"ordinary text") is null,"clipboard_non_url_prefilled");
            Check(memory.Read(()=>"https://example.test/file.zip?token=abc")=="https://example.test/file.zip?token=abc"&&!memory.UsedCached,"clipboard_valid_url_missing");
            Check(memory.Read(()=>throw new System.Runtime.InteropServices.ExternalException())==memory.LastUrl&&memory.UsedCached,"clipboard_busy_fallback");
            Check(memory.Read(()=>"file:///C:/private.txt")==memory.LastUrl,"clipboard_unsafe_replaced_cache");
            Check(memory.Read(()=>"https://user:password@example.test/file.zip")==memory.LastUrl,"clipboard_credentials_accepted");
            Check(memory.Read(()=>"https://example.test/new.zip")=="https://example.test/new.zip"&&!memory.UsedCached,"clipboard_new_url_not_preferred");
            catalog.Select("en");
            var schema = SettingsSchema.Load(Path.Combine(AppContext.BaseDirectory, "settings.schema.json"));
            foreach (var palette in new[] { "classic", "emerald", "violet", "amber", "graphite" })
            foreach (var mode in new[] { "light", "dark" })
            {
                new UserSettings { Palette = palette, Appearance = mode }.ApplyTheme();
                double Luminance(string key)
                {
                    var c = ((System.Windows.Media.SolidColorBrush)app.Resources[key]).Color;
                    double Linear(byte v) { var n = v / 255.0; return n <= 0.04045 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4); }
                    return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
                }
                foreach (var foreground in new[] { "Ink", "MutedInk", "Accent" })
                foreach (var background in new[] { "Surface", "Chrome" })
                {
                    var a = Luminance(foreground); var b = Luminance(background);
                    Check((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) >= 4.5, "contrast:" + palette + ":" + mode + ":" + foreground + ":" + background);
                }
                foreach(var language in new[]{"en","fa"})foreach(var enabled in new[]{true,false}){
                    catalog.Select(language);var fixture=new StackPanel{FlowDirection=catalog.Direction};
                    foreach(var height in new[]{32d,36d,40d})fixture.Children.Add(new TextBox{Text=language=="en"?"Main Queue 12345 02:00":"صف اصلی ۱۲۳۴۵",Height=height,Width=260,IsEnabled=enabled});
                    fixture.Children.Add(new DatePicker{SelectedDate=new DateTime(2026,10,15),Height=36,IsEnabled=enabled});
                    var calendar=new Calendar{SelectedDate=new DateTime(2026,10,15),DisplayDate=new DateTime(2026,10,15)};fixture.Children.Add(calendar);
                    var shell=new Window{Content=fixture};Render(shell,Path.Combine(directory,"controls-"+palette+"-"+mode+"-"+language+"-"+enabled+".png"),350,440);CheckTextFit(fixture,palette+mode+language+enabled);calendar.DisplayMode=CalendarMode.Year;fixture.UpdateLayout();Check(Visuals(calendar).OfType<Grid>().First(g=>g.Name=="PART_YearView").Visibility==Visibility.Visible,"calendar_year_view_hidden");calendar.DisplayMode=CalendarMode.Decade;fixture.UpdateLayout();Check(Visuals(calendar).OfType<Grid>().First(g=>g.Name=="PART_YearView").Visibility==Visibility.Visible,"calendar_decade_view_hidden");shell.Close();
                }
                var menu=new MenuItem{Header="Theme fixture"};menu.Items.Add(new MenuItem{Header="Child"});menu.ApplyTemplate();
                var popup=(System.Windows.Controls.Primitives.Popup)menu.Template.FindName("PART_Popup",menu);
                var surface=(Border)popup.Child;Check(((System.Windows.Media.SolidColorBrush)surface.Background).Color==((System.Windows.Media.SolidColorBrush)app.Resources["Surface"]).Color,"submenu_surface:"+palette+":"+mode);
                var ink = Luminance("Ink"); var selection = Luminance("Selection");
                Check((Math.Max(ink, selection) + 0.05) / (Math.Min(ink, selection) + 0.05) >= 4.5, "selection_contrast:" + palette + ":" + mode);
            }
            var remembered=new UserSettings();remembered.WriteSecret("dialPassword","private-test-value");Check(!Serialized(remembered).Contains("private-test-value")&&remembered.ProtectedSecrets.Count==0,"dial_password_saved_without_permission");Check(remembered.Copy().ReadSecret("dialPassword")=="private-test-value","session_password_copy_lost");remembered.Preferences["dialSavePassword"]=true;remembered.WriteSecret("dialPassword",remembered.ReadSecret("dialPassword"));Check(remembered.ProtectedSecrets.ContainsKey("dialPassword")&&!Serialized(remembered).Contains("private-test-value"),"remembered_password_unencrypted");
            var results = new List<object>(); var fields = 0; var choiceSelections = 0; var dependencies = 0; var tableActions = 0;
            foreach (var language in new[] { "en", "fa" })
            {
                catalog.Select(language); new UserSettings { Appearance = language == "en" ? "light" : "dark" }.ApplyTheme();
                foreach (var section in schema.Sections)
                {
                    var settings = new UserSettings { Language = language, DownloadDirectory = Path.Combine(directory, "downloads") };
                    var initial = Serialized(settings); var savedPath = Path.Combine(directory, language + "-" + section.Id + ".json");
                    var window = new SettingsWindow(settings, catalog, schema, value => value.Save(savedPath), section.Id);
                    try
                    {
                        var surface = (FrameworkElement)window.Content; surface.Measure(new Size(990, 700)); surface.Arrange(new Rect(0, 0, 990, 700)); surface.UpdateLayout();
                        CheckTextFit(surface,"settings-"+language+section.Id);
                        Check(Get<Dictionary<string, FrameworkElement>>(window, "_editors").Count == section.Fields.Count, language + ":editor_count:" + section.Id);
                        foreach (var field in section.Fields)
                        {
                            var editor = Get<Dictionary<string, FrameworkElement>>(window, "_editors")[field.Key]; fields++;
                            Check(catalog.S(field.LabelKey) != field.LabelKey, "missing_label:" + field.Key);
                            if (editor is CheckBox check)
                            {
                                var before = check.IsChecked; check.IsChecked = before != true;
                                Check(((string)Invoke(window, "Value", field.Key)!).Equals((check.IsChecked == true).ToString(), StringComparison.OrdinalIgnoreCase), "toggle:" + field.Key);
                                check.IsChecked = before;
                            }
                            else if (editor is ComboBox combo)
                            {
                                var before = combo.SelectedIndex;
                                for (var index = 0; index < combo.Items.Count; index++) { combo.SelectedIndex = index; Check((string)Invoke(window, "Value", field.Key)! == combo.SelectedValue?.ToString(), "choice:" + field.Key); choiceSelections++; }
                                combo.SelectedIndex = before;
                            }
                            else if (editor is PasswordBox password)
                            {
                                password.Password = "TEST-SECRET-DO-NOT-PERSIST";
                                Check(!Serialized(Get<UserSettings>(window, "_draft")).Contains(password.Password), "secret_in_draft:" + field.Key);
                            }
                            else if (editor is TextBox input)
                            {
                                var before = input.Text;
                                if (field.Secret) { input.Text = "TEST-SECRET-DO-NOT-PERSIST"; Check(!Serialized(Get<UserSettings>(window, "_draft")).Contains(input.Text), "secret_text_in_draft:" + field.Key); input.Text = before; }
                                else if (field.Kind is "number" or "date")
                                {
                                    var draft = Get<UserSettings>(window, "_draft");
                                    if (field.Depends is { } dependency) { var parts = dependency.Split('=', 2); draft.Preferences[parts[0]] = parts.Length == 2 ? JsonValue.Create(parts[1]) : JsonValue.Create(true); Invoke(window, "UpdateDependencies"); }
                                    input.Text = "invalid-number-or-date";
                                    Check(!(bool)Invoke(window, "Commit", false)!, "invalid_accepted:" + field.Key);
                                    var search = Get<TextBox>(window, "_search"); search.Text = "__no_match__"; search.Clear();
                                    input = (TextBox)Get<Dictionary<string, FrameworkElement>>(window, "_editors")[field.Key];
                                    Check(input.Text == "invalid-number-or-date", "invalid_draft_lost:" + field.Key);
                                    input.Text = before;
                                    Check(!Get<HashSet<string>>(window, "_invalidInputs").Contains(field.Key), "error_not_cleared:" + field.Key);
                                }
                                else
                                {
                                    input.Text = field.Kind == "folder" ? Path.Combine(directory, "edited-downloads") : "settings-ui-test";
                                    Check((string)Invoke(window, "Value", field.Key)! == input.Text, "text_edit:" + field.Key); input.Text = before;
                                }
                            }
                            else if (editor is DataGrid grid)
                            {
                                var rows = (ObservableCollection<PreferenceRecord>)grid.ItemsSource; var count = rows.Count;
                                var actions = Get<Dictionary<string, (Button Add, Button Remove)>>(window, "_tableActions")[field.Key];
                                if(field.Key=="categories")QueueCategorySave(); actions.Add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(rows.Count == count + 1, "table_add:" + field.Key);
                                actions.Remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(rows.Count == count, "table_remove:" + field.Key);
                                if(field.Key=="categories")QueueCategorySave(); actions.Add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                foreach (var column in field.Columns ?? []) rows[^1][column.Key] = field.Key == "siteLogins" && column.Key == "username" ? "TEST-SECRET-DO-NOT-PERSIST" : field.Key=="siteLogins" && column.Key=="url" ? "https://example.test/private/" : field.Key=="serverRules" && column.Key=="connections" ? "4" : "fixture-" + column.Key;
                                Invoke(window, "SaveTables"); Check(Get<UserSettings>(window, "_draft").Preferences[field.Key] is JsonArray array && array.Count == count + 1, "table_commit:" + field.Key); tableActions += 3;
                            }
                        }
                        foreach (var field in section.Fields.Where(field => field.Depends is not null))
                        {
                            var parts = field.Depends!.Split('=', 2); var draft = Get<UserSettings>(window, "_draft"); var prior = draft.Preferences[parts[0]]?.DeepClone();
                            foreach (var enabled in new[] { false, true })
                            {
                                draft.Preferences[parts[0]] = parts.Length == 2 ? JsonValue.Create(enabled ? parts[1] : "__disabled__") : JsonValue.Create(enabled); Invoke(window, "UpdateDependencies");
                                Check(Get<Dictionary<string, FrameworkElement>>(window, "_editors")[field.Key].IsEnabled == enabled, "dependency:" + field.Key); dependencies++;
                            }
                            draft.Preferences[parts[0]] = prior; Invoke(window, "UpdateDependencies");
                        }
                        Check(Serialized(settings) == initial, "cancel_source_mutated:" + section.Id);
                        var validDraft=Get<UserSettings>(window,"_draft");
                        // Editor traversal exercises dependencies; supply valid network fixtures for commit.
                        validDraft.Preferences["temporaryFolder"]=Path.Combine(directory,"temp-fixture");validDraft.Preferences["proxyMode"]="none";validDraft.Preferences["socksEnabled"]=false;
                        if(validDraft.Preferences["serverRules"] is JsonArray serverRows)foreach(var r in serverRows.OfType<JsonObject>()){r["host"]="*.example.test";r["connections"]=4;}
                        if(validDraft.Preferences["siteLogins"] is JsonArray loginRows)foreach(var r in loginRows.OfType<JsonObject>())r["url"]="https://example.test/private/";
                        Check((bool)Invoke(window, "Commit", false)!, "commit_failed:" + section.Id);
                        var saved = File.ReadAllText(savedPath); Check(!saved.Contains("TEST-SECRET-DO-NOT-PERSIST"), "secret_persisted");
                        Check(Serialized(UserSettings.Load(savedPath)) == Serialized(Get<UserSettings>(window, "_draft")), "roundtrip:" + section.Id);
                        Check(!File.Exists(savedPath + ".tmp"), "staging_left_behind");
                        var draftNow = Get<UserSettings>(window, "_draft"); var otherKey = section.Fields.Any(field => field.Key == "parallel") ? "startup" : "parallel"; draftNow.Preferences[otherKey] = 7;
                        Invoke(window, "ResetSectionValues", section);
                        Check(draftNow.Preferences[otherKey]?.ToString() == "7", "reset_changed_other_section");
                        foreach (var field in section.Fields.Where(field => field.Key is not ("language" or "appearance" or "palette" or "downloadDirectory"))) Check(JsonNode.DeepEquals(draftNow.Preferences[field.Key], schema.Defaults[field.Key]), "reset_default:" + field.Key);
                        results.Add(new { language, section = section.Id, fields = section.Fields.Count, result = "PASS" });
                    }
                    finally { Release(window); }
                }
                var import = new ImportLinksWindow(catalog, Path.Combine(directory, "downloads"), new[] { "https://example.test/a.zip" }, "https://example.test/a.zip https://example.test/b.zip https://example.test/b.zip");
                var importContent = (FrameworkElement)import.Content; importContent.Measure(new Size(920, 720)); importContent.Arrange(new Rect(0, 0, 920, 720)); importContent.UpdateLayout();
                Check(Visuals(importContent).OfType<DataGrid>().Single().Columns[3].ActualWidth >= 300, "url_column_clipped");
                Check(Visuals(importContent).OfType<TextBlock>().Where(text => text.Text.StartsWith("https://example.test/")).All(text => text.ActualWidth > 250), "url_text_clipped");
                Check(import.SelectedUrls.SequenceEqual(new[] { "https://example.test/b.zip" }), "import_existing_selection");
                var importRows = Get<ObservableCollection<ImportLinkRow>>(import, "_links"); foreach (var row in importRows) row.Selected = false; Check(!Get<Button>(import, "_start").IsEnabled && !Get<Button>(import, "_later").IsEnabled, "empty_import_enabled");
                importRows[0].Selected = true; Check(Get<Button>(import, "_start").IsEnabled && import.SelectedUrls.Length == 1, "selection_not_live");
                var beforeInvalid=import.SelectedUrls.ToArray();Invoke(import,"AppendText","garbage javascript:alert(1) file:///c:/a.txt");Check(import.SelectedUrls.SequenceEqual(beforeInvalid),"invalid_paste_changed_existing_rows");
                var originalRow=importRows[0];Invoke(import,"AppendText","extra text https://example.test/c.zip https://example.test/b.zip");
                Check(importRows.Count==3&&ReferenceEquals(importRows[0],originalRow)&&originalRow.Selected,"append_reset_existing_rows");
                Check(importRows.Select(row=>row.Position).SequenceEqual(new[]{1,2,3}),"import_positions_not_stable");
                Check(!Visuals(importContent).OfType<TextBox>().Any(box=>box.AcceptsReturn),"import_has_freeform_text_area");
                Invoke(import,"RemoveRow",importRows[1]);Check(importRows.Select(row=>row.Position).SequenceEqual(new[]{1,2}),"remove_did_not_renumber");import.Close();
                var engine = new DownloadEngine(Path.Combine(directory, "progress-" + language));
                try
                {
                    var queues = new QueueScheduler(engine,Path.Combine(directory,"scheduler-"+language));
                    typeof(App).GetProperty(nameof(App.Engine))!.SetValue(app,engine);typeof(App).GetProperty(nameof(App.Scheduler))!.SetValue(app,queues);typeof(App).GetProperty(nameof(App.Locale))!.SetValue(app,catalog);typeof(App).GetProperty(nameof(App.Settings))!.SetValue(app,new UserSettings());
                    Check(schema.Defaults["closeToTray"]?.ToString().Equals("true",StringComparison.OrdinalIgnoreCase)==true,"close_to_tray_default");
                    Check(catalog.S("native.closeToTray")!="native.closeToTray","close_to_tray_localized");
                    app.Settings.Preferences["categories"]=new JsonArray(new JsonObject{{"id","research-fixture"},{"name","Research"},{"extensions","ABCXYZ"},{"folder",""}});
                    var customMain=new MainWindow(app);Check((string)Invoke(customMain,"Classify","paper.abcxyz")! == "category:research-fixture","custom_category_not_used_in_main_filter");Check(Get<Dictionary<string,TextBlock>>(customMain,"_categoryCounts").ContainsKey("category:research-fixture"),"custom_category_not_visible_in_sidebar");customMain.GetType().GetField("_canClose",Hidden)!.SetValue(customMain,true);customMain.Close();app.Settings.Preferences.Remove("categories");
                    var mainClose=new MainWindow(app);var closeArgs=new System.ComponentModel.CancelEventArgs();Invoke(mainClose,"ClosingAsync",mainClose,closeArgs);
                    Check(closeArgs.Cancel&&mainClose.WindowState==WindowState.Minimized&&!app.EngineIsDisposed,"close_without_tray_minimizes_and_keeps_engine");
                    Check(!Get<bool>(mainClose,"_closing"),"close_to_tray_does_not_begin_shutdown");
                    mainClose.GetType().GetField("_canClose",Hidden)!.SetValue(mainClose,true);mainClose.Close();
                    var closableSettings=new UserSettings();closableSettings.Preferences["closeToTray"]=false;var closeSettingsPath=Path.Combine(directory,"close-setting-"+language+".json");closableSettings.Save(closeSettingsPath);Check(UserSettings.Load(closeSettingsPath).Preferences["closeToTray"]?.ToString().Equals("false",StringComparison.OrdinalIgnoreCase)==true,"close_choice_persisted");
                    var etaRow=new DownloadRow(new DownloadJob{State=DownloadState.Running,PhaseKey="native.receiving",EstimatedSecondsRemaining=125},catalog);Check(etaRow.TimeLeft==catalog.S("native.remaining_minutes",("count",3)),"eta_minutes_localized");
                    foreach(var sideMode in new[]{"light","dark"}){
                        new UserSettings{Appearance=sideMode}.ApplyTheme();
                        var options=new SettingsWindow(new UserSettings(),catalog,schema,_=>{},"saveTo");Render(options,Path.Combine(directory,"options-"+language+"-"+sideMode+".png"),990,700);Pump(Task.Delay(25));Render(options,Path.Combine(directory,"options-"+language+"-"+sideMode+".png"),990,700);Check(Get<TreeView>(options,"_navigation").Items.OfType<TreeViewItem>().All(n=>!n.HasItems),"settings_navigation_still_nested");Check(!Get<StackPanel>(options,"_pageHeader").Children.OfType<WrapPanel>().Any() && Get<Dictionary<string,FrameworkElement>>(options,"_editors").ContainsKey("categories"),"category_folders_not_directly_in_save_settings");CheckTextFit((DependencyObject)options.Content,"options-side");Release(options);
                        var addWindow=new AddDownloadWindow(catalog,Path.Combine(directory,"downloads"));addWindow.UrlBox.Text="https://example.test/Movie.mkv";Render(addWindow,Path.Combine(directory,"add-"+language+"-"+sideMode+".png"),700,470);CheckTextFit((DependencyObject)addWindow.Content,"add-side");addWindow.Close();
                        var browserWindow=new BrowserIntegrationWindow(app);Render(browserWindow,Path.Combine(directory,"browser-"+language+"-"+sideMode+".png"),800,690);browserWindow.Close();
                        var credentials=new SiteCredentialsWindow(catalog,new PreferenceRecord{{"url","https://example.test/private/"}});Render(credentials,Path.Combine(directory,"credentials-"+language+"-"+sideMode+".png"),550,380);credentials.Close();
                        var grabber=new GrabberWindow(app,_=>Task.CompletedTask);Render(grabber,Path.Combine(directory,"grabber-"+language+"-"+sideMode+".png"),880,700);CheckTextFit((DependencyObject)grabber.Content,"grabber-side");grabber.Close();
                    }
                    var schedulerFixture1=engine.AddAsync("https://example.test/Series.S01E01.mkv",Path.Combine(directory,"downloads"),start:false).GetAwaiter().GetResult();var schedulerFixture2=engine.AddAsync("https://example.test/Series.S01E02.mkv",Path.Combine(directory,"downloads"),start:false).GetAwaiter().GetResult();queues.AssignAsync([schedulerFixture1.Id,schedulerFixture2.Id],"main").GetAwaiter().GetResult();
                    var schedulerWindow=new SchedulerWindow(app);schedulerWindow.LoadAsync().GetAwaiter().GetResult();
                    new UserSettings{Appearance=language=="en"?"dark":"light"}.ApplyTheme();
                    var schedulerContent=(FrameworkElement)schedulerWindow.Content;schedulerContent.Measure(new Size(1110,850));schedulerContent.Arrange(new Rect(0,0,1110,850));schedulerContent.UpdateLayout();
                    Render(schedulerWindow,Path.Combine(directory,"scheduler-"+language+"-disabled.png"),1110,850);CheckTextFit(schedulerContent,"scheduler-disabled");
                    Check(Visuals(schedulerContent).OfType<TabControl>().Single().ActualWidth>700,"scheduler_editor_column_collapsed");
                    Check(Get<ListBox>(schedulerWindow,"_queues").ActualWidth>=190,"queue_navigation_collapsed");
                    Get<CheckBox>(schedulerWindow,"_enabled").IsChecked=true;Check(Get<ComboBox>(schedulerWindow,"_kind").IsEnabled,"schedule_enable_dependency");
                    Render(schedulerWindow,Path.Combine(directory,"scheduler-"+language+"-enabled.png"),1110,850);CheckTextFit(schedulerContent,"scheduler-enabled");
                    var schedulerTabs=Visuals(schedulerContent).OfType<TabControl>().Single();schedulerTabs.SelectedIndex=1;Render(schedulerWindow,Path.Combine(directory,"scheduler-"+language+"-files.png"),1110,850);CheckTextFit(schedulerContent,"scheduler-files");schedulerTabs.SelectedIndex=2;Render(schedulerWindow,Path.Combine(directory,"scheduler-"+language+"-completion.png"),1110,850);CheckTextFit(schedulerContent,"scheduler-completion");schedulerTabs.SelectedIndex=0;
                    Get<CheckBox>(schedulerWindow,"_sync").IsChecked=true;Check(Get<TextBox>(schedulerWindow,"_interval").IsEnabled,"sync_interval_dependency");
                    var completion=Get<ComboBox>(schedulerWindow,"_completion");completion.SelectedItem=completion.Items.OfType<ComboBoxItem>().Single(item=>Equals(item.Tag,CompletionAction.ShutDown));Check(Get<CheckBox>(schedulerWindow,"_force").IsEnabled,"shutdown_force_dependency");
                    Get<TextBox>(schedulerWindow,"_name").Text="Persisted "+language;Get<TextBox>(schedulerWindow,"_delay").Text="60";
                    Check(((Task<bool>)Invoke(schedulerWindow,"SaveAsync")!).GetAwaiter().GetResult(),"scheduler_save_failed");
                    Check(queues.SnapshotAsync().GetAwaiter().GetResult().Single(q=>q.Id=="main") is {Name:var savedName,Completion.Action:CompletionAction.ShutDown,Synchronization:true} && savedName=="Persisted "+language,"scheduler_policy_not_saved");
                    schedulerWindow.Close();engine.RemoveAsync(schedulerFixture1.Id).GetAwaiter().GetResult();engine.RemoveAsync(schedulerFixture2.Id).GetAwaiter().GetResult();queues.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    var job = engine.AddAsync("https://example.test/progress.zip", Path.Combine(directory, "downloads"), "progress.zip", false).GetAwaiter().GetResult();
                    var compact = new ProgressWindow(engine, catalog, job.Id, new ProgressViewOptions(false, false, false)); Check(Get<TabControl>(compact, "_tabs").Items.Count == 1 && Get<Button>(compact, "_detailButton").Visibility == Visibility.Collapsed, "progress_view_preferences_ignored"); compact.Close();
                    var progress = new ProgressWindow(engine, catalog, job.Id);
                    try
                    {
                        ((Task)Invoke(progress, "RefreshForRenderAsync")!).GetAwaiter().GetResult();var closedByHide=false;progress.Closed+=(_,_)=>closedByHide=true;var stateBeforeHide=engine.SnapshotAsync().GetAwaiter().GetResult().Single().State;((Task)Invoke(progress,"HideToTrayAsync")!).GetAwaiter().GetResult();Check(!closedByHide&&progress.WindowState==WindowState.Minimized,"hide_without_tray_closes_window");Check(engine.SnapshotAsync().GetAwaiter().GetResult().Single().State==stateBeforeHide,"hide_changes_download_state");
                        Check(Get<TabControl>(progress, "_tabs").Items.Count == 3, "progress_tab_count");
                        foreach (var state in Enum.GetValues<DownloadState>())
                        {
                            var fixture = job.Copy(); fixture.State = state; fixture.TotalBytes = 1048576; fixture.ReceivedBytes = state == DownloadState.Completed ? 1048576 : 262144; fixture.ActiveSeconds = 2; fixture.TransferredBytes = 262144; fixture.BytesPerSecond = state == DownloadState.Running ? 131072 : 0; fixture.PhaseKey = state == DownloadState.Running ? "native.receiving" : "ui.paused"; fixture.ResumeSupported = true;
                            Invoke(progress, "RenderJob", fixture); Check(Get<Button>(progress, "_pause").IsEnabled == (state != DownloadState.Completed), "progress_pause_state"); Check(Get<Button>(progress, "_stopClose").IsEnabled == (state != DownloadState.Completed), "progress_stop_state");
                            if (state == DownloadState.Paused) Check(progress.TaskbarItemInfo.ProgressState == TaskbarItemProgressState.Paused, "paused_taskbar");
                            var content = (FrameworkElement)progress.Content; content.Measure(new Size(810, 850)); content.Arrange(new Rect(0, 0, 810, 850)); content.UpdateLayout();
                        }
                        var segmented = job.Copy(); segmented.State = DownloadState.Running; segmented.TotalBytes = 1048576; segmented.ActiveConnections = 8; segmented.ConnectionLimit = 8; segmented.Segments = Enumerable.Range(0, 8).Select(index => new DownloadSegment { Index = index, Start = index * 131072, End = (index + 1) * 131072 - 1, ReceivedBytes = 1024, PhaseKey = "native.receiving" }).ToList(); Invoke(progress, "RenderJob", segmented); Check(Get<DataGrid>(progress, "_connections").Items.Count == 8, "real_segment_rows_not_rendered"); Check(Get<Dictionary<string, TextBlock>>(progress, "_values")["native.active_connections"].Text == catalog.S("native.connection_count", ("count", 8), ("max", 8)), "active_connections_not_rendered");
                        var unknown = job.Copy(); unknown.State = DownloadState.Running; unknown.TotalBytes = null; Invoke(progress, "RenderJob", unknown); Check(Get<ProgressBar>(progress, "_bar").IsIndeterminate, "unknown_length_not_indeterminate");
                        Invoke(progress, "RenderJob", job); Check(!Get<ProgressBar>(progress, "_bar").IsIndeterminate, "known_state_kept_animation");
                        var detailButton = Get<Button>(progress, "_detailButton"); detailButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(Get<Border>(progress, "_details").Visibility == Visibility.Visible, "details_not_expanded"); detailButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(Get<Border>(progress, "_details").Visibility == Visibility.Collapsed, "details_not_collapsed");
                        for (var tab = 0; tab < 3; tab++) { Invoke(progress, "SelectTabForRender", tab); ((FrameworkElement)progress.Content).UpdateLayout(); }
                        Get<CheckBox>(progress, "_limitEnabled").IsChecked = true; Get<TextBox>(progress, "_limitValue").Text = "128"; Get<ComboBox>(progress, "_limitUnit").SelectedIndex = 0; Get<CheckBox>(progress, "_remember").IsChecked = true;
                        ((Task)Invoke(progress, "ApplyLimitAsync")!).GetAwaiter().GetResult(); Check(engine.SnapshotAsync().GetAwaiter().GetResult().Single().SpeedLimitBytesPerSecond == 131072, "progress_limit_not_applied");
                        Get<TextBox>(progress, "_limitValue").Text = "invalid"; ((Task)Invoke(progress, "ApplyLimitAsync")!).GetAwaiter().GetResult(); Check(engine.SnapshotAsync().GetAwaiter().GetResult().Single().SpeedLimitBytesPerSecond == 131072, "invalid_progress_limit_applied");
                        Get<CheckBox>(progress, "_limitEnabled").IsChecked = false; ((Task)Invoke(progress, "ApplyLimitAsync")!).GetAwaiter().GetResult(); Check(engine.SnapshotAsync().GetAwaiter().GetResult().Single().SpeedLimitBytesPerSecond == 0, "progress_limiter_not_disabled");
                        Get<CheckBox>(progress, "_showSummary").IsChecked = false; Get<CheckBox>(progress, "_closeWhenDone").IsChecked = true; var preferences = engine.SnapshotAsync().GetAwaiter().GetResult().Single(); Check(!preferences.ShowCompletionSummary && preferences.CloseProgressOnCompletion, "completion_preferences_not_applied");
                        for (var retry = 0; retry < 110; retry++) { var fixture = job.Copy(); fixture.RetryNumber = retry; Invoke(progress, "RenderJob", fixture); }
                        Check(Get<ObservableCollection<TransferEvent>>(progress, "_events").Count == 100, "event_history_unbounded");
                        Check(!Get<bool>(progress,"_detailsShown"),"progress_details_default_expanded");Check(progress.Width<=700&&progress.MinHeight<=450,"progress_not_compact");
                        var chart = new SpeedHistoryChart(); for (var point = 0; point < 80; point++) chart.AddSample(point); chart.AddSample(double.NaN); Check(chart.Samples == 60 && chart.Peak == 79, "chart_history_unbounded");
                        Get<Button>(progress, "_stopClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(engine.SnapshotAsync().GetAwaiter().GetResult().Single() is { State: DownloadState.Paused, InQueue: false }, "stop_close_did_not_stop");
                    }
                    finally { progress.Close(); }
                }
                finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                var ux = new SettingsWindow(new UserSettings { DownloadDirectory = Path.Combine(directory, "downloads") }, catalog, schema, _ => { });
                try { var checks = (string[])Invoke(ux, "VerifyUx")!; Check(checks.Length == 5, "ux_checks"); } finally { Release(ux); }
            }
            var report = new { result = "PASS", assertions = _assertions, sections = results.Count, fields, choiceSelections, dependencies, tableActions, networkListenersCreated = 0, externalCommandsExecuted = 0, results };
            File.WriteAllText(Path.Combine(directory, "settings-tests.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {_assertions} assertions; {results.Count} localized sections; {fields} field cases; {choiceSelections} choice selections; {dependencies} dependency cases; {tableActions} table actions. No network listener or system command created.");
            app.Shutdown(); return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); Console.Error.WriteLine(error); app.Shutdown(); return 1; }
    }
}





sealed class FakeDownloadTraySink:IDownloadTraySink{public bool Reject;public List<uint> Added{get;}=[];public List<uint> Removed{get;}=[];public List<DownloadJob> Updated{get;}=[];public DownloadJob? LastUpdated;public bool Add(uint id,DownloadJob job){if(Reject)return false;Added.Add(id);return true;}public void Update(uint id,DownloadJob job){LastUpdated=job.Copy();Updated.Add(job.Copy());}public void Remove(uint id)=>Removed.Add(id);}

