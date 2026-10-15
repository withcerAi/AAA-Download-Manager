using System.Net;
using System.Text.RegularExpressions;

namespace DownloadManager.Core;

public sealed record ImportedLink(string Url, string FileName, string Host);
public sealed record LinkImportResult(IReadOnlyList<ImportedLink> Links, int Duplicates, int Rejected, bool Limited);

public static class LinkImport
{
    public const int MaximumTextLength = 2 * 1024 * 1024;
    public const int MaximumLinks = 1000;
    private static readonly Regex Urls = new("(?:https?|ftps?)://[^\\s<>\"'`\u200B\uFEFF\u200E\u200F\u2066-\u2069]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
    private static readonly Regex Starts = new("(?:https?|ftps?)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Hrefs = new("<(?:a|area)\\b(?:[^\"'<>]|\"[^\"]*\"|'[^']*')*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
    private static readonly Regex Attributes = new("\\s(?<name>[\\w:-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s\"'=<>`]+))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
    private static readonly Regex HiddenHtml = new("<(script|style)\\b[^>]*>[\\s\\S]*?</\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(300));
    private static readonly Regex Range = new(@"\[(\d{1,9})-(\d{1,9})\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string? Normalize(string value)
    {
        if (value.Length > 16384 || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if(uri.Scheme is "ftp" or "ftps" && Uri.UnescapeDataString(uri.AbsolutePath).Any(char.IsControl))return null;
        return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    }
    public static LinkImportResult Extract(string text)
    {
        if (text.Length > MaximumTextLength) throw new ArgumentException("native.import_too_large");
        var found = new List<ImportedLink>(); var seen = new HashSet<string>(StringComparer.Ordinal); var duplicates = 0; var rejected = 0;
        foreach (Match match in Urls.Matches(text))
        {
            if(match.Length>16384){rejected++;continue;}
            var starts = Starts.Matches(match.Value);if(starts.Count>32){rejected++;continue;} var parts = new List<string>(); var offset = 0; var ambiguous = false;
            foreach (Match next in starts.Cast<Match>().Skip(1))
            {
                var preceding = match.Value[next.Index - 1]; var hasQuery = match.Value.IndexOf('?',offset,next.Index-offset)>=0;
                if (preceding is ',' or ';' or '|')
                {
                    // A nested query may legitimately contain a list of URLs. Never rewrite it.
                    if (hasQuery) { ambiguous = true; break; }
                    parts.Add(match.Value[offset..(next.Index - 1)]); offset = next.Index;
                }
                else if (!hasQuery && preceding != '/') { ambiguous = true; break; }
            }
            if (ambiguous) { rejected++; continue; }
            parts.Add(match.Value[offset..]);
            var firstPart=true;
            foreach (var raw in parts)
            {
                var candidate = TrimProse(raw,firstPart&&match.Index>0?text[match.Index-1]:null);firstPart=false;
                var url = Normalize(candidate);
                if (url is null) { rejected++; continue; }
                if (!seen.Add(url)) { duplicates++; continue; }
                if (found.Count == MaximumLinks) return new(found, duplicates, rejected, true);
                var uri = new Uri(url); found.Add(new(url, DownloadEngine.SuggestedName(url), uri.IdnHost));
            }
        }
        return new(found, duplicates, rejected, false);
    }
    private static string TrimProse(string value,char? wrapper)
    {
        var query=value.Contains('?');
        if(!query)value=value.TrimEnd('.',',',';',':','!','،','؛','؟');
        foreach(var (open,close) in new[]{('(',')'),('[',']'),('{','}')})
            if(!query||wrapper==open)while(value.EndsWith(close)&&value.Count(c=>c==close)>value.Count(c=>c==open))value=value[..^1];
        return query?value:value.TrimEnd('.',',',';',':','!','،','؛','؟');
    }
    public static LinkImportResult FromClipboard(string plainText, string? html = null)
    {
        if (plainText.Length > MaximumTextLength || html?.Length > MaximumTextLength) throw new ArgumentException("native.import_too_large");
        var results = new List<LinkImportResult> { Extract(plainText) };
        if (!string.IsNullOrEmpty(html))
        {
            var fragment = HiddenHtml.Replace(ClipboardFragment(html), " "); var targets = new List<string>();
            foreach (Match match in Hrefs.Matches(fragment))
            {
                var href=Attributes.Matches(match.Value).Cast<Match>().FirstOrDefault(attribute=>attribute.Groups["name"].Value.Equals("href",StringComparison.OrdinalIgnoreCase));
                var target = href is null?null:Normalize(WebUtility.HtmlDecode(href.Groups["value"].Value));
                if (target is not null) targets.Add(target);
                if (targets.Count > MaximumLinks) break;
            }
            results.Add(Extract(string.Join("\n", targets)));
            results.Add(Extract(WebUtility.HtmlDecode(Tags.Replace(HiddenHtml.Replace(fragment, " "), " "))));
        }
        var found = new List<ImportedLink>(); var seen = new HashSet<string>(StringComparer.Ordinal); var duplicates = 0; var rejected = 0; var limited = false;
        foreach (var result in results)
        {
            duplicates += result.Duplicates; rejected += result.Rejected; limited |= result.Limited;
            foreach (var link in result.Links)
            {
                if (!seen.Add(link.Url)) { duplicates++; continue; }
                if (found.Count == MaximumLinks) { limited = true; break; }
                found.Add(link);
            }
        }
        return new(found, duplicates, rejected, limited);
    }
    public static string ClipboardFragment(string html)
    {
        const string start = "<!--StartFragment-->"; const string end = "<!--EndFragment-->"; var from = html.IndexOf(start, StringComparison.OrdinalIgnoreCase); var to = html.IndexOf(end, StringComparison.OrdinalIgnoreCase);
        if (from >= 0 && to > from) return html[(from + start.Length)..to];
        // Do not import the CF_HTML SourceURL header as a download link.
        var markup = html.IndexOf('<'); return markup >= 0 ? html[markup..] : "";
    }
    public static IReadOnlyList<string> ExpandRange(string pattern)
    {
        if (pattern.Length > 16384) throw new ArgumentException("native.invalid_pattern");
        var matches = Range.Matches(pattern); if (matches.Count != 1) throw new ArgumentException("native.invalid_pattern");
        var match = matches[0]; var first = int.Parse(match.Groups[1].Value); var last = int.Parse(match.Groups[2].Value);
        if (last < first || (long)last - first + 1 > MaximumLinks) throw new ArgumentException("native.invalid_pattern");
        var width = match.Groups[1].Value.Length; var result = new List<string>();
        for (var number = first; number <= last; number++)
        {
            var url = pattern[..match.Index] + number.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture) + pattern[(match.Index + match.Length)..];
            if (Normalize(url) is not { } normalized) throw new ArgumentException("native.invalid_pattern"); result.Add(normalized);
        }
        return result;
    }
}
