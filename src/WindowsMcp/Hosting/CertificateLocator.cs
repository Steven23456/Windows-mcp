using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WindowsMcp.Hosting;

/// <summary>
/// Resolves <c>--cert-thumbprint</c> to a certificate Kestrel can serve TLS with: found by
/// thumbprint in <c>LocalMachine\My</c>, then <c>CurrentUser\My</c>, with a private key this
/// process can actually open.
/// </summary>
/// <remarks>
/// Two failure modes that look identical from a bare "certificate not found" are told apart here,
/// because each has a different fix: no private key at all (a public-only import), and a key the
/// current account may not read — the default for a <c>LocalMachine\My</c> cert created by an
/// elevated <c>New-SelfSignedCertificate</c> (key ACL = SYSTEM + Administrators), which otherwise
/// surfaces only at the first TLS handshake as "No credentials are available in the security
/// package". The key is probed by opening it, which is the only reliable test.
/// </remarks>
internal static class CertificateLocator
{
    private static readonly (StoreLocation Location, StoreName Name)[] SearchOrder =
    [
        (StoreLocation.LocalMachine, StoreName.My),
        (StoreLocation.CurrentUser, StoreName.My),
    ];

    public static string SearchedStores =>
        string.Join(", ", SearchOrder.Select(s => $@"{s.Location}\{s.Name}"));

    /// <summary>Returns the first usable certificate; the caller owns it (Kestrel keeps it for the process lifetime).</summary>
    /// <exception cref="OptionsException">The thumbprint is not 40 hex digits.</exception>
    /// <exception cref="InvalidOperationException">No usable certificate; the message says what was found and how to fix it.</exception>
    public static X509Certificate2 Find(string thumbprint)
    {
        var tp = ServerOptions.NormalizeThumbprint(thumbprint);
        var problems = new List<string>();

        foreach (var (location, name) in SearchOrder)
        {
            var where = $@"{location}\{name}";
            X509Certificate2Collection matches;
            try
            {
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                // validOnly:false — a self-signed cert is the expected case for a private RDP box.
                matches = store.Certificates.Find(X509FindType.FindByThumbprint, tp, validOnly: false);
            }
            catch (CryptographicException ex)
            {
                problems.Add($"{where}: could not open the store ({ex.Message})");
                continue;
            }

            foreach (var cert in matches)
            {
                if (!cert.HasPrivateKey)
                {
                    problems.Add($"{where}: found '{cert.Subject}' but it has no private key (public-only import?)");
                    cert.Dispose();
                    continue;
                }

                var keyError = ProbePrivateKey(cert);
                if (keyError is null)
                    return cert;

                problems.Add(
                    $"{where}: found '{cert.Subject}' but this account cannot open its private key ({keyError}). " +
                    "Grant this account read access to the key (certlm.msc > right-click > All Tasks > Manage Private Keys) " +
                    @"or create the certificate in Cert:\CurrentUser\My instead");
                cert.Dispose();
            }
        }

        var detail = problems.Count == 0
            ? $"not found in {SearchedStores}"
            : string.Join("; ", problems);

        throw new InvalidOperationException(
            $"Certificate {tp}: {detail}. " +
            @"To create a self-signed one: New-SelfSignedCertificate -DnsName <host> -CertStoreLocation Cert:\CurrentUser\My");
    }

    /// <summary>Null when the private key opens; otherwise the reason it did not.</summary>
    private static string? ProbePrivateKey(X509Certificate2 cert)
    {
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is not null) return null;
            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa is not null) return null;
            return "unsupported key algorithm; expected RSA or ECDSA";
        }
        catch (CryptographicException ex)
        {
            return ex.Message;
        }
    }
}
