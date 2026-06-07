using System.Runtime.InteropServices;
using System.Text;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

/// <summary>
/// Resolves <c>.lnk</c> targets via the Shell's <c>IShellLink</c> COM object. Only the
/// <c>GetPath</c> / <c>Load</c> vtable slots are declared (both are the first methods of
/// their interfaces, so partial declarations bind to the correct slots).
/// </summary>
public sealed class ShortcutResolver : IShortcutResolver
{
    public string? ResolveTarget(string shortcutPath)
    {
        if (string.IsNullOrEmpty(shortcutPath)) return null;
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return shortcutPath;   // not a shortcut: the file is its own target

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            string path = sb.ToString();
            return string.IsNullOrEmpty(path) ? null : Environment.ExpandEnvironmentVariables(path);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }
}
