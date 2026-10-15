using DownloadManager.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;

if (args.Length >= 1 && args[0] is "--register" or "--unregister")
{
    if (args.Length != 3 || args[1] is not ("chrome" or "edge" or "firefox")) return 2;
    var browser = args[1]; var id = args[2];
    if (browser == "firefox" ? id != "downloadmanager@local.invalid" : !Regex.IsMatch(id, "^[a-p]{32}$")) return 2;
    var root = browser switch { "chrome" => @"Software\Google\Chrome\NativeMessagingHosts\", "edge" => @"Software\Microsoft\Edge\NativeMessagingHosts\", _ => @"Software\Mozilla\NativeMessagingHosts\" };
    var path = Path.Combine(AppContext.BaseDirectory, browser + ".host.json");
    if (args[0] == "--register")
    {
        var manifest = new Dictionary<string, object> { ["name"] = BrowserProtocol.HostName, ["description"] = "AAA Download Manager browser bridge", ["path"] = Environment.ProcessPath!, ["type"] = "stdio" };
        manifest[browser == "firefox" ? "allowed_extensions" : "allowed_origins"] = new[] { browser == "firefox" ? id : "chrome-extension://" + id + "/" };
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        using var key = Registry.CurrentUser.CreateSubKey(root + BrowserProtocol.HostName); key.SetValue("", path, RegistryValueKind.String);
    }
    else
    {
        using var key = Registry.CurrentUser.OpenSubKey(root + BrowserProtocol.HostName);
        if (string.Equals(key?.GetValue("") as string, path, StringComparison.OrdinalIgnoreCase)) { key?.Dispose(); Registry.CurrentUser.DeleteSubKey(root + BrowserProtocol.HostName, false); }
        if (File.Exists(path)) File.Delete(path);
    }
    return 0;
}
try
{
    var allowed = new HashSet<string>(StringComparer.Ordinal) { "downloadmanager@local.invalid" };
    foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "*.host.json"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var key in new[] { "allowed_origins", "allowed_extensions" }) if (doc.RootElement.TryGetProperty(key, out var values)) foreach (var value in values.EnumerateArray()) if (value.GetString() is { } origin) allowed.Add(origin);
    }
    // Firefox supplies the host manifest path first and the caller ID second; Chromium supplies its extension origin first.
    if (!args.Any(allowed.Contains)) return 3;
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var request = (await BrowserProtocol.ReadAsync<BrowserRequest>(Console.OpenStandardInput(), timeout.Token))?.Validated() ?? throw new InvalidDataException();
    var dataIndex = Array.IndexOf(args, "--data-dir");
    var data = dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager");
    using var pipe = new NamedPipeClientStream(".", BrowserProtocol.PipeName(data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    try { await pipe.ConnectAsync(800, timeout.Token); }
    catch (TimeoutException)
    {
        if (args.Contains("--no-launch")) throw;
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "DownloadManager.exe"));
        if (!File.Exists(app)) throw new FileNotFoundException();
        Detached.Start(app);
        await pipe.ConnectAsync(15000, timeout.Token);
    }
    // The native host was launched by the browser after the user's click. Give
    // only the connected desktop process permission to activate its review window.
    // Never grant foreground rights for background pings or commit/abort messages.
    if (request.Action is "import" or "prepare" or "activate") ForegroundPermission.Grant(pipe);
    await BrowserProtocol.WriteAsync(pipe, request, timeout.Token);
    var reply = await BrowserProtocol.ReadAsync<BrowserReply>(pipe, timeout.Token) ?? throw new InvalidDataException();
    await BrowserProtocol.WriteAsync(Console.OpenStandardOutput(), reply, timeout.Token);
    return 0;
}
catch (Exception error) when (error is IOException or TimeoutException or ArgumentException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception)
{
    await BrowserProtocol.WriteAsync(Console.OpenStandardOutput(), new BrowserReply("", false, "unavailable")); return 1;
}
internal static class Detached
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public int Size; public string? Reserved; public string? Desktop; public string? Title; public int X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public short Show,Reserved2; public IntPtr ReservedPointer,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process,Thread; public int ProcessId,ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static void Start(string app)
    {
        var startup = new Startup { Size = Marshal.SizeOf<Startup>() }; var command = new StringBuilder("\"" + app + "\"");
        if (!CreateProcess(app, command, IntPtr.Zero, IntPtr.Zero, false, 0x01000000, IntPtr.Zero, Path.GetDirectoryName(app)!, ref startup, out var process))
        {
            var error = Marshal.GetLastWin32Error(); if (error != 5 || !CreateProcess(app, new StringBuilder("\"" + app + "\""), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(app)!, ref startup, out process)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        CloseHandle(process.Thread); CloseHandle(process.Process);
    }
}

internal static class ForegroundPermission
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    public static void Grant(NamedPipeClientStream pipe)
    {
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) && processId != 0)
            _ = AllowSetForegroundWindow(processId);
    }
}
