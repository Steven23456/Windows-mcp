using FluentAssertions;
using Microsoft.Win32;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class RegistryServiceTests : IDisposable
{
    private readonly string _ns = $"Software\\WindowsMcp.Tests\\{Guid.NewGuid():N}";
    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_ns); } catch { }
    }

    [Fact]
    public async Task Set_then_Get_roundtrips_string_value()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "TestVal", "hello", "String");
        var v = await svc.GetAsync("HKCU", _ns, "TestVal");
        v.Data.Should().Be("hello");
    }

    [Fact]
    public async Task Get_throws_KeyNotFound_for_missing_path()
    {
        var svc = new RegistryService();
        Func<Task> act = () => svc.GetAsync("HKCU", "Software\\DoesNotExistXYZ123", null);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
