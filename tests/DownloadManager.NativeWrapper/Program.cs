using System.Diagnostics;
using System.Text.Json;
using DownloadManager.Core;
// Test-only shim: preserves the real browser origin and production host framing,
// while redirecting the host into an isolated application-data directory.
using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"test-config.json")));
var start=new ProcessStartInfo(config.RootElement.GetProperty("host").GetString()!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
foreach(var arg in args)start.ArgumentList.Add(arg);start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(config.RootElement.GetProperty("data").GetString()!);start.ArgumentList.Add("--no-launch");
using var process=Process.Start(start)!;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
var request=await BrowserProtocol.ReadAsync<BrowserRequest>(Console.OpenStandardInput(),timeout.Token)??throw new InvalidDataException();await BrowserProtocol.WriteAsync(process.StandardInput.BaseStream,request,timeout.Token);
var reply=await BrowserProtocol.ReadAsync<BrowserReply>(process.StandardOutput.BaseStream,timeout.Token)??throw new InvalidDataException();await BrowserProtocol.WriteAsync(Console.OpenStandardOutput(),reply,timeout.Token);await process.WaitForExitAsync(timeout.Token);return process.ExitCode;
