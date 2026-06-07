using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class AuthenticodeInspectorTests
{
    [Fact]
    public void Inspect_trusts_a_signed_system_binary()
    {
        // kernel32.dll is always present and signed (embedded and/or via catalog).
        var path = Path.Combine(Environment.SystemDirectory, "kernel32.dll");

        var info = new AuthenticodeInspector().Inspect(path);

        info.Trusted.Should().BeTrue("a core Windows DLL must verify via embedded or catalog signing");
    }

    [Fact]
    public void Inspect_returns_untrusted_for_unsigned_file()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wmcp_unsigned_{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(tmp, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 });
        try
        {
            var info = new AuthenticodeInspector().Inspect(tmp);

            info.Trusted.Should().BeFalse();
            info.Signer.Should().BeNull();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Inspect_handles_null_and_missing_paths()
    {
        var insp = new AuthenticodeInspector();

        insp.Inspect(null).Should().Be(new WindowsMcp.Abstractions.Models.AuthenticodeInfo(false, null));
        insp.Inspect(@"C:\does\not\exist_wmcp_xyz.dll").Trusted.Should().BeFalse();
    }
}
