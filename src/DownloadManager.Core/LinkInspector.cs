using System.Net;
namespace DownloadManager.Core;

public sealed record LinkMetadata(string? FileName, long? Size, string? ContentType, int? StatusCode, string State);

// Response headers captured by the browser; scoped to one URL and never executable.
public sealed record BrowserResponseMetadata
{
    public string Url { get; init; } = "";
    public string ContentDisposition { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long? Size { get; init; }
    public int StatusCode { get; init; } = 200;
    public BrowserResponseMetadata Validated(string url)
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || Url != url || ContentDisposition is null || ContentType is null || ContentDisposition.Length > 8192 || ContentType.Length > 256 || ContentDisposition.Any(char.IsControl) || ContentType.Any(char.IsControl) || Size < 0 || StatusCode is < 200 or > 599) throw new ArgumentException("browser.invalid_context");
        return this;
    }
    public LinkMetadata ToMetadata()
    {
        Validated(Url);
        using var response = new HttpResponseMessage((System.Net.HttpStatusCode)StatusCode) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, Url) };
        response.Content.Headers.TryAddWithoutValidation("Content-Disposition", ContentDisposition);
        response.Content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
        return new(response.IsSuccessStatusCode ? LinkInspector.ResponseName(Url, response) : null, response.IsSuccessStatusCode ? Size : null, ContentType, StatusCode, response.IsSuccessStatusCode ? "ready" : "unavailable");
    }
}

public static class LinkInspector
{
    private static readonly HashSet<string> EndpointExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "", ".php", ".asp", ".aspx", ".jsp", ".cgi", ".ashx", ".do", ".action", ".bin" };
    private static string SafeFileName(string name) => DownloadEngine.SafeFileName(name);
    private static string SuggestedName(string url) => DownloadEngine.SuggestedName(url);
    public static string ResponseName(string url, HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var supplied = disposition?.FileNameStar ?? disposition?.FileName;
        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
        var name = !string.IsNullOrWhiteSpace(supplied) ? SafeFileName(supplied.Trim('"')) : SuggestedName(finalUrl);
        if (EndpointExtensions.Contains(Path.GetExtension(name)))
        {
            var extension = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
            {
                "video/mp4" => ".mp4", "video/x-matroska" => ".mkv", "video/webm" => ".webm",
                "video/quicktime" => ".mov", "video/x-msvideo" => ".avi", "video/mp2t" => ".ts",
                "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/flac" => ".flac",
                "audio/ogg" => ".ogg", "audio/wav" => ".wav", "application/pdf" => ".pdf",
                "application/zip" => ".zip", "application/x-7z-compressed" => ".7z", _ => null
            };
            if (extension is not null) name = SafeFileName(Path.ChangeExtension(name, extension));
        }
        return name;
    }
    public static async Task<LinkMetadata> InspectAsync(string url, TransferSettings settings, SessionRequest? session = null, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return new(null, null, null, null, "unsupported");
        // A preview must never replay a POST or fetch a file body.
        if (session is not null && session.Method != "GET") return new(null, null, null, null, "unavailable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var client = DownloadHttpClient.Create(settings.Network, true);
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            if (session is not null) session.Apply(request);
            request.Method = HttpMethod.Head;
            if (!request.Headers.Contains("User-Agent")) request.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var state = response.IsSuccessStatusCode ? "ready" : status is 404 or 410 ? "missing" : status is 401 or 403 ? "denied" : "unavailable";
            var size = response.Content.Headers.ContentEncoding.Count == 0 ? response.Content.Headers.ContentLength : null;
            return new(response.IsSuccessStatusCode ? ResponseName(url, response) : null, response.IsSuccessStatusCode ? size : null, response.Content.Headers.ContentType?.MediaType, status, state);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new(null,null,null,null,"timeout"); }
        catch (HttpRequestException) { return new(null,null,null,null,"unavailable"); }
    }
}
