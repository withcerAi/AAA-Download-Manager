using DownloadManager.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var root=Path.GetFullPath(args[0]);var extension=Path.GetFullPath(args[1]);var host=Path.GetFullPath(args[2]);var wrapper=Path.GetFullPath(args[3]);var edge=Path.GetFullPath(args[4]);Directory.CreateDirectory(root);
var scenario=args.Length>5?args[5]:"plain";var firefox=args.Length>6&&args[6]=="firefox";var chrome=args.Length>6&&args[6]=="chrome";
var data=Path.Combine(root,"data");Directory.CreateDirectory(data);var id=firefox?"downloadmanager@local.invalid":"gibcnlmfmdabncpgkkhiilhoajbbdifa";var name="com.downloadmanager.test."+Guid.NewGuid().ToString("N");var registry=(firefox?@"Software\Mozilla\NativeMessagingHosts\":chrome?@"Software\Google\Chrome\NativeMessagingHosts\":@"Software\Microsoft\Edge\NativeMessagingHosts\")+name;
var wrapperConfig=Path.Combine(Path.GetDirectoryName(wrapper)!,"test-config.json");File.WriteAllText(wrapperConfig,JsonSerializer.Serialize(new{host,data}));
var allow=Path.Combine(Path.GetDirectoryName(host)!,"e2e.host.json");File.WriteAllText(allow,JsonSerializer.Serialize(new{allowed_origins=new[]{"chrome-extension://"+id+"/"},allowed_extensions=new[]{id}}));
await using var server=new FixtureServer();await server.StartAsync();var port=server.Port;
server.BrowserPage=scenario=="post"?"<form method=post action=/post.zip><input name=token value=fixture><button>Download</button></form>":"<a href=/session.zip>Download</a>";
await using var engine=new DownloadEngine(data);engine.Configure(new(){MaxConnections=8,RetryCount=0});await using var scheduler=new QueueScheduler(engine,data);scheduler.Start();var service=new BrowserHandoffService(engine,scheduler,new BrowserHandoffs(data));var actions=new List<string>(); BrowserResponseMetadata? receivedMetadata = null;
await using var pipe=new BrowserPipeServer(data,async(request,token)=>{
 lock(actions)actions.Add(request.Action); if(request.Action=="import") receivedMetadata=request.ResponseMetadata;
 if(request.Action=="ping")return new(request.Id,true,"ready");
 if(request.Action=="import"){var count=await service.PrepareAsync(request,request.Urls,Path.Combine(root,"desktop-files"),"main",true,true,false,token);return await service.ExistingAsync(new BrowserRequest{Id=request.Id,Action="commit"})??new(request.Id,false,"commitFailed");}
 if(request.Action=="prepare"){var count=await service.PrepareAsync(request,request.Urls,Path.Combine(root,"desktop-files"),"main",true,true,false,token);return new(request.Id,true,"prepared",count);}
 return await service.ExistingAsync(request)??new(request.Id,false,"unknown");
});pipe.Start();
var testExtension=Path.Combine(root,"extension");Directory.CreateDirectory(testExtension);foreach(var file in Directory.EnumerateFiles(extension,"*",SearchOption.AllDirectories)){var relative=Path.GetRelativePath(extension,file);var target=Path.Combine(testExtension,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);}
var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(testExtension,"manifest.json")))!.AsObject();manifest["host_permissions"]=new JsonArray("http://*/*","https://*/*");manifest["permissions"]!.AsArray().Add("cookies");manifest["permissions"]!.AsArray().Add("downloads");File.WriteAllText(Path.Combine(testExtension,"manifest.json"),manifest.ToJsonString());
var backgroundPath=Path.Combine(testExtension,"background.js");var background=File.ReadAllText(backgroundPath).Replace("com.downloadmanager.bridge",name,StringComparison.Ordinal);
background=background.Replace("void api.runtime.lastError;finish(new Error(\"bridgeUnavailable\"));","const problem=api.runtime.lastError;finish(new Error(\"native disconnect: \"+(problem?.message||port.error?.message||\"unknown\")));",StringComparison.Ordinal);
var selfTest="""
;(async()=>{
 const existing=await api.storage.session.get("e2eStarted");if(existing.e2eStarted)return;await api.storage.session.set({e2eStarted:true});
 try{
  if(SCENARIO==="missing"){
   await toggleCapture(Promise.resolve(true)); if(routingEnabled||(await api.storage.local.get(defaults)).enabled)throw Error("missing_host_enabled");
   const scripts=await api.scripting.getRegisteredContentScripts();if(scripts.length)throw Error("missing_host_scripts");
   if(!IS_FIREFOX&&(await api.declarativeNetRequest.getDynamicRules()).length)throw Error("missing_host_rules");
   const id=await api.downloads.download({url:OFF_URL,filename:"browser-off.zip"});let item;const deadline=Date.now()+20000;
   do{[item]=await api.downloads.search({id});if(item.state==="complete")break;await new Promise(r=>setTimeout(r,100));}while(Date.now()<deadline);
   await fetch(REPORT_URL,{method:"POST",body:JSON.stringify({ok:item.state==="complete",missingHostRemainsOff:true,offBrowserState:item.state})});return;
  }
  await toggleCapture(Promise.resolve(true));if(!routingEnabled)throw Error("routing_rules_rejected");
  const ready=await native({version:1,id:crypto.randomUUID(),action:"ping",urls:[]});if(!ready.ok)throw Error("native_ping");
  if(SCENARIO==="cookie")await api.cookies.set({url:COOKIE_URL,name:"sid",value:"fixture",path:"/"});
  const safety=await api.tabs.create({url:SAFETY_URL});await new Promise(r=>setTimeout(r,1800));
  const safeResult=await api.scripting.executeScript({target:{tabId:safety.id},func:async()=>{const response=await fetch('/manifest.json');const text=await response.text();const video=document.createElement('video');video.src='/online.mp4';video.preload='metadata';document.body.append(video);return response.ok&&text.length>0;}});
  await new Promise(r=>setTimeout(r,1200));if(!safeResult[0]?.result||(await api.storage.session.get('lastStatus')).lastStatus==='accepted'||(await api.downloads.search({})).length)throw Error('passive_resource_hijacked');
  const tab=await api.tabs.create({url:DOWNLOAD_URL});
  if(["cookie","post"].includes(SCENARIO)){await new Promise(resolve=>setTimeout(resolve,1500));await api.scripting.executeScript({target:{tabId:tab.id},func:()=>{const form=document.querySelector("form");if(form)form.requestSubmit();else document.querySelector("a").click();}});}
  if(SCENARIO==="inplace"){await new Promise(r=>setTimeout(r,1500));await api.scripting.executeScript({target:{tabId:tab.id},func:()=>location.assign('/header-download')});}
  const end=Date.now()+25000;let status;while(Date.now()<end){status=(await api.storage.session.get("lastStatus")).lastStatus;if(status==="accepted"||status==="bridgeUnavailable")break;await new Promise(r=>setTimeout(r,100));}
  const onItems=await api.downloads.search({});if(status!=="accepted"||onItems.length)throw Error("ON_interception_failed:"+status+":"+onItems.length);
  await new Promise(r=>setTimeout(r,700));const returnedTab=await api.tabs.get(tab.id);if(returnedTab.url?.startsWith(api.runtime.getURL("")))throw Error("intermediary_page_opened");
  let documentState;try{documentState=(await api.scripting.executeScript({target:{tabId:tab.id},func:()=>({url:location.href,body:document.body.innerText.slice(0,300)})}))[0]?.result;}catch(error){documentState={error:String(error)};}
  if(SCENARIO==="inplace"&&!documentState?.url?.endsWith("/browser-source"))throw Error("source_page_not_restored:"+JSON.stringify(documentState));
  await toggleCapture(Promise.resolve(true));if(routingEnabled)throw Error("OFF_failed");
  const id=await api.downloads.download({url:OFF_URL,filename:"browser-off.zip"});
  let item;const deadline=Date.now()+20000;do{[item]=await api.downloads.search({id});if(item.state==="complete")break;await new Promise(r=>setTimeout(r,100));}while(Date.now()<deadline);
  await fetch(REPORT_URL,{method:"POST",body:JSON.stringify({ok:item.state==="complete",status,documentState,onBrowserDownloads:onItems.length,offBrowserState:item.state})});
 }catch(error){await fetch(REPORT_URL,{method:"POST",body:JSON.stringify({ok:false,error:String(error)})});}
})();
""";
selfTest=selfTest.Replace("SAFETY_URL",JsonSerializer.Serialize(server.Url("browser-source"))).Replace("DOWNLOAD_URL",JsonSerializer.Serialize(server.Url(scenario is "cookie" or "post" or "inplace"?"browser-source":scenario=="header"?"header-download":"browser-fixture.zip"))).Replace("COOKIE_URL",JsonSerializer.Serialize(server.Url("session.zip"))).Replace("SCENARIO",JsonSerializer.Serialize(scenario)).Replace("OFF_URL",JsonSerializer.Serialize(server.Url("browser-off.zip"))).Replace("REPORT_URL",JsonSerializer.Serialize(server.Url("extension-report")));File.WriteAllText(backgroundPath,background+selfTest);
var downloads=Path.Combine(root,"browser-files");Directory.CreateDirectory(downloads);var profile=Path.Combine(root,"browser-profile");Directory.CreateDirectory(Path.Combine(profile,"Default"));File.WriteAllText(Path.Combine(profile,"Default","Preferences"),JsonSerializer.Serialize(new{download=new{default_directory=downloads,prompt_for_download=false}}));
var nativeManifest=Path.Combine(root,name+".json");var hostManifest=new Dictionary<string,object>{{"name",name},{"description","Isolated Download Manager test"},{"path",wrapper},{"type","stdio"}};hostManifest[firefox?"allowed_extensions":"allowed_origins"]=new[]{firefox?id:"chrome-extension://"+id+"/"};File.WriteAllText(nativeManifest,JsonSerializer.Serialize(hostManifest));
if(scenario!="missing") { using var key=Registry.CurrentUser.CreateSubKey(registry); key.SetValue("",nativeManifest); }
Process? browser=null;HttpClient? webdriver=null;string? webdriverSession=null;int driverPort=0,marionettePort=0,cdpPort=0;Task<string>? driverErrors=null,driverOutput=null;
static int FreePort(){var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();var value=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return value;}

try{
 if(firefox){
  driverPort=FreePort();marionettePort=FreePort();var driver=new ProcessStartInfo(args[7]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
  foreach(var arg in new[]{"--host","127.0.0.1","--port",driverPort.ToString(),"--marionette-host","127.0.0.1","--marionette-port",marionettePort.ToString(),"--websocket-port","0","--profile-root",profile})driver.ArgumentList.Add(arg);
  browser=Process.Start(driver)!;driverErrors=browser.StandardError.ReadToEndAsync();driverOutput=browser.StandardOutput.ReadToEndAsync();webdriver=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+driverPort),Timeout=TimeSpan.FromSeconds(60)};
  var readyUntil=DateTime.UtcNow.AddSeconds(15);while(true){try{using var status=await webdriver.GetAsync("/status");if(status.IsSuccessStatusCode)break;}catch(HttpRequestException){}if(DateTime.UtcNow>readyUntil)throw new TimeoutException("geckodriver startup");await Task.Delay(100);}
  async Task<JsonNode> Post(string path,object body){using var response=await webdriver.PostAsync(path,new StringContent(JsonSerializer.Serialize(body),System.Text.Encoding.UTF8,"application/json"));var text=await response.Content.ReadAsStringAsync();if(!response.IsSuccessStatusCode)throw new Exception("WebDriver failed: "+text);return JsonNode.Parse(text)!;}
  var options=new JsonObject{["binary"]=edge,["args"]=new JsonArray("-headless"),["prefs"]=new JsonObject{["browser.download.dir"]=downloads,["browser.download.folderList"]=2,["browser.download.useDownloadDir"]=true,["browser.helperApps.neverAsk.saveToDisk"]="application/octet-stream,application/zip",["browser.download.always_ask_before_handling_new_types"]=false,["browser.shell.checkDefaultBrowser"]=false,["browser.startup.homepage"]="about:blank",["browser.newtabpage.enabled"]=false,["extensions.logging.enabled"]=true,["devtools.console.stdout.chrome"]=true,["toolkit.telemetry.enabled"]=false,["datareporting.healthreport.uploadEnabled"]=false,["datareporting.policy.dataSubmissionEnabled"]=false,["app.normandy.enabled"]=false}};
  var caps=new JsonObject{["browserName"]="firefox",["moz:firefoxOptions"]=options};var session=await Post("/session",new{capabilities=new{alwaysMatch=caps}});webdriverSession=session["value"]!["sessionId"]!.GetValue<string>();
  var addon=Path.Combine(root,"test-extension.xpi");System.IO.Compression.ZipFile.CreateFromDirectory(testExtension,addon);await Post("/session/"+webdriverSession+"/moz/addon/install",new{path=addon,temporary=true});
 }else{
 var start=new ProcessStartInfo(edge){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};var flags=new List<string>{"--headless=new","--no-first-run","--no-default-browser-check","--user-data-dir="+profile};
 if(chrome)flags.AddRange(["--remote-debugging-port=0","--remote-debugging-address=127.0.0.1","--enable-unsafe-extension-debugging"]);else flags.AddRange(["--disable-extensions-except="+testExtension,"--load-extension="+testExtension]);flags.Add("about:blank");foreach(var flag in flags)start.ArgumentList.Add(flag);
 browser=Process.Start(start)!;driverErrors=browser.StandardError.ReadToEndAsync();driverOutput=browser.StandardOutput.ReadToEndAsync();
 if(chrome){
  var activePort=Path.Combine(profile,"DevToolsActivePort");var deadline=DateTime.UtcNow.AddSeconds(15);while(!File.Exists(activePort)){if(DateTime.UtcNow>deadline)throw new TimeoutException("Chrome debug endpoint");await Task.Delay(100);}
  var address=File.ReadAllLines(activePort);cdpPort=int.Parse(address[0]);using var socket=new System.Net.WebSockets.ClientWebSocket();using var debugTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));await socket.ConnectAsync(new Uri("ws://127.0.0.1:"+cdpPort+address[1]),debugTimeout.Token);
  var message=System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id=1,method="Extensions.loadUnpacked",@params=new{path=testExtension}}));await socket.SendAsync(message,System.Net.WebSockets.WebSocketMessageType.Text,true,debugTimeout.Token);var buffer=new byte[65536];while(true){using var response=new MemoryStream();System.Net.WebSockets.WebSocketReceiveResult chunk;do{chunk=await socket.ReceiveAsync(buffer,debugTimeout.Token);response.Write(buffer,0,chunk.Count);}while(!chunk.EndOfMessage);using var reply=JsonDocument.Parse(response.ToArray());if(reply.RootElement.TryGetProperty("id",out var replyId)&&replyId.GetInt32()==1){if(reply.RootElement.TryGetProperty("error",out var error))throw new Exception("Chrome test extension load: "+error);break;}}
 }
 }
 var report=await server.ExtensionReport.Task.WaitAsync(TimeSpan.FromSeconds(45));File.WriteAllText(Path.Combine(root,"browser-report.json"),report);
 using var result=JsonDocument.Parse(report);if(!result.RootElement.GetProperty("ok").GetBoolean())throw new Exception("Browser native handoff failed: "+report);
 if(scenario=="missing"){
  if(actions.Count!=0||(await engine.SnapshotAsync()).Count!=0)throw new Exception("Missing host reached desktop");
  if(!SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(downloads,"browser-off.zip"))).SequenceEqual(SHA256.HashData(server.CurrentData)))throw new Exception("Missing host browser download mismatch");
  File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{result="PASS",scenario,missingHostRemainsOff=true,browserDownload="SHA256 verified"}));Console.WriteLine("PASS: missing native host keeps extension OFF and normal browser download completes.");return;
 }
 if(scenario is "header" or "inplace" && receivedMetadata?.ToMetadata().FileName != "header.zip") throw new Exception("Response metadata lost before desktop review: "+JsonSerializer.Serialize(receivedMetadata));
 var end=DateTime.UtcNow.AddSeconds(20);while(!(await engine.SnapshotAsync()).Any(j=>j.State==DownloadState.Completed)){if(DateTime.UtcNow>end)throw new TimeoutException("Desktop transfer did not finish");await Task.Delay(50);}
 var job=(await engine.SnapshotAsync()).Single();if(scenario is "cookie" or "post"&&job.ProtectedSession is null)throw new Exception("Browser session was not transferred");if(scenario=="post"&&SessionRequest.Restore(job.ProtectedSession)?.Method!="POST")throw new Exception("POST method lost");if(!SHA256.HashData(await File.ReadAllBytesAsync(job.FinalPath)).SequenceEqual(SHA256.HashData(server.CurrentData)))throw new Exception("Desktop SHA mismatch");
 if(scenario=="post"&&server.Captured.Count(r=>r.Path=="post.zip"&&r.Method=="POST")!=1)throw new Exception("POST duplicated");
 if(actions.Count(a=>a=="import")!=1)throw new Exception("Expected one native import while ON and none while OFF");
 if(!SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(downloads,"browser-off.zip"))).SequenceEqual(SHA256.HashData(server.CurrentData)))throw new Exception("Browser OFF SHA mismatch");
 File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{result="PASS",scenario,browser=firefox?"Firefox headless isolated profile":chrome?"Chrome headless isolated profile":"Edge headless isolated profile",actions,sha256="verified",review="deterministic acceptance fixture; no real WPF approval dialog",userBrowserProfileChanged=false,realPowerActions=0},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS: real "+(firefox?"Firefox":chrome?"Chrome":"Edge")+" extension -> production native host -> named pipe -> pre-navigation interception (zero browser download items) -> SHA-256 verified desktop download; OFF browser download verified.");
}finally{
 if(webdriverSession is not null&&webdriver is not null){try{using var response=await webdriver.DeleteAsync("/session/"+webdriverSession);}catch(Exception error)when(error is HttpRequestException or TaskCanceledException){}}
 if(browser is not null){try{if(!browser.HasExited){browser.Kill(true);await browser.WaitForExitAsync();}}finally{browser.Dispose();}}
 using(var key=Registry.CurrentUser.OpenSubKey(registry)){if(key?.GetValue("") as string==nativeManifest){key.Dispose();Registry.CurrentUser.DeleteSubKey(registry,false);}}
 if(driverErrors is not null)File.WriteAllText(Path.Combine(root,"webdriver-stderr.log"),await driverErrors);if(driverOutput is not null)File.WriteAllText(Path.Combine(root,"webdriver-stdout.log"),await driverOutput);webdriver?.Dispose();
 File.Delete(allow);File.Delete(wrapperConfig);await server.DisposeAsync();
 if(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==port||driverPort>0&&p.Port==driverPort||marionettePort>0&&p.Port==marionettePort||cdpPort>0&&p.Port==cdpPort))throw new Exception("Browser fixture listener leaked");Console.WriteLine("Temporary browser fixture port "+port+" closed; test native-host registration removed.");
}
