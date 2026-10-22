using System.Diagnostics;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test <b>Skipped</b>, with the reason, when this
/// machine cannot make a directory junction under the temp folder with <c>cmd /c mklink /J</c>
/// (no admin needed, but a locked-down profile or a non-NTFS temp volume can refuse). xunit 2.9
/// has no dynamic skip, so the probe runs in the constructor, at discovery time, and cleans up
/// after itself.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmcp-junction-probe-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(dir, "target");
        var link = Path.Combine(dir, "link");
        try
        {
            Directory.CreateDirectory(target);
            if (!TryCreateJunction(link, target, out var error))
                Skip = "This machine cannot create a directory junction with 'mklink /J' under the temp folder: " + error;
        }
        catch (Exception ex)
        {
            Skip = "This machine cannot create a directory junction under the temp folder: " + ex.Message;
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// <c>cmd /c mklink /J "link" "target"</c>; true when the junction exists afterwards.
    /// Remove it with <c>Directory.Delete(link)</c> (non-recursive), which deletes the link only.
    /// </summary>
    internal static bool TryCreateJunction(string link, string target, out string error)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var made = process.ExitCode == 0
                   && new DirectoryInfo(link) is { Exists: true } info
                   && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        error = made ? "" : $"exit {process.ExitCode}: {(stderr + " " + stdout).Trim()}";
        return made;
    }
}
