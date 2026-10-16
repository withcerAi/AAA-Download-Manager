using System.Windows;

namespace DownloadManager.Desktop;

public static class StartupLaunch
{
    public const string Argument = "--startup";
    public static string Command(string executable) => "\"" + executable + "\" " + Argument;
    public static bool OwnsCommand(string? command, string executable) =>
        string.Equals(command, "\"" + executable + "\"", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, Command(executable), StringComparison.OrdinalIgnoreCase);

    public static void Present(Window window, bool automatic, Func<bool> hideToTray)
    {
        if (!automatic) { window.Show(); return; }
        // Create the tray icon before ever showing the main window: no startup flash.
        if (hideToTray()) return;
        // Explorer may not be ready at logon. Keep an accessible minimized window.
        window.ShowActivated = false;
        window.WindowState = WindowState.Minimized;
        window.Show();
    }
}
