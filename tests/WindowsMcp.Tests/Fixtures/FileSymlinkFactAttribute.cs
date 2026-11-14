namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test <b>Skipped</b>, with the reason, when this
/// process cannot create a FILE symbolic link under the temp folder (no Developer Mode and no
/// SeCreateSymbolicLinkPrivilege). The sibling of <see cref="JunctionFactAttribute"/>: a junction
/// needs no privilege but is a directory, and some rules are about a link that looks like a file.
/// The probe runs at discovery time and cleans up after itself.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FileSymlinkFactAttribute : FactAttribute
{
    public FileSymlinkFactAttribute()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmcp-symlink-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "target.txt");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip = "This process cannot create a file symbolic link under the temp folder (no Developer Mode / "
                   + "SeCreateSymbolicLinkPrivilege): " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
