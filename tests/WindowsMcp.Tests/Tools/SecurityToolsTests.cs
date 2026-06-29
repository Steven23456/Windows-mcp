using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class SecurityToolsTests
{
    [Fact]
    public void VerifySignature_serializes_the_inspector_verdict()
    {
        var inspector = new Mock<IAuthenticodeInspector>();
        inspector.Setup(i => i.Inspect(@"C:\app.exe"))
                 .Returns(new AuthenticodeInfo(true, "CN=Contoso"));
        var tools = new SecurityTools(inspector.Object, new Mock<ISecurityService>().Object, new Mock<ICertStoreService>().Object);

        var json = tools.VerifySignature(@"C:\app.exe");

        json.Should().Contain("true").And.Contain("Contoso");
    }

    [Fact]
    public void VerifySignature_forwards_the_path_to_the_inspector()
    {
        var inspector = new Mock<IAuthenticodeInspector>();
        inspector.Setup(i => i.Inspect(It.IsAny<string>()))
                 .Returns(new AuthenticodeInfo(false, null));
        var tools = new SecurityTools(inspector.Object, new Mock<ISecurityService>().Object, new Mock<ICertStoreService>().Object);

        tools.VerifySignature(@"C:\unknown.bin");

        inspector.Verify(i => i.Inspect(@"C:\unknown.bin"), Times.Once);
    }
}
