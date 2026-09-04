using System.Diagnostics;
using System.Text;

namespace WindowsMcp.Services;

/// <summary>
/// Builds powershell.exe invocations. Shared by <see cref="PowerShellService"/> (foreground,
/// serialized) and <see cref="JobService"/> (background jobs) so both spawn the child EXACTLY
/// the same way — same exe, same flags, same encoding preamble, same EncodedCommand/temp-file
/// fallback, same stdin/stdout/stderr redirection.
/// </summary>
internal static class PowerShellInvocation
{
    // System PowerShell is guaranteed present at this path on Windows 7+.
    // Avoids the broken InitialSessionState.CreateDefault2 path in the PS NuGet
    // SDK when running under PublishSingleFile=true: Assembly.Location returns ""
    // in single-file mode, then Path.Combine chokes inside PSSnapInReader.
    // Snap-in DLLs are not bundled in the single-file image.
    internal const string ExePath =
        @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    // Windows caps a command line at 32767 chars. -EncodedCommand base64s UTF-16LE, so the
    // encoded form is ~2.67x the script length; stay well clear of the ceiling.
    private const int MaxEncodedCommandChars = 30_000;

    /// <summary>
    /// Produces the powershell.exe arguments for <paramref name="command"/>, plus the path of a
    /// temp script file to delete afterwards (null when none was needed).
    /// </summary>
    /// <remarks>
    /// We deliberately do NOT use <c>-Command -</c> with the script piped to stdin. PowerShell
    /// reads piped stdin and evaluates it LINE BY LINE as independent statements, so every
    /// multi-line construct (hashtable literal, try/catch, foreach, function, wrapped assignment)
    /// is silently mangled — and the process still exits 0 with EMPTY stdout. That is what made
    /// <c>disk_inspect mode:reclaimable</c> return nothing on exit 0: its script ends in a
    /// multi-line <c>[PSCustomObject]@{...} | ConvertTo-Json</c>. Piping also left the input
    /// encoding at the console default, corrupting non-ASCII.
    ///
    /// <c>-EncodedCommand</c> passes the script as one base64 UTF-16LE blob: parsed as a single
    /// unit, encoding explicit, no quoting hazards. Its only limit is the command-line length —
    /// and since stdin had no such limit, an oversized script falls back to a temp <c>.ps1</c>
    /// run with <c>-File</c> so large scripts do not regress.
    /// </remarks>
    internal static async Task<(string Arguments, string? TempScript)> BuildArgumentsAsync(
        string command, CancellationToken token)
    {
        const string CommonFlags = "-NoProfile -NonInteractive -ExecutionPolicy Bypass";

        // Line 1: we read the child's stdout as UTF-8 (StandardOutputEncoding), but Windows
        // PowerShell 5.1 WRITES stdout in the console OEM codepage, so non-ASCII arrives corrupted
        // (café -> caf?). Force the writer side to match the reader side. Kept to one line and
        // try/caught so it can never break a caller's script; `catch {}` deliberately swallows, as
        // failing to set an encoding must not fail the command.
        //
        // Line 2 (D-8): with stderr redirected, every Write-Progress — including the ones inside
        // Invoke-WebRequest and first-use module autoload — becomes a CLIXML record on stderr that
        // the model then reads and ignores (measured 2026-09-04: 596 characters for a single
        // progress record). There is no console to draw a progress bar on, so nothing is lost, and
        // Invoke-WebRequest / Invoke-RestMethod are markedly faster without it. Script scope, so a
        // caller's script can set it back — ClixmlStderr then drops the records anyway.
        const string Preamble =
            "try{[Console]::OutputEncoding=[System.Text.Encoding]::UTF8}catch{}\n" +
            "$ProgressPreference='SilentlyContinue'\n";

        var payload = Preamble + command;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        if (encoded.Length <= MaxEncodedCommandChars)
            return ($"{CommonFlags} -EncodedCommand {encoded}", null);

        // Too long for a command line: write it out and run the file instead.
        // UTF-8 *with BOM* — Windows PowerShell 5.1 assumes the ANSI codepage for a BOM-less
        // file and mangles non-ASCII (the em-dash parse trap).
        var path = Path.Combine(Path.GetTempPath(), $"winmcp-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), token);
        return ($"{CommonFlags} -File \"{path}\"", path);
    }

    /// <summary>Creates the ProcessStartInfo for a powershell.exe child.</summary>
    /// <remarks>
    /// -NoProfile: skip user profile load (faster, deterministic).
    /// -NonInteractive: never prompt.
    /// -ExecutionPolicy Bypass: allow scripts.
    /// Stdin is redirected even though we never write to it: this process is an MCP STDIO
    /// server, so our own stdin is the JSON-RPC channel. An un-redirected child would INHERIT
    /// that handle and could consume protocol bytes. Callers must Close() the child's stdin
    /// immediately after Start(), or PowerShell waits for input that never comes.
    /// </remarks>
    internal static ProcessStartInfo CreateStartInfo(string arguments) => new()
    {
        FileName = ExePath,
        Arguments = arguments,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
}
