using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

sealed class FtpFixture : IAsyncDisposable
{
    private readonly TcpListener _control=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly ConcurrentBag<Task> _tasks=[];
    private Task? _accept;private bool _disposed;
    public readonly ConcurrentQueue<long> Offsets=[];
    public readonly ConcurrentBag<int> DataPorts=[];
    public byte[] Data {get;}=Enumerable.Range(0,2*1024*1024).Select(i=>(byte)(i%239)).ToArray();
    public int Port=>((IPEndPoint)_control.LocalEndpoint).Port;
    public string Url=>"ftp://127.0.0.1:"+Port+"/fixture.bin";
    public void Start(){_control.Start();_accept=Accept();}
    private async Task Accept(){while(!_stop.IsCancellationRequested){try{var client=await _control.AcceptTcpClientAsync(_stop.Token);_tasks.Add(Serve(client));}catch(OperationCanceledException){break;}}}
    private async Task Serve(TcpClient client)
    {
        using(client){TcpListener? passive=null;IPEndPoint? active=null;
        try{await using var stream=client.GetStream();using var input=new StreamReader(stream,Encoding.ASCII,false,1024,true);using var output=new StreamWriter(stream,Encoding.ASCII,1024,true){NewLine="\r\n",AutoFlush=true};
            async Task Reply(string text)=>await output.WriteLineAsync(text);
            await Reply("220 FTP fixture");long offset=0;
            while(await input.ReadLineAsync(_stop.Token) is {} line){var split=line.IndexOf(' ');var verb=(split<0?line:line[..split]).ToUpperInvariant();var arg=split<0?"":line[(split+1)..];
                switch(verb){
                    case "USER":await Reply("331 Password required");break;
                    case "PASS":await Reply("230 Logged in");break;
                    case "OPTS":case "TYPE":case "CWD":await Reply("200 OK");break;
                    case "PWD":await Reply("257 \"/\" is current directory");break;
                    case "SIZE":await Reply("213 "+Data.Length);break;
                    case "MDTM":await Reply("213 20261015000000");break;
                    case "EPSV":case "PASV":passive?.Stop();passive=new TcpListener(IPAddress.Loopback,0);passive.Start();var port=((IPEndPoint)passive.LocalEndpoint).Port;DataPorts.Add(port);await Reply(verb=="EPSV"?"229 Entering Extended Passive Mode (|||"+port+"|)":"227 Entering Passive Mode (127,0,0,1,"+(port/256)+","+(port%256)+")");break;
                    case "PORT":var values=arg.Split(',').Select(int.Parse).ToArray();active=new(IPAddress.Loopback,values[4]*256+values[5]);await Reply("200 PORT accepted");break;
                    case "REST":offset=long.Parse(arg);await Reply("350 Restart accepted");break;
                    case "RETR":
                        Offsets.Enqueue(offset);await Reply("150 Opening data connection");
                        using(var data=passive is not null?await passive.AcceptTcpClientAsync(_stop.Token):new TcpClient()){
                            if(passive is null)await data.ConnectAsync(active!.Address,active.Port,_stop.Token);
                            await using var body=data.GetStream();for(var i=(int)offset;i<Data.Length;i+=32768){await body.WriteAsync(Data.AsMemory(i,Math.Min(32768,Data.Length-i)),_stop.Token);await Task.Delay(8,_stop.Token);}
                        }passive?.Stop();passive=null;offset=0;await Reply("226 Transfer complete");break;
                    case "QUIT":await Reply("221 Bye");return;
                    default:await Reply("502 Unsupported");break;
                }
            }
        }catch(Exception e)when(e is IOException or SocketException or OperationCanceledException){}finally{passive?.Stop();}}
    }
    public async ValueTask DisposeAsync(){if(_disposed)return;_disposed=true;_stop.Cancel();_control.Stop();if(_accept is not null)await _accept;await Task.WhenAll(_tasks);_stop.Dispose();}
}
