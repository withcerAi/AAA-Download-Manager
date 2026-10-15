using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Core;
public sealed class BrowserRequest
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "import";
    public bool? Media {get;set;}
    public BrowserResponseMetadata? ResponseMetadata { get; set; }
    public SessionRequest? Context { get; set; }
    public string[] Urls { get; set; } = [];
    public BrowserRequest Validated()
    {
        if (Id is null || Urls is null || Version != 1 || Id.Length is < 1 or > 64 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || Action is not ("ping" or "activate" or "import" or "prepare" or "commit" or "abort") || Urls.Length > 1000) throw new ArgumentException("browser.invalid");
        if (Action is "import" or "prepare" && Urls.Length == 0) throw new ArgumentException("browser.invalid");
        foreach (var url in Urls) if (url is null || url.Length > 16384 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("browser.invalid");
        if(Context is not null && (Urls.Length!=1 || Context.Validated().Url!=Urls[0]))throw new ArgumentException("browser.invalid_context");
        if (ResponseMetadata is not null) { if (Urls.Length != 1) throw new ArgumentException("browser.invalid_context"); ResponseMetadata.Validated(Urls[0]); }
        Urls = Urls.Select(url => LinkImport.Normalize(url) ?? throw new ArgumentException("browser.invalid")).Distinct(StringComparer.Ordinal).ToArray(); return this;
    }
}
public sealed record BrowserReply(string Id, bool Ok, string Status, int Count = 0, int Version = 1);
public static class BrowserProtocol
{
    public const int MaxBytes = 1024 * 1024;
    public const string HostName = "com.downloadmanager.bridge";
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static string PipeName(string dataDirectory)
    {
        var identity = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() + "|" + Environment.UserDomainName + "|" + Environment.UserName;
        return "DownloadManager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellation = default)
    {
        var prefix = new byte[4]; var first = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellation); if (first == 0) return default;
        await stream.ReadExactlyAsync(prefix.AsMemory(1), cancellation); var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length is 0 or > MaxBytes) throw new InvalidDataException("browser.message_size");
        var payload = new byte[(int)length]; await stream.ReadExactlyAsync(payload, cancellation);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("browser.invalid");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json); if (payload.Length > MaxBytes) throw new InvalidDataException("browser.message_size");
        var prefix = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length); await stream.WriteAsync(prefix, cancellation); await stream.WriteAsync(payload, cancellation); await stream.FlushAsync(cancellation);
    }
}
