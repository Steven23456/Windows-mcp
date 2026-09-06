namespace WindowsMcp.Services;

/// <summary>
/// B-8: the two ways to start an application, behind a seam so <c>WindowService.LaunchAsync</c>'s
/// decision logic — path short-circuit, packaged versus shortcut, what the result reports — is
/// unit-testable without starting a process. The production implementation is the only caller of
/// <c>IApplicationActivationManager</c> and <c>ShellExecute</c>; the tests drive a recording fake.
/// </summary>
internal interface IAppActivator
{
    /// <summary>
    /// <c>IApplicationActivationManager.ActivateApplication(aumid, null, AO_NONE, out pid)</c> —
    /// the only route that hands back the PID of a packaged app.
    /// </summary>
    int ActivatePackaged(string aumid);

    /// <summary>
    /// <c>Process.Start(UseShellExecute:true)</c> on a <c>.lnk</c>, a path or an executable name.
    /// </summary>
    int StartShortcutOrPath(string target);
}
