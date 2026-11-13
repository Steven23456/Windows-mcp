using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WindowsMcp.Services;

/// <summary>
/// E-8: the real <see cref="ISessionProbe"/> - the only caller of the session APIs. Every member
/// reads one fact and throws when Windows will not answer; <see cref="SessionService"/> turns a
/// failure into that field's unknown value. Nothing here changes any state.
/// </summary>
internal sealed class Win32SessionProbe : ISessionProbe
{
    public bool IsElevated() => ProcessElevation.IsElevated();

    public uint IntegrityRid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.Token;

        Native.GetTokenInformation(token, Native.TokenIntegrityLevel, IntPtr.Zero, 0, out uint needed);
        if (needed == 0) throw Failed("GetTokenInformation");
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buffer, needed, out _))
                throw Failed("GetTokenInformation");
            // TOKEN_MANDATORY_LABEL begins with the label SID's pointer; the level is its last sub-authority.
            var sid = Marshal.ReadIntPtr(buffer);
            byte count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
            if (count == 0) throw new InvalidOperationException("The integrity label SID has no sub-authority.");
            return (uint)Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public string UserName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    public int SessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    public bool UserInteractive() => Environment.UserInteractive;

    public int ConnectState() => QuerySession(Native.WTSConnectState, (buffer, bytes) =>
        bytes >= sizeof(int) ? Marshal.ReadInt32(buffer) : throw new InvalidOperationException("WTSConnectState returned no value."));

    public int SessionFlags() => QuerySession(Native.WTSSessionInfoEx, (buffer, bytes) =>
    {
        // WTSINFOEXW: DWORD Level, then (8-aligned) WTSINFOEX_LEVEL1_W { SessionId, SessionState, SessionFlags, ... }.
        const int levelOffset = 0, flagsOffset = 16;
        if (bytes < flagsOffset + sizeof(int) || Marshal.ReadInt32(buffer, levelOffset) != 1)
            throw new InvalidOperationException("WTSSessionInfoEx returned an unexpected structure.");
        return Marshal.ReadInt32(buffer, flagsOffset);
    });

    public (string? Name, int Error) InputDesktop()
    {
        // Read-only: the desktop is opened to ask its name and closed again. When the open fails
        // the error is the answer: a process that is not SYSTEM is refused the secure desktop
        // with access denied (5), and what any other code means is for the service to decide.
        var desktop = Native.OpenInputDesktop(0, false, Native.DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return (null, Marshal.GetLastWin32Error());
        try
        {
            Native.GetUserObjectInformation(desktop, Native.UOI_NAME, IntPtr.Zero, 0, out uint needed);
            if (needed == 0) throw Failed("GetUserObjectInformation");
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!Native.GetUserObjectInformation(desktop, Native.UOI_NAME, buffer, needed, out _))
                    throw Failed("GetUserObjectInformation");
                return (Marshal.PtrToStringUni(buffer) ?? throw new InvalidOperationException("The input desktop has no name."), 0);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Native.CloseDesktop(desktop);
        }
    }

    private static T QuerySession<T>(int infoClass, Func<IntPtr, uint, T> read)
    {
        if (!Native.WTSQuerySessionInformation(IntPtr.Zero, Native.WTS_CURRENT_SESSION, infoClass, out var buffer, out uint bytes))
            throw Failed("WTSQuerySessionInformation");
        try
        {
            return read(buffer, bytes);
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    // InvalidOperationException, not Win32Exception: SessionService catches it either way, and a
    // type ToolErrors does not know must never be constructed on a path a caller could reach.
    private static InvalidOperationException Failed(string api) =>
        new($"{api} failed (Win32 error {Marshal.GetLastWin32Error()}).");

    private static class Native
    {
        internal const int TokenIntegrityLevel = 25;
        internal const int WTSConnectState = 8;
        internal const int WTSSessionInfoEx = 25;
        internal const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;
        internal const uint DESKTOP_READOBJECTS = 0x0001;
        internal const int UOI_NAME = 2;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint length, out uint returnLength);

        [DllImport("advapi32.dll")]
        internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformation(IntPtr handle, int index, IntPtr info, uint length, out uint lengthNeeded);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(IntPtr desktop);
    }
}
