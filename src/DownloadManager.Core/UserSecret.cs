using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace DownloadManager.Core;
public static class UserSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob{public int Size;public IntPtr Data;}
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    public static string Seal(string value)=>Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value),true));
    public static string Open(string value)=>Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value),false));
    private static byte[] Transform(byte[] bytes,bool protect)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();if(bytes.Length>1048576)throw new ArgumentException();
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(Math.Max(1,bytes.Length))};Blob output=default;
        try{Marshal.Copy(bytes,0,input.Data,bytes.Length);var ok=protect?CryptProtectData(ref input,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);if(!ok)throw new Win32Exception(Marshal.GetLastWin32Error());var result=new byte[output.Size];Marshal.Copy(output.Data,result,0,result.Length);return result;}
        finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);Marshal.Copy(new byte[input.Size],0,input.Data,input.Size);Marshal.FreeHGlobal(input.Data);if(output.Data!=IntPtr.Zero)LocalFree(output.Data);}
    }
}
