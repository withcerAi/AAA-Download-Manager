using DownloadManager.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace DownloadManager.Desktop;
public static class DownloadTrayArt
{
    public static BitmapSource Render(DownloadState state,double progress)
    {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){
            var color=state switch{DownloadState.Paused=>Color.FromRgb(182,123,28),DownloadState.Failed=>Color.FromRgb(185,54,57),DownloadState.Completed=>Color.FromRgb(28,130,93),_=>Color.FromRgb(23,103,187)};
            dc.DrawEllipse(new SolidColorBrush(color),null,new Point(16,16),14,14);
            var white=new Pen(Brushes.White,2.5){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
            void Line(double x,double y,double x2,double y2)=>dc.DrawLine(white,new Point(x,y),new Point(x2,y2));
            if(state==DownloadState.Completed){Line(9,16,14,21);Line(14,21,23,11);}
            else if(state==DownloadState.Paused){Line(12,10,12,22);Line(20,10,20,22);}
            else if(state==DownloadState.Failed){Line(11,11,21,21);Line(21,11,11,21);}
            else{Line(16,9,16,22);Line(10,16,16,22);Line(22,16,16,22);}
            var p=Math.Clamp(progress,0,100);var ring=new Pen(new SolidColorBrush(Color.FromRgb(104,235,181)),2);
            if(p>=100)dc.DrawEllipse(null,ring,new Point(16,16),15,15);
            else if(p>0){var radians=p*Math.PI*2/100;var geometry=new StreamGeometry();using(var context=geometry.Open()){context.BeginFigure(new Point(16,1),false,false);context.ArcTo(new Point(16+15*Math.Sin(radians),16-15*Math.Cos(radians)),new Size(15,15),0,p>50,SweepDirection.Clockwise,true,false);}dc.DrawGeometry(null,ring,geometry);}
        }
        var bitmap=new RenderTargetBitmap(32,32,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,ImageSize;public int X,Y;public uint Used,Important;}
    [StructLayout(LayoutKind.Sequential)]private struct IconInfo{[MarshalAs(UnmanagedType.Bool)]public bool IsIcon;public uint X,Y;public IntPtr Mask,Color;}
    [DllImport("gdi32.dll")]private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateBitmap(int width,int height,uint planes,uint bits,byte[] pixels);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")]public static extern bool DestroyIcon(IntPtr icon);
    public static IntPtr Create(DownloadState state,double progress)
    {
        var image=Render(state,progress);var pixels=new byte[4096];image.CopyPixels(pixels,128,0);
        var info=new BitmapInfo{Size=(uint)Marshal.SizeOf<BitmapInfo>(),Width=32,Height=-32,Planes=1,BitCount=32};var color=CreateDIBSection(IntPtr.Zero,ref info,0,out var bits,IntPtr.Zero,0);if(color==IntPtr.Zero)return IntPtr.Zero;
        IntPtr mask=IntPtr.Zero;try{Marshal.Copy(pixels,0,bits,pixels.Length);var maskBytes=new byte[128];for(var y=0;y<32;y++)for(var x=0;x<32;x++)if(pixels[(y*32+x)*4+3]==0)maskBytes[y*4+x/8]|=(byte)(0x80>>(x%8));mask=CreateBitmap(32,32,1,1,maskBytes);if(mask==IntPtr.Zero)return IntPtr.Zero;var icon=new IconInfo{IsIcon=true,Color=color,Mask=mask};return CreateIconIndirect(ref icon);}finally{DeleteObject(color);if(mask!=IntPtr.Zero)DeleteObject(mask);}
    }
}
