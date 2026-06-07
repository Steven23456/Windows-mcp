using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// Catalog-aware Authenticode inspector. Uses WinVerifyTrust for the trust verdict
/// (embedded signature first, then a security-catalog fallback for system/driver files)
/// and reads the embedded signer subject separately when one exists.
/// </summary>
public sealed class AuthenticodeInspector : IAuthenticodeInspector
{
    public AuthenticodeInfo Inspect(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return new AuthenticodeInfo(false, null);

        bool trusted;
        try { trusted = NativeTrust.IsTrusted(filePath); }
        catch { trusted = false; }

        return new AuthenticodeInfo(trusted, TryGetEmbeddedSigner(filePath));
    }

    private static string? TryGetEmbeddedSigner(string path)
    {
        try
        {
            // SYSLIB0057: CreateFromSignedFile is obsolete with no functional replacement —
            // X509CertificateLoader loads certificate *files*, it does not extract the embedded
            // signer from a PE. The trust verdict comes from WinVerifyTrust; this only reads
            // the signer subject string for display, so the obsolete extractor is acceptable.
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch
        {
            // No embedded signature (the file may still be catalog-trusted).
            return null;
        }
    }

    private static class NativeTrust
    {
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_CHOICE_CATALOG = 2;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;
        private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE = new(-1);

        public static bool IsTrusted(string path)
        {
            int embedded = VerifyEmbedded(path);
            if (embedded == 0) return true;
            // Only consult catalogs when the file simply has no embedded signature.
            if (unchecked((uint)embedded) != TRUST_E_NOSIGNATURE) return false;
            return VerifyViaCatalog(path);
        }

        private static int VerifyEmbedded(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var wd = NewWintrustData(WTD_CHOICE_FILE, pFile);
                return VerifyAndClose(ref wd);
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }

        private static bool VerifyViaCatalog(string path)
        {
            IntPtr hFile = CreateFile(path, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hFile == IntPtr.Zero || hFile == INVALID_HANDLE) return false;

            IntPtr hCatAdmin = IntPtr.Zero;
            IntPtr hCatInfo = IntPtr.Zero;
            try
            {
                if (!CryptCATAdminAcquireContext2(out hCatAdmin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
                    return false;

                uint hashSize = 0;
                CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref hashSize, null, 0);
                if (hashSize == 0) return false;

                byte[] hash = new byte[hashSize];
                if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile, ref hashSize, hash, 0))
                    return false;

                IntPtr prev = IntPtr.Zero;
                hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, hashSize, 0, ref prev);
                if (hCatInfo == IntPtr.Zero) return false;   // hash not in any catalog → not catalog-signed

                var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (!CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0)) return false;

                return VerifyCatalogMember(ci.wszCatalogFile, path, Convert.ToHexString(hash), hash, hCatAdmin) == 0;
            }
            finally
            {
                if (hCatInfo != IntPtr.Zero && hCatAdmin != IntPtr.Zero)
                    CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
                if (hCatAdmin != IntPtr.Zero) CryptCATAdminReleaseContext(hCatAdmin, 0);
                if (hFile != INVALID_HANDLE) CloseHandle(hFile);
            }
        }

        private static int VerifyCatalogMember(string catalogFile, string memberFile, string memberTag, byte[] hash, IntPtr hCatAdmin)
        {
            IntPtr pHash = Marshal.AllocHGlobal(hash.Length);
            var catInfo = new WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                pcwszCatalogFilePath = catalogFile,
                pcwszMemberTag = memberTag,
                pcwszMemberFilePath = memberFile,
                hMemberFile = IntPtr.Zero,
                pbCalculatedFileHash = pHash,
                cbCalculatedFileHash = (uint)hash.Length,
                // Required for SHA-256 catalogs: without the admin handle, WinVerifyTrust
                // assumes SHA-1 and fails to match modern catalog members (TRUST_E_NOSIGNATURE).
                hCatAdmin = hCatAdmin,
            };
            IntPtr pCat = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
            try
            {
                Marshal.Copy(hash, 0, pHash, hash.Length);
                Marshal.StructureToPtr(catInfo, pCat, false);
                var wd = NewWintrustData(WTD_CHOICE_CATALOG, pCat);
                return VerifyAndClose(ref wd);
            }
            finally
            {
                Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(pCat);
                Marshal.FreeHGlobal(pCat);
                Marshal.FreeHGlobal(pHash);
            }
        }

        private static WINTRUST_DATA NewWintrustData(uint unionChoice, IntPtr pInfo) => new()
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = unionChoice,
            pInfoUnion = pInfo,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };

        private static int VerifyAndClose(ref WINTRUST_DATA wd)
        {
            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref wd);
            wd.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref wd);   // free hWVTStateData
            return result;
        }

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem,
            [MarshalAs(UnmanagedType.LPWStr)] string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile,
            ref uint pcbHash, byte[]? pbHash, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash,
            uint dwFlags, ref IntPtr phPrevCatInfo);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

        [DllImport("wintrust.dll", SetLastError = true)]
        private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pInfoUnion;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_CATALOG_INFO
        {
            public uint cbStruct;
            public uint dwCatalogVersion;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszCatalogFilePath;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberTag;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberFilePath;
            public IntPtr hMemberFile;
            public IntPtr pbCalculatedFileHash;
            public uint cbCalculatedFileHash;
            public IntPtr pcCatalogContext;
            public IntPtr hCatAdmin;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CATALOG_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
        }
    }
}
