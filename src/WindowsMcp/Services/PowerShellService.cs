using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class PowerShellService : IPowerShellService
{
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _backstop;
    private bool _disposed;

    // Backstop so a runaway script (e.g. an accidental `while($true){}`) can't hold the
    // serialization gate forever and wedge every PowerShell-backed tool. Deliberately generous —
    // longer than any legitimate caller budget (storage_health caps its own CTS at 300s). The
    // normal cancellation path is the caller's CancellationToken; this is the last-resort teardown.
    private static readonly TimeSpan DefaultBackstop = TimeSpan.FromMinutes(15);

    public PowerShellService(ILogger<PowerShellService> log) : this((ILogger)log, null) { }

    // Test ctor accepting non-generic ILogger (+ optional shorter backstop for tests).
    public PowerShellService(ILogger log, TimeSpan? backstopTimeout = null)
    {
        _log = log;
        _backstop = backstopTimeout ?? DefaultBackstop;
    }

    public async Task<PSResult> RunAsync(string command, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PowerShellService));
        ct.ThrowIfCancellationRequested();

        string? scriptFileToDelete = null;

        // Acquire the gate under the CALLER's token only. The backstop must bound this call's
        // *execution*, not the time it spends queued behind other callers — otherwise a caller
        // deep in the queue can burn its entire "runaway-script" budget just waiting, and get
        // cancelled before its own (perfectly fine) command ever runs.
        await _gate.WaitAsync(ct);
        try
        {
            // Now that we hold the gate, start the execution backstop and link the caller's token.
            using var timeoutCts = new CancellationTokenSource(_backstop);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var token = linkedCts.Token;

            // Build the invocation. See PowerShellInvocation for why stdin is NOT used and how
            // the EncodedCommand / temp-file fallback works.
            var (arguments, tempScript) = await PowerShellInvocation.BuildArgumentsAsync(command, token);
            scriptFileToDelete = tempScript;

            using var proc = new Process { StartInfo = PowerShellInvocation.CreateStartInfo(arguments) };
            proc.Start();

            // Register the kill BEFORE any await: if cancellation (caller or backstop) fires
            // early, the child must still be torn down or it orphans.
            using var ctReg = token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            // Close stdin immediately — the script is passed via the command line, and leaving
            // the pipe open would make PowerShell wait for input that never comes.
            proc.StandardInput.Close();

            // Read both streams concurrently to avoid pipe deadlock on large output.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(token);
            var stderrTask = proc.StandardError.ReadToEndAsync(token);

            await proc.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var errors = ExtractErrors(stderr);

            return new PSResult(
                Success: proc.ExitCode == 0 && errors.Length == 0,
                Stdout: stdout,
                Stderr: stderr,
                ExitCode: proc.ExitCode,
                Errors: errors);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "PowerShell execution failed");
            return new PSResult(false, "", ex.Message, -1, new[] { ex.Message });
        }
        finally
        {
            if (scriptFileToDelete is not null)
            {
                try { File.Delete(scriptFileToDelete); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to delete temp script {Path}", scriptFileToDelete); }
            }
            _gate.Release();
        }
    }

    /// <summary>
    /// Extracts the lines that should count as errors from a child's raw stderr.
    /// </summary>
    /// <remarks>
    /// Windows PowerShell 5.1 with redirected stderr wraps its error/warning/progress/verbose
    /// streams in a CLIXML document (a <c>#&lt; CLIXML</c> header line followed by
    /// <c>&lt;Objs&gt;</c> XML). Benign records land there too — e.g. an <c>Obj S="progress"</c>
    /// "Preparing modules for first use." on first-touch module import, or <c>S S="warning"</c>
    /// from Write-Warning — so non-empty stderr does NOT mean the command failed. Only genuine
    /// <c>&lt;S S="Error"&gt;</c> records count against Success; <see cref="PSResult.Stderr"/>
    /// keeps the raw stream. Non-CLIXML stderr (native children write raw bytes) and unparseable
    /// CLIXML (raw bytes interleaved with it) fall back to the plain line split.
    /// </remarks>
    internal static string[] ExtractErrors(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return Array.Empty<string>();

        string[] RawLines() => stderr.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        const string ClixmlHeader = "#< CLIXML";
        if (!stderr.StartsWith(ClixmlHeader, StringComparison.Ordinal))
            return RawLines();

        int xmlStart = stderr.IndexOf('<', ClixmlHeader.Length);
        if (xmlStart < 0) return Array.Empty<string>(); // header only, no records at all

        try
        {
            // One <Objs> document per stream flush can be concatenated; wrap to parse as one.
            var root = XElement.Parse("<r>" + stderr[xmlStart..] + "</r>");
            return root.Descendants()
                .Where(e => e.Name.LocalName == "S" && string.Equals(
                    (string?)e.Attribute("S"), "Error", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => DecodeClixmlEscapes(e.Value).Split('\n',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();
        }
        catch (System.Xml.XmlException)
        {
            return RawLines();
        }
    }

    // CLIXML escapes characters that are invalid in XML text as _xHHHH_ (CRLF arrives as
    // _x000D__x000A_); a surrogate pair is two consecutive escapes, so per-char decode is exact.
    private static readonly Regex ClixmlEscape = new("_x([0-9A-Fa-f]{4})_", RegexOptions.Compiled);

    private static string DecodeClixmlEscapes(string value) =>
        ClixmlEscape.Replace(value, m => ((char)ushort.Parse(
            m.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
