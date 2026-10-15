namespace DownloadManager.Core;
public sealed record CategoryFolderRule(string Extensions,string Folder);
public static class DownloadDestination
{
    public static string CategoryFolder(string defaultFolder,string folder)
    {
        var root=Path.GetFullPath(defaultFolder);
        if(string.IsNullOrWhiteSpace(folder))return root;
        if(folder.Length>32700||folder.Any(c=>c<32)||Path.IsPathRooted(folder)&&!Path.IsPathFullyQualified(folder))throw new ArgumentException("errors.invalid_folder");
        var resolved=Path.GetFullPath(Path.IsPathFullyQualified(folder)?folder:Path.Combine(root,folder));
        var tail=resolved[(Path.GetPathRoot(resolved)?.Length??0)..];
        if(tail.Split([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar]).Any(part=>part.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new ArgumentException("errors.invalid_folder");
        return resolved;
    }
    public static string Resolve(string requestedFolder,string filename,TransferSettings settings)
    {
        var root=Path.GetFullPath(requestedFolder);
        if(string.IsNullOrEmpty(settings.CategoryRoot)||!root.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(settings.CategoryRoot).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))return root;
        var extension=Path.GetExtension(filename).TrimStart('.');
        var rule=settings.CategoryFolders.FirstOrDefault(rule=>rule.Extensions.Split([' ',',',';'],StringSplitOptions.RemoveEmptyEntries).Any(value=>value.TrimStart('*','.').Equals(extension,StringComparison.OrdinalIgnoreCase)));
        if(rule is null||string.IsNullOrWhiteSpace(rule.Folder))return root;
        return CategoryFolder(root,rule.Folder);
    }
}
