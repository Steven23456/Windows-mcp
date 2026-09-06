namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// B-8: one launchable application, as the in-process catalog sees it (roadmap C7 — no
/// PowerShell). <paramref name="Kind"/> is <c>shortcut</c> (a Start Menu <c>.lnk</c>),
/// <c>packaged</c> (an MSIX/appx app enumerated through the WinRT PackageManager) or
/// <c>path</c> (the caller gave a path or an executable name outright, so no catalog entry was
/// consulted). <paramref name="Target"/> is what launches it: the <c>.lnk</c> path for a
/// shortcut, the AUMID for a packaged app, the path for a path.
/// <paramref name="Source"/> is where it came from: the Start Menu folder that was scanned, or
/// <c>package:&lt;family name&gt;</c>.
/// </summary>
public record AppEntry(string Name, string Kind, string Target, string Source);

/// <summary>
/// B-8: the catalog entry a requested name resolved to. <paramref name="Strategy"/> is
/// <c>exact</c> (the name, ordinal ignoring case), <c>prefix</c> (the request is a prefix of the
/// name; the shortest such name wins) or <c>fuzzy</c>
/// (<c>max(PartialRatio, TokenSetRatio) &gt;= 70</c>, highest wins, ties by the shortest name).
/// <paramref name="Score"/> is 100 for the first two and the fuzzy score for the last.
/// </summary>
public record AppMatch(AppEntry Entry, int Score, string Strategy);

/// <summary>
/// B-8: what <c>launch</c> actually did. <paramref name="MatchedName"/> is the catalog entry's
/// name (or the path, when the request was a path), <paramref name="Strategy"/> says how the
/// request resolved (<c>path|exact|prefix|fuzzy</c>) and <paramref name="Pid"/> is the process
/// the activation returned. <paramref name="WindowDetected"/> is the boolean that replaces
/// upstream's "launched" vs "sent, window not detected" string: false leaves
/// <paramref name="Hwnd"/> and <paramref name="Title"/> null and is not an error.
/// </summary>
public record LaunchResult(
    string MatchedName,
    string Kind,
    int Score,
    int Pid,
    long? Hwnd,
    string? Title,
    bool WindowDetected,
    string Strategy);
