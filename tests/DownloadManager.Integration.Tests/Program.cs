using DownloadManager.Core;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;

var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var assertions = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); assertions++; }
async Task Until(Func<Task<bool>> test) { var end = DateTime.UtcNow.AddSeconds(20); while (!await test()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(20); } }
var now = new DateTimeOffset(2026,10,15,23,30,0,TimeSpan.Zero);
var schedule = new QueueSchedule { Enabled = true, Kind = ScheduleKind.Once, FirstDate = new(2026,10,15), StartTime = new(23,0), DurationMinutes = 120, TimeZoneId = "UTC" };
Check(schedule.Window(now) is not null, "once_start"); Check(schedule.Window(now.AddHours(1)) is not null, "midnight_window"); Check(schedule.Window(now.AddHours(2)) is null, "window_end");
schedule.Kind = ScheduleKind.Weekly; schedule.Days = [DayOfWeek.Friday]; Check(schedule.Window(now) is null,"weekday_filter"); schedule.Days=[DayOfWeek.Thursday]; Check(schedule.Window(now) is not null,"weekday_accept");
schedule.LastDate=new(2026,10,14); try {schedule.Validate();throw new Exception("invalid_range");}catch(ArgumentException){assertions++;} schedule.LastDate=null;
schedule.Enabled=false;Check(schedule.Window(now) is null,"disabled");schedule.Enabled=true;schedule.Kind=ScheduleKind.Once;schedule.FirstDate=new(2028,1,1);Check(schedule.Next(now)?.Year==2028,"future_next");
var dst = new QueueSchedule { Enabled=true, FirstDate=new(2026,3,8),StartTime=new(2,30),DurationMinutes=60,TimeZoneId="Eastern Standard Time" };
Check(dst.Window(new DateTimeOffset(2026,3,8,7,45,0,TimeSpan.Zero)) is null,"dst_nonexistent_time");
var clock = new TestClock(); var fake = new FakeExecutor();
foreach(var action in new[]{CompletionAction.ShutDown,CompletionAction.Sleep,CompletionAction.Hibernate,CompletionAction.ExitApplication})
{
 var countdown=new CompletionCountdown(new("ticket","main",new(){Action=action}),clock);Check(!await countdown.TickAsync(false,fake),"early_power_action");clock.Advance(61);Check(await countdown.TickAsync(false,fake),"action_not_fired");Check(!await countdown.TickAsync(false,fake),"action_replayed");
}
var cancelled=new CompletionCountdown(new("cancel","main",new(){Action=CompletionAction.ShutDown}),clock);cancelled.Cancel();clock.Advance(100);Check(!await cancelled.TickAsync(false,fake),"cancel_failed");
var busy=new CompletionCountdown(new("busy","main",new(){Action=CompletionAction.ShutDown}),clock);clock.Advance(100);Check(!await busy.TickAsync(true,fake)&&busy.Finished,"busy_not_cancelled");Check(fake.Actions.Count==4,"unexpected_power_actions");
await using var server=new FixtureServer();await server.StartAsync();var port=server.Port;
try
{
 var previewPolicy = new TransferSettings { Network = new NetworkPolicy { Mode = ProxySelection.Direct } };
 var beforePreview = server.Captured.Count;
 var preview = await LinkInspector.InspectAsync(server.Url("remote_mime.php"), previewPolicy);
 Check(preview.State == "ready" && preview.FileName == "remote_mime.mp4" && preview.Size > 0, "preview_mime_name_size_before_download");
 Check((await LinkInspector.InspectAsync(server.Url("preview-missing"), previewPolicy)).State == "missing", "preview_missing_is_distinct");
 Check((await LinkInspector.InspectAsync(server.Url("preview-no-head"), previewPolicy)).State == "unavailable", "preview_head_unsupported_not_broken_link");
 Check((await LinkInspector.InspectAsync(server.Url("forbidden"), previewPolicy)).State == "denied", "preview_auth_distinct");
 Check((await LinkInspector.InspectAsync(server.Url("preview-303"), previewPolicy)).FileName == "remote_mime.mp4", "preview_redirect_destination_name");
 var namedPreview = await LinkInspector.InspectAsync(server.Url("remote_control.php"), previewPolicy);
 Check(namedPreview.FileName == "ویدیو.mkv", "preview_disposition_utf8");
 var untypedPreview = await LinkInspector.InspectAsync(server.Url("untyped.php"), previewPolicy);
 Check(untypedPreview.FileName == "untyped.php", "preview_never_sniffs_body");
 Check(server.Captured.Skip(beforePreview).All(r => r.Method == "HEAD"), "preview_headers_only_no_get");
 var postPreviewCount = server.Captured.Count;
 var postPreview = await LinkInspector.InspectAsync(server.Url("remote_mime.php"), previewPolicy, new SessionRequest { Url = server.Url("remote_mime.php"), Method = "POST" });
 Check(postPreview.State == "unavailable" && server.Captured.Count == postPreviewCount, "preview_never_replays_post");
 var capturedMetadata = new BrowserResponseMetadata { Url = server.Url("remote_control.php"), ContentDisposition = "attachment; filename=browser.mp4", ContentType = "video/mp4", Size = 1234 };
 Check(capturedMetadata.ToMetadata().FileName == "browser.mp4" && capturedMetadata.ToMetadata().Size == 1234, "preview_browser_headers_without_extra_request");
 try { capturedMetadata.Validated(server.Url("other")); Check(false, "preview_metadata_scope_rejected"); } catch(ArgumentException) { Check(true, "preview_metadata_scope_rejected"); }
 // ETA must absorb connection bursts, respond to sustained changes, and hide when stalled.
 var estimate=new RemainingTimeEstimator();Check(estimate.Sample(0,0,1000000) is null,"eta_warmup");
 Check(estimate.Sample(1,10000,1000000) is null,"eta_no_early_guess");estimate.Sample(2,20000,1000000);
 var baseline=estimate.Sample(3,30000,1000000)!.Value;var burst=estimate.Sample(4,130000,1000000)!.Value;
 Check(burst>baseline/3,"eta_single_burst_not_tenfold_jump");
 for(var t=5;t<=25;t++)estimate.Sample(t,130000+(t-4)*20000,1000000);
 var settled=estimate.Sample(26,570000,1000000);Check(settled is >15 and <30,"eta_adapts_to_sustained_speed");
 Check(estimate.Sample(32,570000,1000000) is null,"eta_stall_hidden");estimate.Reset();Check(estimate.Sample(33,500000,1000000) is null,"eta_resume_warmup");Check(estimate.Sample(37,540000,null) is null,"eta_unknown_size");
 foreach(var connections in new[]{1,4}){
   var namesRoot=Path.Combine(root,"response-names-"+connections);var destination=Path.Combine(namesRoot,"files");
   await using var namedEngine=new DownloadEngine(Path.Combine(namesRoot,"state"));
   namedEngine.Configure(new(){MaxConnections=connections,RetryCount=0,CategoryRoot=destination,CategoryFolders=[new("mkv mp4","Video")]});
   async Task<DownloadJob> DownloadNamed(string url,string? custom=null){var item=await namedEngine.AddAsync(url,destination,custom);await Until(async()=>(await namedEngine.SnapshotAsync()).Single(j=>j.Id==item.Id).State is DownloadState.Completed or DownloadState.Failed);var done=(await namedEngine.SnapshotAsync()).Single(j=>j.Id==item.Id);Check(done.State==DownloadState.Completed,"metadata_download_complete:"+done.ErrorKey);Check(SHA256.HashData(await File.ReadAllBytesAsync(done.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"metadata_bytes_unchanged");return done;}
   var unicode=await DownloadNamed(server.Url("remote_control.php"));Check(unicode.FileName=="ویدیو.mkv","response_utf8_name_preferred");Check(unicode.Directory==Path.Combine(destination,"Video"),"response_name_category_routed");
   var collision=await DownloadNamed(server.Url("remote_control.php"));Check(collision.FileName=="ویدیو (1).mkv"&&File.Exists(unicode.FinalPath),"response_name_collision_safe");
   var mime=await DownloadNamed(server.Url("remote_mime.php"));Check(mime.FileName=="remote_mime.mp4","response_mime_extension");
   var custom=await DownloadNamed(server.Url("remote_control.php"),"my-choice.mkv");Check(custom.FileName=="my-choice.mkv","explicit_name_preserved");
   var safe=await DownloadNamed(server.Url("unsafe-name.php"));Check(safe.FileName=="outside.mkv"&&safe.Directory==Path.Combine(destination,"Video"),"server_path_traversal_contained");
   var untyped=await namedEngine.AddAsync(server.Url("untyped.php"),destination);await Until(async()=>(await namedEngine.SnapshotAsync()).Single(j=>j.Id==untyped.Id).State is DownloadState.Completed or DownloadState.Failed);untyped=(await namedEngine.SnapshotAsync()).Single(j=>j.Id==untyped.Id);
   Check(untyped.State==DownloadState.Completed&&untyped.FileName=="untyped.php","no_content_based_filename_guess");var expectedUntyped=server.CurrentData.ToArray();new byte[]{0x1a,0x45,0xdf,0xa3}.CopyTo(expectedUntyped,0);Check(SHA256.HashData(await File.ReadAllBytesAsync(untyped.FinalPath)).SequenceEqual(SHA256.HashData(expectedUntyped)),"unknown_type_bytes_preserved");
   server.RedirectTarget=server.Url("actual-video.mkv");var redirected=await DownloadNamed(server.Url("redirect"));Check(redirected.FileName=="actual-video.mkv"&&redirected.EffectiveUrl==server.RedirectTarget,"final_redirect_name");server.RedirectTarget=null;server.ResizeData(4*1024*1024);
 }
 var path=Path.Combine(root,"queues");string first,second;var q1=new DownloadQueue{Name="Night",Parallel=1};var q2=new DownloadQueue{Name="Other",Parallel=1};
 await using(var engine=new DownloadEngine(path))
 await using(var queues=new QueueScheduler(engine,path){GlobalParallel=2})
 {
  await queues.SaveQueueAsync(q1);await queues.SaveQueueAsync(q2);
  first=(await engine.AddAsync(server.Url("range"),Path.Combine(root,"files"),"first.bin",false)).Id;second=(await engine.AddAsync(server.Url("range"),Path.Combine(root,"files"),"second.bin",false)).Id;
  await queues.AssignAsync([first],q1.Id);await queues.AssignAsync([second],q2.Id);await queues.StartAsync(q1.Id,now);await queues.StartAsync(q2.Id,now);
  Check((await engine.SnapshotAsync()).Count(j=>j.State==DownloadState.Running)==2,"global_parallel");
  await queues.StopAsync(q1.Id);Check((await engine.SnapshotAsync()).Single(j=>j.Id==second).State==DownloadState.Running,"stop_other_queue");
  await queues.AssignAsync([second],q1.Id);Check((await engine.SnapshotAsync()).Single(j=>j.Id==second).State==DownloadState.Paused,"move_running_not_paused");
  await queues.MoveAsync(q1.Id,second,-1);Check((await queues.SnapshotAsync()).Single(q=>q.Id==q1.Id).JobIds.First()==second,"order_not_saved");
  q1=(await queues.SnapshotAsync()).Single(q=>q.Id==q1.Id);q1.Schedule=new(){Enabled=true,Kind=ScheduleKind.Once,FirstDate=new(2026,10,15),StartTime=new(23,0),DurationMinutes=120,TimeZoneId="UTC"};await queues.SaveQueueAsync(q1);
  await queues.TickAsync(now);Check((await engine.SnapshotAsync()).Any(j=>j.State==DownloadState.Running),"scheduled_start");await queues.StopAsync(q1.Id);await queues.TickAsync(now.AddSeconds(1));Check(!(await queues.SnapshotAsync()).Single(q=>q.Id==q1.Id).IsRunning,"manual_stop_restarted");
  await queues.StartAsync(q1.Id,now);await Until(async()=>(await engine.SnapshotAsync()).Any(j=>j.ReceivedBytes>65536));
 }
 await using(var engine=new DownloadEngine(path))
 await using(var queues=new QueueScheduler(engine,path){GlobalParallel=1})
 {
  await queues.TickAsync(now.AddSeconds(5));Check((await engine.SnapshotAsync()).Any(j=>j.State==DownloadState.Running),"restart_not_resumed");await queues.TickAsync(now.AddHours(2));
  // Manual starts have no scheduled cutoff. Apply a new scheduled occurrence below.
  await queues.StopAllAsync();
  var scheduled=(await queues.SnapshotAsync()).Single(q=>q.Id==q1.Id);scheduled.Schedule.FirstDate=new(2026,10,16);scheduled.Schedule.StartTime=new(1,0);scheduled.Schedule.DurationMinutes=1;await queues.SaveQueueAsync(scheduled);
  var begin=new DateTimeOffset(2026,10,16,1,0,0,TimeSpan.Zero);await queues.TickAsync(begin);Check((await queues.SnapshotAsync()).Single(q=>q.Id==q1.Id).IsRunning,"next_occurrence");await queues.TickAsync(begin.AddMinutes(2));Check(!(await engine.SnapshotAsync()).Any(j=>j.State==DownloadState.Running),"cutoff_failed");
 }
 // Successful completion creates one ticket; it never invokes an OS executor from the scheduler.
 var success=Path.Combine(root,"success");
 await using(var engine=new DownloadEngine(success))
 await using(var queues=new QueueScheduler(engine,success))
 {
  var queue=new DownloadQueue{Name="Finish",Completion=new(){Action=CompletionAction.ShutDown}};await queues.SaveQueueAsync(queue);
  var job=await engine.AddAsync(server.Url("range"),Path.Combine(root,"files"),"success.bin",false);await queues.AssignAsync([job.Id],queue.Id);await queues.StartAsync(queue.Id);
  await Until(async()=>(await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);await queues.TickAsync(DateTimeOffset.UtcNow);
  Check((await engine.SnapshotAsync()).Single().State==DownloadState.Completed,"scheduled_transfer_failed");Check(SHA256.HashData(await File.ReadAllBytesAsync(job.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"queue_hash");
  Check((await queues.DrainCompletionsAsync()).Count==1,"completion_ticket");Check((await queues.DrainCompletionsAsync()).Count==0,"completion_replay");
  await queues.StartAsync(queue.Id);Check((await queues.DrainCompletionsAsync()).Count==0,"empty_run_power_action");
 }
 // Synchronization preserves the last complete file until its replacement is fully verified.
 var syncState=Path.Combine(root,"synchronization");
 await using(var engine=new DownloadEngine(syncState))
 {
  var job=await engine.AddAsync(server.Url("range"),Path.Combine(root,"files"),"sync.bin");await Until(async()=>(await engine.SnapshotAsync()).Single().State==DownloadState.Completed);
  var original=SHA256.HashData(await File.ReadAllBytesAsync(job.FinalPath));Check(!await engine.SynchronizeAsync(job.Id),"unchanged_redownloaded");
  server.ChangeVersion();Check(await engine.SynchronizeAsync(job.Id),"changed_not_downloaded");await Until(async()=>(await engine.SnapshotAsync()).Single().ReceivedBytes>65536);await engine.PauseAsync(job.Id);
  Check(SHA256.HashData(await File.ReadAllBytesAsync(job.FinalPath)).SequenceEqual(original),"old_sync_file_destroyed_early");await engine.StartAsync(job.Id);await Until(async()=>(await engine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);
  Check((await engine.SnapshotAsync()).Single().State==DownloadState.Completed,"sync_replacement_failed");Check(SHA256.HashData(await File.ReadAllBytesAsync(job.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"sync_hash");
 }
 var quotaPath=Path.Combine(root,"quota-clock");Directory.CreateDirectory(quotaPath);var quotaClock=new TestClock();
 using(var quota=new TransferQuota(quotaPath,quotaClock))
 {
  quota.Configure(new(true,1024,1));var lease=await quota.ReserveAsync(1024,CancellationToken.None);quota.Refund(lease,512);Check(quota.Consumed==512,"quota_refund");
  var next=await quota.ReserveAsync(512,CancellationToken.None);Check(next.Granted==512&&quota.Consumed==1024,"quota_limit");
  using var cancel=new CancellationTokenSource(150);try{await quota.ReserveAsync(1,cancel.Token);throw new Exception("quota_exceeded");}catch(OperationCanceledException){assertions++;}
  quotaClock.Advance(3601);Check((await quota.ReserveAsync(1024,CancellationToken.None)).Granted==1024,"quota_window_not_reset");
 }
 using(var quota=new TransferQuota(quotaPath,quotaClock)){quota.Configure(new(true,1024,1));Check(quota.Consumed==1024,"quota_restart_lost");}
 var quotaState=Path.Combine(root,"quota-transfer");
 await using(var engine=new DownloadEngine(quotaState))
 {
  engine.Configure(new(){Quota=new(true,65536,1)});var job=await engine.AddAsync(server.Url("range"),Path.Combine(root,"files"),"quota.bin");
  await Until(()=>Task.FromResult(File.Exists(job.PartialPath)&&new FileInfo(job.PartialPath).Length>=65536));await Task.Delay(200);var pause=System.Diagnostics.Stopwatch.StartNew();await engine.PauseAsync(job.Id);
  Check(pause.ElapsedMilliseconds<1500,"quota_pause_slow");Check(new FileInfo(job.PartialPath).Length==65536,"quota_transfer_exceeded");
  engine.Configure(new(){Quota=new(false)});await engine.StartAsync(job.Id);await Until(async()=>(await engine.SnapshotAsync()).Single().State==DownloadState.Completed);Check(SHA256.HashData(await File.ReadAllBytesAsync(job.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"quota_resume_hash");
 }
 // Durable two-phase browser handoff: a prepared transfer cannot start until commit.
 var handoffPath=Path.Combine(root,"handoff");var handoffRequest=new BrowserRequest{Action="prepare",Urls=[server.Url("range")]};
 await using(var engine=new DownloadEngine(handoffPath))
 await using(var queues=new QueueScheduler(engine,handoffPath))
 {
  var store=new BrowserHandoffs(handoffPath);var handoffs=new BrowserHandoffService(engine,queues,store);
  Check(await handoffs.PrepareAsync(handoffRequest,handoffRequest.Urls,Path.Combine(root,"files"),"main",true,true,false,CancellationToken.None)==1,"handoff_prepare");
  var held=(await engine.SnapshotAsync()).Single();Check(held.PendingBrowserCommit&&!held.InQueue,"handoff_not_held");try{await engine.StartAsync(held.Id);throw new Exception("held_download_started");}catch(InvalidOperationException){assertions++;}
  Check(await handoffs.PrepareAsync(handoffRequest,handoffRequest.Urls,Path.Combine(root,"files"),"main",true,true,false,CancellationToken.None)==1 && (await engine.SnapshotAsync()).Count==1,"prepare_replayed");
 }
 await using(var engine=new DownloadEngine(handoffPath))
 await using(var queues=new QueueScheduler(engine,handoffPath))
 {
  var handoffs=new BrowserHandoffService(engine,queues,new BrowserHandoffs(handoffPath));var request=new BrowserRequest{Id=handoffRequest.Id,Action="commit"};Check((await handoffs.ExistingAsync(request))?.Ok==true,"commit_after_restart");
  Check((await handoffs.ExistingAsync(request))?.Status=="committed","commit_replay");await Until(async()=>(await engine.SnapshotAsync()).Single().State==DownloadState.Completed);
  Check(SHA256.HashData(await File.ReadAllBytesAsync((await engine.SnapshotAsync()).Single().FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"handoff_hash");
  var aborted=new BrowserRequest{Action="prepare",Urls=[server.Url("range")]};await handoffs.PrepareAsync(aborted,aborted.Urls,Path.Combine(root,"files"),"main",true,true,false,CancellationToken.None);
  Check((await handoffs.ExistingAsync(new(){Id=aborted.Id,Action="abort"}))?.Ok==true,"handoff_abort");Check((await engine.SnapshotAsync()).Count==1,"abort_kept_owned_entry");
 }
 // Protocol validates unsupported schemes, credential URLs and frame lengths before dispatch.
 foreach(var url in new[]{"file:///C:/x","javascript:alert(1)","https://user:pass@example.test/a","https://example.test/a\n"}) {try{new BrowserRequest{Urls=[url]}.Validated();throw new Exception("bad_url");}catch(ArgumentException){assertions++;}}
 using(var frame=new MemoryStream()){await BrowserProtocol.WriteAsync(frame,new BrowserRequest{Urls=["https://example.test/a"]});frame.Position=0;Check((await BrowserProtocol.ReadAsync<BrowserRequest>(frame))!.Validated().Urls.Length==1,"protocol_roundtrip");}
 using(var bad=new MemoryStream([255,255,255,127])){try{await BrowserProtocol.ReadAsync<BrowserRequest>(bad);throw new Exception("unbounded_frame");}catch(InvalidDataException){assertions++;}}
 var pipeData=Path.Combine(root,"pipe");await using(var pipeServer=new BrowserPipeServer(pipeData,(request,_)=>Task.FromResult(new BrowserReply(request.Id,true,"ready"))))
 {
  pipeServer.Start();using var client=new NamedPipeClientStream(".",BrowserProtocol.PipeName(pipeData),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await client.ConnectAsync(5000);await BrowserProtocol.WriteAsync(client,new BrowserRequest{Action="ping"});Check((await BrowserProtocol.ReadAsync<BrowserReply>(client))?.Ok==true,"named_pipe_roundtrip");
 }
 if(args.Length>1)
 {
  var hostData=Path.Combine(root,"host-pipe");await using var hostPipe=new BrowserPipeServer(hostData,(request,_)=>Task.FromResult(new BrowserReply(request.Id,true,"accepted",request.Urls.Length)));hostPipe.Start();
  var start=new System.Diagnostics.ProcessStartInfo(args[1]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
  start.ArgumentList.Add("downloadmanager@local.invalid");start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(hostData);start.ArgumentList.Add("--no-launch");
  using(var host=System.Diagnostics.Process.Start(start)!)
  {
   using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(10));var request=new BrowserRequest{Urls=["https://example.test/host.zip"]};await BrowserProtocol.WriteAsync(host.StandardInput.BaseStream,request,budget.Token);
   var reply=await BrowserProtocol.ReadAsync<BrowserReply>(host.StandardOutput.BaseStream,budget.Token);await host.WaitForExitAsync(budget.Token);Check(host.ExitCode==0&&reply is {Ok:true,Count:1}&&reply.Id==request.Id,"real_native_host_framing");
  }
 }

 var secret=UserSecret.Seal("fixture-private-password");Check(secret!="fixture-private-password"&&UserSecret.Open(secret)=="fixture-private-password","dpapi_roundtrip");
 var context=new SessionRequest{Url=server.Url("session"),Headers=new(){["Cookie"]="sid=fixture",["Referer"]=server.Url("page")}};
 var protectedContext=context.Protect();Check(!protectedContext.Contains("fixture")&&SessionRequest.Restore(protectedContext)?.Headers["Cookie"]=="sid=fixture","session_encryption");
 try{(context with {Headers=new(){["Host"]="evil.test"}}).Validated();throw new Exception("host_override");}catch(ArgumentException){assertions++;}
 try{(context with {Headers=new(){["Cookie"]="a=1\r\nHost: bad"}}).Validated();throw new Exception("header_injection");}catch(ArgumentException){assertions++;}
 await using(var secureEngine=new DownloadEngine(Path.Combine(root,"secure-engine")))
 {
  secureEngine.Configure(new(){MaxConnections=8,RetryCount=0,Network=new(){Mode=ProxySelection.Direct}});
  var job=await secureEngine.AddAsync(context.Url,Path.Combine(root,"secure-files"),"cookie.bin",session:context);await Until(async()=> (await secureEngine.SnapshotAsync()).Single(j=>j.Id==job.Id).State is DownloadState.Completed or DownloadState.Failed);
  var result=(await secureEngine.SnapshotAsync()).Single(j=>j.Id==job.Id);Check(result.State==DownloadState.Completed&&result.Segments.Count==8,"session_multipart");Check(SHA256.HashData(await File.ReadAllBytesAsync(result.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"session_sha");
  var post=new SessionRequest{Url=server.Url("post"),Method="POST",BodyBase64=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("token=fixture")),Headers=new(){["Content-Type"]="application/x-www-form-urlencoded"}};
  var posted=await secureEngine.AddAsync(post.Url,Path.Combine(root,"secure-files"),"post.bin",session:post);await Until(async()=> (await secureEngine.SnapshotAsync()).Single(j=>j.Id==posted.Id).State is DownloadState.Completed or DownloadState.Failed);
  result=(await secureEngine.SnapshotAsync()).Single(j=>j.Id==posted.Id);Check(result.State==DownloadState.Completed&&!result.ResumeSupported&&result.Segments.Count==0,"post_single_transfer");Check(server.Captured.Count(r=>r.Path=="post")==1,"post_replayed_by_probe");Check(!File.ReadAllText(Path.Combine(root,"secure-engine","downloads.json")).Contains("token=fixture"),"post_body_plaintext_persisted");
  var denied=await secureEngine.AddAsync(server.Url("forbidden"),Path.Combine(root,"secure-files"));await Until(async()=> (await secureEngine.SnapshotAsync()).Single(j=>j.Id==denied.Id).State==DownloadState.Failed);result=(await secureEngine.SnapshotAsync()).Single(j=>j.Id==denied.Id);Check(result.ErrorKey=="errors.http"&&result.ErrorDetail=="403"&&result.HttpStatus==403,"http_403_diagnostics");
 }
 using(var authClient=DownloadHttpClient.Create(new(){Mode=ProxySelection.Direct,Logins=[new(server.Url("auth"),"Basic",new System.Net.NetworkCredential("user","pass"))]}))
 using(var authResponse=await authClient.GetAsync(server.Url("auth"),System.Net.Http.HttpCompletionOption.ResponseHeadersRead)){Check(authResponse.IsSuccessStatusCode,"site_basic_auth");}
 using(var proxyClient=DownloadHttpClient.Create(new(){Mode=ProxySelection.Manual,Host="127.0.0.1",Port=port}))
 using(var proxyResponse=await proxyClient.GetAsync("http://download.example.test/proxy-file",System.Net.Http.HttpCompletionOption.ResponseHeadersRead)){Check(proxyResponse.IsSuccessStatusCode&&server.Captured.Any(r=>r.Path=="proxy-file"),"manual_proxy_route");}
 await using(var foreign=new FixtureServer())
 {
  await foreign.StartAsync();var foreignPort=foreign.Port;server.RedirectTarget=foreign.Url("range");
  var requestContext=context with{Url=server.Url("redirect"),Headers=new(){["Cookie"]="sid=fixture",["Authorization"]="Bearer private"}};
  using(var client=DownloadHttpClient.Create(new(){Mode=ProxySelection.Direct},true))using(var request=new HttpRequestMessage(HttpMethod.Get,requestContext.Url)){requestContext.Apply(request);using var response=await client.SendAsync(request,System.Net.Http.HttpCompletionOption.ResponseHeadersRead);Check(response.IsSuccessStatusCode,"redirect_failed");Check(foreign.Captured.TryPeek(out var captured)&&!captured.Headers.ContainsKey("Cookie")&&!captured.Headers.ContainsKey("Authorization"),"session_redirect_leak");}
  server.RedirectTarget=null;await foreign.DisposeAsync();Check(!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==foreignPort),"redirect_fixture_listener_leaked");
 }
 await using(var ftp=new FtpFixture())
 {
  ftp.Start();var ftpPort=ftp.Port;
  await using(var ftpEngine=new DownloadEngine(Path.Combine(root,"ftp")))
  {
   ftpEngine.Configure(new(){MaxConnections=8,RetryCount=0,FtpUser="fixture",FtpPassword="fixture-password"});
   var file=await ftpEngine.AddAsync(ftp.Url,Path.Combine(root,"ftp-files"));await Until(async()=> (await ftpEngine.SnapshotAsync()).Single().ReceivedBytes>=131072);await ftpEngine.PauseAsync(file.Id);
   var paused=(await ftpEngine.SnapshotAsync()).Single();Check(paused.State==DownloadState.Paused&&paused.ResumeSupported&&paused.ReceivedBytes>0,"ftp_pause");await ftpEngine.StartAsync(file.Id);await Until(async()=> (await ftpEngine.SnapshotAsync()).Single().State is DownloadState.Completed or DownloadState.Failed);
   var finished=(await ftpEngine.SnapshotAsync()).Single();Check(finished.State==DownloadState.Completed,"ftp_complete");Check(ftp.Offsets.Any(n=>n>0),"ftp_rest_resume");Check(SHA256.HashData(await File.ReadAllBytesAsync(finished.FinalPath)).SequenceEqual(SHA256.HashData(ftp.Data)),"ftp_sha256");
   Check(!File.ReadAllText(Path.Combine(root,"ftp","downloads.json")).Contains("fixture-password"),"ftp_password_leak");
  }
  await ftp.DisposeAsync();Check(!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==ftpPort||ftp.DataPorts.Contains(p.Port)),"ftp_port_leaked");
 }
 var grabbed=await WebsiteGrabber.ScanAsync(new(){StartUrl=server.Url("site/index.html"),Depth=1,Extensions="zip pdf",ExcludedPatterns="*excluded.zip"},new(){Mode=ProxySelection.Direct},"Fixture/1.0");
 Check(grabbed.Pages==2&&grabbed.Links.Count==2,"grabber_depth_and_filters");Check(grabbed.Links.All(l=>l.Url.StartsWith(server.Url("site/"),StringComparison.Ordinal)),"grabber_origin_folder_scope");
 var shallow=await WebsiteGrabber.ScanAsync(new(){StartUrl=server.Url("site/index.html"),Depth=0,Extensions="zip pdf"},new(){Mode=ProxySelection.Direct},"Fixture/1.0");Check(shallow.Pages==1&&shallow.Links.Count==1,"grabber_depth_zero");
 using(var cancelGrabber=new CancellationTokenSource()){cancelGrabber.Cancel();try{await WebsiteGrabber.ScanAsync(new(){StartUrl=server.Url("site/index.html")},new(),"Fixture/1.0",cancellation:cancelGrabber.Token);throw new Exception("grabber_cancel_ignored");}catch(OperationCanceledException){assertions++;}}
 await using(var cappedEngine=new DownloadEngine(Path.Combine(root,"concurrency")))
 await using(var cappedQueues=new QueueScheduler(cappedEngine,Path.Combine(root,"concurrency")){GlobalParallel=1})
 {
  var queue=new DownloadQueue{Name="One at once",Parallel=1};var other=new DownloadQueue{Name="Other",Parallel=2};await cappedQueues.SaveQueueAsync(queue);await cappedQueues.SaveQueueAsync(other);
  var a=await cappedEngine.AddAsync(server.Url("range"),Path.Combine(root,"cap-files"),"a.bin",false);var b=await cappedEngine.AddAsync(server.Url("range"),Path.Combine(root,"cap-files"),"b.bin",false);var c=await cappedEngine.AddAsync(server.Url("range"),Path.Combine(root,"cap-files"),"c.bin",false);
  await cappedQueues.AssignAsync([a.Id,b.Id],queue.Id);await cappedQueues.AssignAsync([c.Id],other.Id);await cappedQueues.ResumeSelectedAsync([a.Id,b.Id,c.Id]);var current=await cappedEngine.SnapshotAsync();
  Check(current.Count(j=>j.State==DownloadState.Running)==1,"overall_one_file_limit");Check(current.Count(j=>j.State==DownloadState.Queued)==2,"waiting_files_not_queued");
  cappedQueues.GlobalParallel=2;await cappedQueues.TickAsync(DateTimeOffset.UtcNow);current=await cappedEngine.SnapshotAsync();Check(current.Count(j=>j.State==DownloadState.Running)==2,"overall_two_file_limit");Check(current.Count(j=>(j.Id==a.Id||j.Id==b.Id)&&j.State==DownloadState.Running)==1,"per_queue_one_file_limit");
  await cappedQueues.StopAllAsync();Check(!(await cappedEngine.SnapshotAsync()).Any(j=>j.State==DownloadState.Running),"stop_all_owned_downloads");
 }

 await using(var removal=new DownloadEngine(Path.Combine(root,"removal-engine"))){
  var folder=Path.Combine(root,"removal-files");
  var keep=await removal.AddAsync(server.Url("range"),folder,"keep.bin",false);
  await File.WriteAllTextAsync(keep.FinalPath,"complete-keep");await File.WriteAllTextAsync(keep.PartialPath,"partial-keep");
  await removal.RemoveAsync(keep.Id,false);
  Check(File.Exists(keep.FinalPath)&&File.Exists(keep.PartialPath),"list_only_deleted_disk_files");
  Check(!(await removal.SnapshotAsync()).Any(j=>j.Id==keep.Id),"list_only_row_remains");
  var erase=await removal.AddAsync(server.Url("range"),folder,"erase.bin",false);
  await File.WriteAllTextAsync(erase.FinalPath,"complete-delete");await File.WriteAllTextAsync(erase.PartialPath,"partial-delete");
  var sibling=Path.Combine(folder,"unrelated.txt");await File.WriteAllTextAsync(sibling,"keep");
  await removal.RemoveAsync(erase.Id,true);
  Check(!File.Exists(erase.FinalPath)&&!File.Exists(erase.PartialPath),"delete_disk_files_remain");Check(File.Exists(sibling)&&File.Exists(keep.FinalPath),"delete_touched_unrelated_file");
  var locked=await removal.AddAsync(server.Url("range"),folder,"locked.bin",false);await File.WriteAllTextAsync(locked.FinalPath,"locked");
  using(var handle=new FileStream(locked.FinalPath,FileMode.Open,FileAccess.Read,FileShare.None)){try{await removal.RemoveAsync(locked.Id,true);throw new Exception("locked_delete_succeeded");}catch(IOException){assertions++;}}
  Check((await removal.SnapshotAsync()).Any(j=>j.Id==locked.Id),"failed_delete_lost_row");await removal.RemoveAsync(locked.Id,true);Check(!File.Exists(locked.FinalPath),"delete_retry_failed");
  var active=await removal.AddAsync(server.Url("range"),folder,"active.bin");await Until(async()=>File.Exists(active.PartialPath));await removal.RemoveAsync(active.Id,true);Check(!File.Exists(active.PartialPath)&&!(await removal.SnapshotAsync()).Any(j=>j.Id==active.Id),"running_delete_not_stopped");
 }

 var sequentialRoot=Path.Combine(root,"sequential-ten");server.ResizeData(262144);
 await using(var sequentialEngine=new DownloadEngine(sequentialRoot)){
  sequentialEngine.Configure(new(){MaxConnections=1,RetryCount=0});await using var sequential=new QueueScheduler(sequentialEngine,sequentialRoot);
  Check(sequential.GlobalParallel==1&&(await sequential.SnapshotAsync()).Single().Parallel==1,"sequential_not_default");
  var jobs=new List<DownloadJob>();for(var i=0;i<10;i++)jobs.Add(await sequentialEngine.AddAsync(server.Url("sequential-"+i),Path.Combine(sequentialRoot,"files"),i+".bin",false));
  await sequential.AssignAsync(jobs.Select(j=>j.Id),"main");await sequential.ResumeSelectedAsync(jobs.Select(j=>j.Id));
  await Until(async()=>{await sequential.TickAsync(DateTimeOffset.UtcNow);var snapshot=(await sequentialEngine.SnapshotAsync()).ToDictionary(j=>j.Id);Check(snapshot.Values.Count(j=>j.State==DownloadState.Running)<=1,"ten_files_started_together");var foundPending=false;foreach(var job in jobs){if(snapshot[job.Id].State!=DownloadState.Completed)foundPending=true;else Check(!foundPending,"queue_completion_order_changed");}return snapshot.Values.All(j=>j.State==DownloadState.Completed);});
  foreach(var job in jobs){Check(!File.Exists(job.PartialPath),"completed_temp_file_remains");Check(File.Exists(job.FinalPath),"sequential_final_missing");}
 }
 var legacyRoot=Path.Combine(root,"legacy-temp");Directory.CreateDirectory(legacyRoot);var oldJob=new DownloadJob{Id=Guid.NewGuid().ToString("N"),Directory=legacyRoot,FileName="legacy.bin",FinalPath=Path.Combine(legacyRoot,"legacy.bin"),Url=server.Url("range"),State=DownloadState.Paused};oldJob.PartialPath=Path.Combine(legacyRoot,".dm-"+oldJob.Id+".part");await File.WriteAllTextAsync(oldJob.PartialPath,"saved-partial-bytes");await File.WriteAllTextAsync(Path.Combine(legacyRoot,"downloads.json"),JsonSerializer.Serialize(new[]{oldJob}));
 await using(var migrated=new DownloadEngine(legacyRoot)){var job=(await migrated.SnapshotAsync()).Single();Check(!File.Exists(oldJob.PartialPath)&&File.Exists(job.PartialPath),"legacy_temp_not_moved");Check(await File.ReadAllTextAsync(job.PartialPath)=="saved-partial-bytes","legacy_temp_corrupted");Check(Path.GetFileName(Path.GetDirectoryName(job.PartialPath))=="Temporary Downloads","legacy_temp_wrong_folder");}
 var folderDefault=Path.Combine(root,"category-default");var folderCustom=Path.Combine(root,"separate-location","Videos");
 await using(var folderEngine=new DownloadEngine(Path.Combine(root,"category-folder-engine"))){
  folderEngine.Configure(new(){MaxConnections=1,CategoryRoot=folderDefault,CategoryFolders=[new("MKV",folderCustom),new("ZIP","")]});
  var custom=await folderEngine.AddAsync(server.Url("range"),folderDefault,"Movie.mkv");await Until(async()=>(await folderEngine.SnapshotAsync()).Single(j=>j.Id==custom.Id).State==DownloadState.Completed);
  Check(custom.Directory==folderCustom&&SHA256.HashData(await File.ReadAllBytesAsync(custom.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"category_custom_absolute_download_path_and_sha");
  var fallback=await folderEngine.AddAsync(server.Url("range"),folderDefault,"Archive.zip");await Until(async()=>(await folderEngine.SnapshotAsync()).Single(j=>j.Id==fallback.Id).State==DownloadState.Completed);
  Check(fallback.Directory==folderDefault,"category_empty_folder_downloads_to_default");
  folderEngine.Configure(new(){MaxConnections=1,CategoryRoot=folderDefault,CategoryFolders=[new("MKV","")]});
  var cleared=await folderEngine.AddAsync(server.Url("range"),folderDefault,"Second.mkv",start:false);Check(cleared.Directory==folderDefault,"category_cleared_path_does_not_keep_old_location");
  var explicitFolder=Path.Combine(root,"explicit-download-folder");var explicitJob=await folderEngine.AddAsync(server.Url("range"),explicitFolder,"Third.mkv",start:false);Check(explicitJob.Directory==explicitFolder,"category_overrides_explicit_user_folder");
 }
 Check(DownloadDestination.Resolve(@"C:\Downloads","Movie.mkv",new(){CategoryRoot=@"C:\Downloads",CategoryFolders=[new("MKV",@"Z:\Videos")]})==@"Z:\Videos","category_cross_drive_resolution_without_io");
 var categorizedRoot=Path.Combine(root,"categorized");
 await using(var categorized=new DownloadEngine(Path.Combine(root,"categorized-engine"))){
  categorized.Configure(new(){MaxConnections=1,CategoryRoot=categorizedRoot,CategoryFolders=[new("MKV MP4","Video")],TemporaryDirectory=Path.Combine(root,"custom-temp")});
  var movie=await categorized.AddAsync(server.Url("range"),categorizedRoot,"Movie.MKV");await Until(async()=>(await categorized.SnapshotAsync()).Single().State==DownloadState.Completed);
  Check(Path.GetDirectoryName(movie.FinalPath)==Path.Combine(categorizedRoot,"Video"),"mkv_not_in_video");Check(Path.GetDirectoryName(movie.PartialPath)==Path.Combine(root,"custom-temp"),"custom_temp_ignored");Check(!File.Exists(movie.PartialPath),"custom_temp_not_cleaned");Check(SHA256.HashData(await File.ReadAllBytesAsync(movie.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"categorized_output_corrupted");
  var explicitFolder=Path.Combine(root,"explicit-save");var custom=await categorized.AddAsync(server.Url("range"),explicitFolder,"Movie.MKV",false);Check(Path.GetDirectoryName(custom.FinalPath)==explicitFolder,"explicit_folder_overridden");
 }
 var sharedData=Path.Combine(root,"shared-temp-engine");
 await using(var shared=new DownloadEngine(sharedData)){
  shared.Configure(new(){MaxConnections=1,BytesPerSecond=32768});var videoFolder=Path.Combine(root,"final-video");var audioFolder=Path.Combine(root,"final-audio");
  var video=await shared.AddAsync(server.Url("range"),videoFolder,"video.mkv",false);var audio=await shared.AddAsync(server.Url("range"),audioFolder,"audio.mp3",false);
  Check(Path.GetDirectoryName(video.PartialPath)==Path.GetDirectoryName(audio.PartialPath),"categories_have_separate_temp_folders");Check(Path.GetDirectoryName(video.PartialPath)==Path.Combine(sharedData,"Temporary Downloads"),"default_temp_inside_final_folder");
  await shared.StartAsync(video.Id);await Until(async()=>(await shared.SnapshotAsync()).Single(j=>j.Id==video.Id).ReceivedBytes>0);await shared.PauseAsync(video.Id);var partial=(await shared.SnapshotAsync()).Single(j=>j.Id==video.Id);
  var newTemp=Path.Combine(root,"moved-shared-temp");shared.Configure(new(){MaxConnections=1,TemporaryDirectory=newTemp});await shared.StartAsync(video.Id);
  Check((await shared.SnapshotAsync()).Single(j=>j.Id==video.Id).PartialPath==Path.Combine(newTemp,Path.GetFileName(partial.PartialPath)),"resume_did_not_relocate_to_selected_temp");Check(!File.Exists(partial.PartialPath),"old_temp_left_after_relocation");
  await Until(async()=>(await shared.SnapshotAsync()).Single(j=>j.Id==video.Id).State==DownloadState.Completed);Check(SHA256.HashData(await File.ReadAllBytesAsync(video.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)),"relocated_resume_sha_mismatch");Check(!File.Exists(Path.Combine(newTemp,Path.GetFileName(partial.PartialPath))),"relocated_temp_not_cleaned");
 }
 var dialFake=new FakeDial();var prerequisite=new ConnectionPrerequisite(dialFake);
 await prerequisite.EnsureAsync(new(),CancellationToken.None);Check(dialFake.Calls==0,"disabled_dial_started");
 await prerequisite.EnsureAsync(new(true,"Fixture VPN",Attempts:1),CancellationToken.None);Check(dialFake.Calls==1,"dial_prerequisite_not_called");
 using(var dialCancel=new CancellationTokenSource()){dialCancel.Cancel();try{await prerequisite.EnsureAsync(new(true,"Fixture VPN"),dialCancel.Token);throw new Exception("dial_cancel_ignored");}catch(OperationCanceledException){assertions++;}}
 Check(dialFake.Calls==1,"cancelled_dial_invoked");
 Check(fake.Actions.Count==4,"real_os_action_leaked");
}
finally {await server.DisposeAsync();}
Check(!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==port),"listener_not_closed");
Console.WriteLine($"PASS {assertions} integration assertions; queue/schedule/restart/SHA-256/IPC/countdown. Test loopback port {port} closed. All OS actions used FakeExecutor only.");
sealed class FakeExecutor:ICompletionExecutor { public List<CompletionAction> Actions{get;}=[];public Task ExecuteAsync(CompletionOptions options){Actions.Add(options.Action);return Task.CompletedTask;} }
sealed class TestClock:TimeProvider { private long _stamp;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>_stamp;public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,15,0,0,0,TimeSpan.Zero).AddMilliseconds(_stamp);public void Advance(int seconds)=>_stamp+=seconds*1000; }

sealed class FakeDial:IDialTransport{public int Calls;public Task ConnectAsync(DialPolicy policy,CancellationToken cancellation){Calls++;return Task.CompletedTask;}}
