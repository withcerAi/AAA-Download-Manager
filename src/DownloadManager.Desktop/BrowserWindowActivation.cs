using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DownloadManager.Desktop;

/// <summary>One foreground request for a browser-initiated review, never a focus-stealing timer.</summary>
public static class BrowserWindowActivation
{
    public static void Prepare(Window window)
    {
        var previousTopmost = window.Topmost;
        var previousShowActivated = window.ShowActivated;
        window.ShowActivated = true;
        // Make the first rendered frame visible even when Windows denies activation.
        // Restored immediately after that frame; this is not an always-on-top dialog.
        window.Topmost = true;
        var finished = false;
        void Restore()
        {
            if (finished) return;
            finished = true;
            window.ContentRendered -= Rendered;
            window.Closed -= Closed;
            window.Topmost = previousTopmost;
            window.ShowActivated = previousShowActivated;
        }
        void Rendered(object? sender, EventArgs args)
        {
            try { BringToFront(window); }
            finally { Restore(); }
        }
        void Closed(object? sender, EventArgs args) => Restore();
        window.ContentRendered += Rendered;
        window.Closed += Closed;
    }

    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        window.Activate();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
