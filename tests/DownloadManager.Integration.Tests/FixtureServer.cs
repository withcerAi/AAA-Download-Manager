using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
sealed class FixtureServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private Task? _accept;
    private bool _disposed;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    private byte[] _data = Enumerable.Range(0, 4 * 1024 * 1024).Select(number => (byte)(number % 251)).ToArray();
    private int _flakyRequests;
    private int _partChanged;
    private int _version = 1;
    private readonly object _versionGate = new();
    private readonly ConcurrentDictionary<string, int> _activeByPath = [];
    public ConcurrentDictionary<string, int> MaximumByPath { get; } = [];
    public ConcurrentQueue<(string Path, long From, long To)> Ranges { get; } = [];
    public ConcurrentQueue<string> UserAgents { get; } = [];
    private string _tag = "\"fixture-v1\"";
    public ConcurrentQueue<(string Path, long Offset)> Requests { get; } = [];
    public byte[] CurrentData => _data;
    public TaskCompletionSource<string> ExtensionReport{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? BrowserPage{get;set;}
    public string? MediaRoot{get;set;}
    public string? RedirectTarget {get;set;}
    public ConcurrentQueue<(string Method,string Path,Dictionary<string,string> Headers,string Body)> Captured {get;}=[];
    public Task StartAsync()
    {
        _listener.Start();
        _accept = AcceptAsync();
        return Task.CompletedTask;
    }
    public string Url(string path) => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/" + path;
    public void ResizeData(int size) { lock (_versionGate) { _data = Enumerable.Range(0, size).Select(value => (byte)(value % 251)).ToArray(); _tag = "\"fixture-v" + (++_version) + "\""; } }
    public void ChangeVersion() { lock (_versionGate) { _data = _data.Select(value => (byte)(value ^ 0x55)).ToArray(); _tag = "\"fixture-v" + (++_version) + "\""; } }
    private async Task AcceptAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try { var socket = await _listener.AcceptTcpClientAsync(_cancellation.Token); _clients.Add(HandleAsync(socket)); }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task HandleAsync(TcpClient socket)
    {
        using (socket)
        {
            string? countedPath = null;
            try
            {
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var first = await reader.ReadLineAsync(_cancellation.Token) ?? "";
                var path = first.Split(' ').ElementAtOrDefault(1)?.TrimStart('/') ?? "";
                if(Uri.TryCreate(path,UriKind.Absolute,out var absolute)&&absolute.Scheme=="http")path=absolute.AbsolutePath.TrimStart('/');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(_cancellation.Token) is { Length: > 0 } line)
                {
                    var split = line.IndexOf(':');
                    if (split > 0) headers[line[..split]] = line[(split + 1)..].Trim();
                }
                var body="";if(int.TryParse(headers.GetValueOrDefault("Content-Length"),out var bodySize)&&bodySize>0&&bodySize<300000){var chars=new char[bodySize];await reader.ReadBlockAsync(chars.AsMemory(),_cancellation.Token);body=new string(chars);}
                var method=first.Split(' ')[0];Captured.Enqueue((method,path,headers,body));
                if(path is "preview-missing" or "preview-no-head") { var status=path=="preview-missing"?"404 Missing":"405 Method Not Allowed";await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return; }
                if(path=="preview-303") { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 303 See Other\r\nLocation: /remote_mime.php\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return; }
                if(path=="browser-source" && BrowserPage is {} page){var htmlBytes=Encoding.UTF8.GetBytes(page);await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: "+htmlBytes.Length+"\r\nConnection: close\r\n\r\n"));await stream.WriteAsync(htmlBytes);return;}
                if(path=="extension-report"){ExtensionReport.TrySetResult(body);await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return;}
                if(path=="redirect"&&RedirectTarget is {} destination){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: "+destination+"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return;}
                if(path=="auth"&&headers.GetValueOrDefault("Authorization")!="Basic dXNlcjpwYXNz"){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return;}
                if(path=="forbidden"||path is "session" or "session.zip"&&headers.GetValueOrDefault("Cookie")!="sid=fixture"||path is "post" or "post.zip"&&(method!="POST"||body!="token=fixture")){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return;}
                if(path is "site/index.html" or "site/page.html"){
                    var html=path=="site/index.html"?"<a href='a.zip'>a</a><a href='a.zip#duplicate'>same</a><a href='page.html'>next</a><a href='/outside.zip'>outside</a><a href='https://foreign.test/private.zip'>foreign</a>":"<a href='b.pdf'>b</a><a href='excluded.zip'>excluded</a><a href='index.html'>cycle</a>";
                    var payload=Encoding.UTF8.GetBytes(html);await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "+payload.Length+"\r\nConnection: close\r\n\r\n"));await stream.WriteAsync(payload);return;
                }
                if(MediaRoot is not null&&path.StartsWith("media/",StringComparison.Ordinal)){
                    var file=Path.Combine(MediaRoot,Path.GetFileName(path[6..]));if(!File.Exists(file)){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Missing\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return;}
                    var bytes=await File.ReadAllBytesAsync(file,_cancellation.Token);var etag="\""+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))+"\"";
                    if(headers.GetValueOrDefault("If-None-Match")==etag){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 304 Not Modified\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));return;}
                    long mediaFrom=0,mediaTo=bytes.Length-1;var ranged=headers.TryGetValue("Range",out var requested);if(ranged){var values=requested![6..].Split('-');mediaFrom=long.Parse(values[0]);if(values.Length>1&&values[1].Length>0)mediaTo=Math.Min(mediaTo,long.Parse(values[1]));}
                    var reply="HTTP/1.1 "+(ranged?"206 Partial Content":"200 OK")+"\r\nContent-Length: "+(mediaTo-mediaFrom+1)+"\r\nETag: "+etag+"\r\nConnection: close\r\n";if(ranged)reply+="Content-Range: bytes "+mediaFrom+"-"+mediaTo+"/"+bytes.Length+"\r\n";reply+="\r\n";await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                    for(var i=(int)mediaFrom;i<=mediaTo;i+=8192){await stream.WriteAsync(bytes.AsMemory(i,Math.Min(8192,(int)(mediaTo-i+1))),_cancellation.Token);await Task.Delay(8,_cancellation.Token);}return;
                }
                var offset = headers.TryGetValue("Range", out var range) ? long.Parse(range[6..].Split('-')[0]) : 0;
                Requests.Enqueue((path, offset)); UserAgents.Enqueue(headers.GetValueOrDefault("User-Agent") ?? "");
                if (path == "unavailable" || path == "flaky" && Interlocked.Increment(ref _flakyRequests) == 1)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _cancellation.Token); return;
                }
                if (path == "multi-change" && headers.ContainsKey("If-Range") && Interlocked.CompareExchange(ref _partChanged, 1, 0) == 0) ChangeVersion();
                byte[] data; string tag; lock (_versionGate) { data = _data; tag = _tag; }
                if (path == "untyped.php") { data=data.ToArray();new byte[]{0x1a,0x45,0xdf,0xa3}.CopyTo(data,0); }
                if (path == "empty") data = [];
                var supported = path is not ("no-range" or "unknown");
                var hasRange = headers.ContainsKey("Range");
                var useRange = supported && hasRange;
                if (path == "multi-ignore" && headers.ContainsKey("If-Range")) useRange = false;
                if (useRange && path is not ("changed" or "multi-change") && headers.TryGetValue("If-Range", out var validator) && validator != tag) useRange = false;
                if (!useRange) offset = 0;
                var rangeEnd = data.Length - 1L;
                if (useRange && long.TryParse(headers["Range"][6..].Split('-').ElementAtOrDefault(1), out var requestedEnd)) rangeEnd = Math.Min(rangeEnd, requestedEnd);
                if (useRange) Ranges.Enqueue((path, offset, rangeEnd));
                if (headers.ContainsKey("If-Range")) { countedPath = path; var active = _activeByPath.AddOrUpdate(path, 1, (_, value) => value + 1); MaximumByPath.AddOrUpdate(path, active, (_, value) => Math.Max(active, value)); }
                if (useRange && offset >= data.Length)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{data.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    return;
                }
                var from = useRange && (path == "bad-range" || path == "multi-bad" && headers.ContainsKey("If-Range")) ? offset + 1 : offset;
                var response = $"HTTP/1.1 {(useRange ? "206 Partial Content" : "200 OK")}\r\nContent-Length: {rangeEnd - offset + 1}\r\nETag: {(path == "multi-weak" ? "W/" + tag : tag)}\r\nConnection: close\r\n";
                if(path=="remote_control.php")response+="Content-Disposition: attachment; filename=fallback.mkv; filename*=UTF-8''%D9%88%DB%8C%D8%AF%DB%8C%D9%88.mkv\r\nContent-Type: video/x-matroska\r\n";
                if(path=="remote_mime.php")response+="Content-Type: video/mp4\r\n";
                if(path=="unsafe-name.php")response+="Content-Disposition: attachment; filename=\"../../outside.mkv\"\r\n";
                if(path=="header-download")response+="Content-Disposition: attachment; filename=header.zip\r\n";
                if (path == "unknown") response = response.Replace($"Content-Length: {rangeEnd - offset + 1}\r\n", "");
                if (supported) response += "Accept-Ranges: bytes\r\n";
                if (useRange) response += $"Content-Range: bytes {from}-{rangeEnd}/{data.Length}\r\n";
                response += "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cancellation.Token);
                if (path == "stall") await Task.Delay(3000, _cancellation.Token);
                var last = path == "short" ? data.Length / 2 : path == "multi-short" && headers.ContainsKey("If-Range") ? (int)(offset + (rangeEnd - offset + 1) / 2) : (int)rangeEnd + 1;
                for (var position = (int)offset; position < last; position += 32768)
                {
                    await stream.WriteAsync(data.AsMemory(position, Math.Min(32768, last - position)), _cancellation.Token);
                    await Task.Delay(path == "multi-preview" ? 60 : 8, _cancellation.Token);
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
            finally { if (countedPath is { } path) _activeByPath.AddOrUpdate(path, 0, (_, value) => Math.Max(0, value - 1)); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _cancellation.Cancel();
        _listener.Stop();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_clients);
        _cancellation.Dispose();
    }
}
