using System.IO.Pipes;
namespace DownloadManager.Core;
public sealed class BrowserPipeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly string _name;
    private readonly Func<BrowserRequest, CancellationToken, Task<BrowserReply>> _handler;
    private Task? _loop;
    public BrowserPipeServer(string dataDirectory, Func<BrowserRequest, CancellationToken, Task<BrowserReply>> handler) { _name = BrowserProtocol.PipeName(dataDirectory); _handler = handler; }
    public void Start() { _loop ??= Task.Run(RunAsync); }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // Local IPC only. Windows ACL and the client flag restrict the connection to this OS user.
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); budget.CancelAfter(TimeSpan.FromMinutes(5));
                var request = (await BrowserProtocol.ReadAsync<BrowserRequest>(pipe, budget.Token))?.Validated() ?? throw new InvalidDataException();
                var response = await _handler(request, budget.Token); await BrowserProtocol.WriteAsync(pipe, response, budget.Token);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException) { }
        }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); if (_loop is not null) await _loop; _stop.Dispose(); }
}
