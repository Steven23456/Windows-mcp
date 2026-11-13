namespace WindowsMcp.Services;

/// <summary>
/// E-8: one member per raw read behind <see cref="SessionService"/>, so the mapping is unit-tested
/// on a fake and <see cref="Win32SessionProbe"/> is the only caller of the Windows APIs. Every
/// member returns the raw value; any failure is thrown and the service maps it to that field's
/// unknown value.
/// </summary>
internal interface ISessionProbe
{
    /// <summary><see cref="ProcessElevation.IsElevated"/>.</summary>
    bool IsElevated();

    /// <summary>
    /// GetTokenInformation(TokenIntegrityLevel): the RID of the token's mandatory label (the label
    /// SID's last sub-authority), e.g. 0x2000 for Medium.
    /// </summary>
    uint IntegrityRid();

    /// <summary>WindowsIdentity.GetCurrent().Name - DOMAIN\user.</summary>
    string UserName();

    /// <summary>The current process's terminal-services session id.</summary>
    int SessionId();

    /// <summary>Environment.UserInteractive.</summary>
    bool UserInteractive();

    /// <summary>
    /// WTSQuerySessionInformation(WTS_CURRENT_SERVER, WTS_CURRENT_SESSION, WTSConnectState): the raw
    /// WTS_CONNECTSTATE_CLASS value (0 = WTSActive ... 9 = WTSInit).
    /// </summary>
    int ConnectState();

    /// <summary>
    /// WTSQuerySessionInformation(..., WTSSessionInfoEx): the raw WTSINFOEX_LEVEL1.SessionFlags
    /// (0 = WTS_SESSIONSTATE_LOCK, 1 = WTS_SESSIONSTATE_UNLOCK, -1 = WTS_SESSIONSTATE_UNKNOWN).
    /// </summary>
    int SessionFlags();

    /// <summary>
    /// E-8 R3 (decision B): OpenInputDesktop + GetUserObjectInformation(UOI_NAME). The name and Error 0
    /// when the input desktop was opened; a null name and the Win32 error OpenInputDesktop failed with
    /// otherwise. Throws only when the desktop was opened but its name could not be read.
    /// </summary>
    (string? Name, int Error) InputDesktop();
}
