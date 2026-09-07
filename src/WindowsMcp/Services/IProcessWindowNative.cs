namespace WindowsMcp.Services;

/// <summary>
/// C-3 R5: the seam the graceful kill posts through — <c>EnumWindows</c> +
/// <c>GetWindowThreadProcessId</c> to find every top-level window a pid owns, and
/// <c>PostMessage(WM_CLOSE)</c> to ask each of them to close. Internal so the unit test can see
/// the posts without a desktop; the production implementation is the Win32 one.
/// </summary>
internal interface IProcessWindowNative
{
    /// <summary>Handles of every top-level window whose owning process is <paramref name="pid"/>.</summary>
    long[] TopLevelWindowsOf(int pid);

    /// <summary>Post <c>WM_CLOSE</c> to one window; false when the post failed.</summary>
    bool PostClose(long hwnd);
}
