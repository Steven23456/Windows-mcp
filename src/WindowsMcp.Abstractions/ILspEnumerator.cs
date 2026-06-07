using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// Enumerates the Winsock 2 service-provider catalog (base providers and any layered
/// service providers / LSPs). Mirrors the HiJackThis "O10 - Winsock LSP" section.
/// </summary>
public interface ILspEnumerator
{
    LspProviderDto[] Enumerate();
}
