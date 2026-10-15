using DownloadManager.Core;
namespace DownloadManager.Desktop;
public static class BrowserDownloadReview
{
    public static bool IsSingle(BrowserRequest request)=>request.Validated().Urls.Length==1;
    public static AddDownloadWindow CreateSingle(LocaleCatalog locale,UserSettings settings,IReadOnlyList<QueueChoice> queues,BrowserRequest request)
    {
        request.Validated();if(!IsSingle(request))throw new ArgumentException("browser.invalid");
        var window=new AddDownloadWindow(locale,settings.DownloadDirectory,queues);window.Inspector = (url, token) => request.ResponseMetadata is {} metadata && metadata.Url == url ? Task.FromResult(metadata.ToMetadata()) : LinkInspector.InspectAsync(url, settings.TransferPolicy(), request.Context, token);var autoFolder=settings.DownloadDirectory;var policy=settings.TransferPolicy();
        window.FileNameBox.TextChanged+=(_,_)=>{if(window.FolderBox.Text.Equals(autoFolder,StringComparison.OrdinalIgnoreCase)){autoFolder=DownloadDestination.Resolve(settings.DownloadDirectory,window.FileNameBox.Text,policy);window.FolderBox.Text=autoFolder;}};
        window.UrlBox.Text=request.Urls[0];window.UrlBox.IsReadOnly=request.Context is not null;
        if(request.Context is not null)window.UrlBox.ToolTip=locale.S("browser.session_url_locked");
        if(request.Media==true)window.SetAdaptive(true);
        BrowserWindowActivation.Prepare(window);
        return window;
    }
    public static BrowserRequest AcceptedRequest(BrowserRequest request,AddDownloadWindow window)
    {
        var url=window.UrlBox.Text.Trim();return new BrowserRequest{Version=request.Version,Id=request.Id,Action=request.Action,Urls=[url],Media=window.Adaptive,Context=request.Urls[0]==url?request.Context:null}.Validated();
    }
}
