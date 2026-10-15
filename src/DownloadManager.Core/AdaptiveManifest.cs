using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DownloadManager.Core;

public sealed record MediaDocument(string Text,Uri Address);
public sealed record MediaPart(int Stream,string Url,long? Offset=null,long? Length=null,string? KeyUrl=null,string? Iv=null,long Sequence=0);
public sealed record AdaptivePlan(IReadOnlyList<MediaPart> Parts,int Streams);

public static partial class AdaptiveManifest
{
    private const int MaximumParts=10000;
    [GeneratedRegex("(?<key>[A-Z0-9-]+)=(?:\"(?<value>[^\"]*)\"|(?<value>[^,]*))",RegexOptions.CultureInvariant,300)]private static partial Regex Attributes();
    [GeneratedRegex(@"\$(RepresentationID|Bandwidth|Number|Time)(?:%0(\d{1,2})d)?\$",RegexOptions.CultureInvariant,300)]private static partial Regex Variables();
    private static Dictionary<string,string> Attr(string value)=>Attributes().Matches(value).ToDictionary(m=>m.Groups["key"].Value,m=>m.Groups["value"].Value,StringComparer.Ordinal);
    private static string Address(Uri basis,string reference){if(!Uri.TryCreate(basis,reference,out var value)||value.Scheme is not("http" or "https")||value.UserInfo.Length>0||value.AbsoluteUri.Length>16384||reference.Any(char.IsControl))throw new InvalidDataException("media.invalid_manifest");return value.AbsoluteUri;}
    public static async Task<AdaptivePlan> ReadAsync(Uri uri,Func<Uri,Task<MediaDocument>> fetch,CancellationToken cancellation=default)
    {
        var source=await fetch(uri);if(source.Text.Length>2*1024*1024)throw new InvalidDataException("media.manifest_large");
        return source.Text.TrimStart('\uFEFF',' ','\r','\n').StartsWith("#EXTM3U",StringComparison.Ordinal)?await Hls(source,fetch,0,cancellation):Dash(source);
    }
    private static async Task<AdaptivePlan> Hls(MediaDocument source,Func<Uri,Task<MediaDocument>> fetch,int nesting,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();if(nesting>5||source.Text.Length>2*1024*1024)throw new InvalidDataException("media.invalid_manifest");var lines=source.Text.Split('\n').Select(s=>s.Trim()).ToArray();
        var variants=new List<(Dictionary<string,string> Values,string Url)>();
        for(var i=0;i<lines.Length-1;i++)if(lines[i].StartsWith("#EXT-X-STREAM-INF:",StringComparison.Ordinal)){var values=Attr(lines[i][18..]);var next=lines.Skip(i+1).FirstOrDefault(s=>s.Length>0&&!s.StartsWith('#'));if(next is not null)variants.Add((values,Address(source.Address,next)));}
        if(variants.Count>0){
            var best=variants.OrderByDescending(v=>long.TryParse(v.Values.GetValueOrDefault("BANDWIDTH"),out var n)?n:0).First();var video=await Hls(await fetch(new(best.Url)),fetch,nesting+1,cancellation);
            if(best.Values.TryGetValue("AUDIO",out var group)){
                var audio=lines.Where(l=>l.StartsWith("#EXT-X-MEDIA:",StringComparison.Ordinal)).Select(l=>Attr(l[13..])).Where(a=>a.GetValueOrDefault("TYPE")=="AUDIO"&&a.GetValueOrDefault("GROUP-ID")==group&&a.ContainsKey("URI")).OrderByDescending(a=>a.GetValueOrDefault("DEFAULT")=="YES").FirstOrDefault();
                if(audio is not null){var separate=await Hls(await fetch(new(Address(source.Address,audio["URI"]))),fetch,nesting+1,cancellation);if(video.Streams!=1||separate.Streams!=1||video.Parts.Count+separate.Parts.Count>MaximumParts)throw new InvalidDataException("media.invalid_manifest");return new(video.Parts.Concat(separate.Parts.Select(p=>p with{Stream=1})).ToArray(),2);}
            }return video;
        }
        if(!lines.Any(l=>l=="#EXT-X-ENDLIST"))throw new InvalidDataException("media.live_not_supported");
        var parts=new List<MediaPart>();string? key=null,iv=null;long sequence=0,nextOffset=0;string? range=null;
        foreach(var line in lines){
            if(line.StartsWith("#EXT-X-MEDIA-SEQUENCE:",StringComparison.Ordinal))sequence=long.Parse(line[22..],CultureInfo.InvariantCulture);
            else if(line.StartsWith("#EXT-X-KEY:",StringComparison.Ordinal)){var a=Attr(line[11..]);var method=a.GetValueOrDefault("METHOD");if(method=="NONE"){key=iv=null;}else if(method=="AES-128"&&a.TryGetValue("URI",out var keyUri)){key=Address(source.Address,keyUri);iv=a.GetValueOrDefault("IV");if(iv is not null&&(iv.Length!=34||!iv.StartsWith("0x",StringComparison.OrdinalIgnoreCase)||!iv[2..].All(Uri.IsHexDigit)))throw new InvalidDataException("media.invalid_manifest");}else throw new InvalidDataException("media.protected_format");}
            else if(line.StartsWith("#EXT-X-MAP:",StringComparison.Ordinal)){var a=Attr(line[11..]);if(!a.TryGetValue("URI",out var map))throw new InvalidDataException("media.invalid_manifest");var (offset,length)=Range(a.GetValueOrDefault("BYTERANGE"),0);if(key is not null&&iv is null)throw new InvalidDataException("media.invalid_manifest");parts.Add(new(0,Address(source.Address,map),offset,length,key,iv,sequence));}
            else if(line.StartsWith("#EXT-X-BYTERANGE:",StringComparison.Ordinal))range=line[17..];
            else if(line.Length>0&&!line.StartsWith('#')){var(offset,length)=Range(range,nextOffset);parts.Add(new(0,Address(source.Address,line),offset,length,key,iv,sequence++));nextOffset=(offset??0)+(length??0);range=null;}
            if(parts.Count>MaximumParts||sequence<0)throw new InvalidDataException("media.invalid_manifest");
        }
        if(parts.Count==0)throw new InvalidDataException("media.invalid_manifest");return new(parts,1);
    }
    private static (long? Offset,long? Length) Range(string? value,long fallback)
    {
        if(value is null)return(null,null);var parts=value.Split('@');if(parts.Length>2||!long.TryParse(parts[0],out var length)||length<=0)throw new InvalidDataException("media.invalid_manifest");var offset=parts.Length==2?long.Parse(parts[1],CultureInfo.InvariantCulture):fallback;if(offset<0||offset>long.MaxValue-length)throw new InvalidDataException("media.invalid_manifest");return(offset,length);
    }
    private static AdaptivePlan Dash(MediaDocument source)
    {
        using var reader=XmlReader.Create(new StringReader(source.Text),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2*1024*1024});var document=XDocument.Load(reader);var root=document.Root??throw new InvalidDataException("media.invalid_manifest");var ns=root.Name.Namespace;
        if(root.Name.LocalName!="MPD"||root.Attribute("type")?.Value=="dynamic")throw new InvalidDataException("media.live_not_supported");
        if(root.Descendants(ns+"ContentProtection").Any())throw new InvalidDataException("media.protected_format");
        var periods=root.Elements(ns+"Period").ToArray();if(periods.Length!=1)throw new InvalidDataException("media.multiperiod_not_supported");var period=periods[0];
        var duration=period.Attribute("duration")?.Value??root.Attribute("mediaPresentationDuration")?.Value;var seconds=duration is null?0:XmlConvert.ToTimeSpan(duration).TotalSeconds;
        var selected=new List<XElement>();foreach(var kind in new[]{"video","audio"}){
            var candidates=period.Elements(ns+"AdaptationSet").Where(a=>(a.Attribute("contentType")?.Value==kind)||(a.Attribute("mimeType")?.Value.StartsWith(kind+"/",StringComparison.Ordinal)==true)||a.Elements(ns+"Representation").Any(r=>r.Attribute("mimeType")?.Value.StartsWith(kind+"/",StringComparison.Ordinal)==true)).SelectMany(a=>a.Elements(ns+"Representation"));
            var best=candidates.OrderByDescending(r=>Number(r.Attribute("bandwidth")?.Value,0)).FirstOrDefault();if(best is not null)selected.Add(best);
        }
        if(selected.Count==0)throw new InvalidDataException("media.invalid_manifest");var result=new List<MediaPart>();
        foreach(var representation in selected){var stream=selected.IndexOf(representation);var adaptation=representation.Parent!;var basis=source.Address;foreach(var level in new[]{root,period,adaptation,representation})if(level.Element(ns+"BaseURL") is {} b)basis=new(Address(basis,b.Value.Trim()));
            var template=representation.Element(ns+"SegmentTemplate")??adaptation.Element(ns+"SegmentTemplate");var list=representation.Element(ns+"SegmentList")??adaptation.Element(ns+"SegmentList");
            if(template is not null){
                var parentTemplate=adaptation.Element(ns+"SegmentTemplate");string? Get(string name)=>template.Attribute(name)?.Value??parentTemplate?.Attribute(name)?.Value;
                var timescale=Number(Get("timescale"),1);var number=Number(Get("startNumber"),1);if(timescale<=0||number<0)throw new InvalidDataException("media.invalid_manifest");
                string Expand(string value,long n,long time)=>Variables().Replace(value.Replace("$$","\u0001"),m=>{var tag=m.Groups[1].Value;var text=tag=="RepresentationID"?representation.Attribute("id")?.Value??"":tag=="Bandwidth"?representation.Attribute("bandwidth")?.Value??"0":(tag=="Number"?n:time).ToString(CultureInfo.InvariantCulture);return m.Groups[2].Success?text.PadLeft(int.Parse(m.Groups[2].Value), '0'):text;}).Replace('\u0001','$');
                if(Get("initialization") is {} init)result.Add(new(stream,Address(basis,Expand(init,number,0))));var media=Get("media")??throw new InvalidDataException("media.invalid_manifest");var timeline=template.Element(ns+"SegmentTimeline")??parentTemplate?.Element(ns+"SegmentTimeline");
                if(timeline is not null){long time=0;var entries=timeline.Elements(ns+"S").ToArray();for(var i=0;i<entries.Length;i++){var entry=entries[i];time=Number(entry.Attribute("t")?.Value,time);var length=Number(entry.Attribute("d")?.Value,0);if(length<=0||time<0)throw new InvalidDataException("media.invalid_manifest");var repeat=Number(entry.Attribute("r")?.Value,0);if(repeat<0){var end=i+1<entries.Length?Number(entries[i+1].Attribute("t")?.Value,0):(long)Math.Ceiling(seconds*timescale);if(end<=time)throw new InvalidDataException("media.invalid_manifest");repeat=(end-time+length-1)/length-1;}if(repeat>MaximumParts)throw new InvalidDataException("media.invalid_manifest");for(long j=0;j<=repeat;j++){result.Add(new(stream,Address(basis,Expand(media,number++,time))));time=checked(time+length);if(result.Count>MaximumParts)throw new InvalidDataException("media.invalid_manifest");}}}
                else{var durationUnits=Number(Get("duration"),0);if(durationUnits<=0||seconds<=0)throw new InvalidDataException("media.invalid_manifest");var count=Math.Ceiling(seconds*timescale/durationUnits);if(count>MaximumParts)throw new InvalidDataException("media.invalid_manifest");for(long i=0;i<count;i++)result.Add(new(stream,Address(basis,Expand(media,number++,checked(i*durationUnits)))));}
            }
            else if(list is not null){if(list.Element(ns+"Initialization") is {} init&&init.Attribute("sourceURL") is {} initUrl)AddList(initUrl.Value,init.Attribute("range")?.Value);foreach(var segment in list.Elements(ns+"SegmentURL")){AddList(segment.Attribute("media")?.Value??"",segment.Attribute("mediaRange")?.Value);}void AddList(string url,string? range){long? offset=null,length=null;if(range is not null){var pair=range.Split('-');offset=long.Parse(pair[0],CultureInfo.InvariantCulture);length=checked(long.Parse(pair[1],CultureInfo.InvariantCulture)-offset+1);if(offset<0||length<=0)throw new InvalidDataException("media.invalid_manifest");}result.Add(new(stream,Address(basis,url),offset,length));}}
            else if(representation.Element(ns+"BaseURL") is not null||adaptation.Element(ns+"BaseURL") is not null)result.Add(new(stream,basis.AbsoluteUri));
            else throw new InvalidDataException("media.invalid_manifest");
            if(result.Count>MaximumParts)throw new InvalidDataException("media.invalid_manifest");
        }return new(result,selected.Count);
    }
    private static long Number(string? value,long fallback)=>value is null?fallback:long.Parse(value,CultureInfo.InvariantCulture);
}
