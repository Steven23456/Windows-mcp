using System.Runtime.InteropServices;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// Enumerates the Winsock catalog via the ws2_32 SPI functions
/// <c>WSCEnumProtocols</c> / <c>WSCGetProviderPath</c>.
/// </summary>
public sealed class LspEnumerator : ILspEnumerator
{
    private const int SOCKET_ERROR = -1;

    public LspProviderDto[] Enumerate()
    {
        // First call: discover the required buffer size (returns SOCKET_ERROR + WSAENOBUFS).
        uint len = 0;
        _ = WSCEnumProtocols(null, IntPtr.Zero, ref len, out _);
        if (len == 0) return Array.Empty<LspProviderDto>();

        IntPtr buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            int count = WSCEnumProtocols(null, buffer, ref len, out _);
            if (count <= 0) return Array.Empty<LspProviderDto>();

            int stride = Marshal.SizeOf<WSAPROTOCOL_INFOW>();
            var result = new List<LspProviderDto>(count);
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WSAPROTOCOL_INFOW>(buffer + i * stride);
                result.Add(new LspProviderDto(
                    (int)info.dwCatalogEntryId,
                    info.szProtocol ?? string.Empty,
                    ResolveProviderPath(info.ProviderId)));
            }
            return result.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ResolveProviderPath(Guid providerId)
    {
        var buf = new char[260];
        int len = buf.Length;
        if (WSCGetProviderPath(ref providerId, buf, ref len, out _) != 0)
            return null;

        int nul = Array.IndexOf(buf, '\0');
        string raw = new string(buf, 0, nul >= 0 ? nul : buf.Length).Trim();
        return string.IsNullOrEmpty(raw) ? null : Environment.ExpandEnvironmentVariables(raw);
    }

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSCEnumProtocols(int[]? lpiProtocols, IntPtr lpProtocolBuffer,
        ref uint lpdwBufferLength, out int lpErrno);

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WSCGetProviderPath(ref Guid lpProviderId, char[]? lpszProviderDllPath,
        ref int lpProviderDllPathLen, out int lpErrno);

    [StructLayout(LayoutKind.Sequential)]
    private struct WSAPROTOCOLCHAIN
    {
        public int ChainLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
        public uint[] ChainEntries;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WSAPROTOCOL_INFOW
    {
        public uint dwServiceFlags1;
        public uint dwServiceFlags2;
        public uint dwServiceFlags3;
        public uint dwServiceFlags4;
        public uint dwProviderFlags;
        public Guid ProviderId;
        public uint dwCatalogEntryId;
        public WSAPROTOCOLCHAIN ProtocolChain;
        public int iVersion;
        public int iAddressFamily;
        public int iMaxSockAddr;
        public int iMinSockAddr;
        public int iSocketType;
        public int iProtocol;
        public int iProtocolMaxOffset;
        public int iNetworkByteOrder;
        public int iSecurityScheme;
        public uint dwMessageSize;
        public uint dwProviderReserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szProtocol;
    }
}
