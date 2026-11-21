using System.Runtime.InteropServices;
using System.Text;

namespace ContextBridge.App.Services;

static class NativeMethods
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int cchOutBuf, IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr packageFullNames,
                                                        ref uint bufferLength, char[]? buffer);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(string file, int iconIndex, int cxIcon, int cyIcon,
                                                  IntPtr[]? hIcons, uint[]? iconIds, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorSuccess = 0;

    public static string? LoadIndirectString(string source)
    {
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    /// <summary>Full names of installed packages of the given family for the current user.</summary>
    public static List<string> PackagesByFamily(string family)
    {
        uint count = 0, length = 0;
        int rc = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, null);
        if (rc == ErrorSuccess || count == 0) return new();
        if (rc != ErrorInsufficientBuffer) return new();
        var buffer = new char[length];
        IntPtr names = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
        try
        {
            rc = GetPackagesByPackageFamily(family, ref count, names, ref length, buffer);
            if (rc != ErrorSuccess) return new();
            int used = (int)Math.Min(length, (uint)buffer.Length);
            return new string(buffer, 0, used).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(names);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkCom { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        int GetClassID(out Guid pClassID);
        int IsDirty();
        int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }

    /// <summary>Resolves the target file path of a Windows .lnk shortcut; returns null on failure.</summary>
    public static string? ResolveShortcut(string lnkPath)
    {
        object? com = null;
        try
        {
            com = new ShellLinkCom();
            var persist = (IPersistFile)com;
            if (persist.Load(lnkPath, 0) != 0) return null;
            var link = (IShellLinkW)com;
            var sb = new StringBuilder(1024);
            if (link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0) return null;
            var target = sb.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
    }
}
