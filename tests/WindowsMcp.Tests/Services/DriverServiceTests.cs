using System.Collections.Generic;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class DriverServiceTests
{
    [Fact]
    public async Task ListAsync_maps_driver_fields_and_drops_nameless_stubs()
    {
        var wmi = new Mock<IWmiService>();
        wmi.Setup(w => w.QueryAsync("Win32_PnPSignedDriver", null, null, It.IsAny<CancellationToken>()))
           .ReturnsAsync(new object[]
           {
               new Dictionary<string, object>
               {
                   ["DeviceName"] = "Realtek Audio",
                   ["Manufacturer"] = "Realtek",
                   ["DriverVersion"] = "6.0.9285.1",
                   ["DriverDate"] = "20231101000000.000000-000",
                   ["IsSigned"] = true,
                   ["InfName"] = "oem12.inf",
               },
               // Nameless bus/enumerator stub — should be filtered out.
               new Dictionary<string, object> { ["Manufacturer"] = "(Standard system devices)" },
           });

        var drivers = await new DriverService(wmi.Object).ListAsync();

        drivers.Should().ContainSingle();
        drivers[0].DeviceName.Should().Be("Realtek Audio");
        drivers[0].IsSigned.Should().BeTrue();
        drivers[0].InfName.Should().Be("oem12.inf");
    }

    [Fact]
    public async Task ListAsync_handles_unsigned_and_missing_fields()
    {
        var wmi = new Mock<IWmiService>();
        wmi.Setup(w => w.QueryAsync(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
           .ReturnsAsync(new object[]
           {
               new Dictionary<string, object> { ["DeviceName"] = "Sketchy Driver", ["IsSigned"] = false },
           });

        var drivers = await new DriverService(wmi.Object).ListAsync();

        drivers.Should().ContainSingle();
        drivers[0].IsSigned.Should().BeFalse();
        drivers[0].DriverVersion.Should().BeNull();
    }
}
