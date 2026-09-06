using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-12: the four fields <c>multi_monitor</c> gains. The shape matters as much as the values —
/// they are appended and defaulted so that A-8's region maths, the twelve existing
/// <c>new MonitorInfo(...)</c> constructions and the <c>screenshot</c> metadata all carry on
/// untouched.
/// </summary>
[Trait("Category", "Unit")]
public class MonitorInfoTests
{
    [Fact]
    public void The_seven_original_fields_still_come_first_and_in_the_same_order()
    {
        var parameters = typeof(MonitorInfo).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length).First().GetParameters();

        parameters.Take(7).Select(p => p.Name).Should().Equal(
            "Index", "DeviceName", "X", "Y", "Width", "Height", "IsPrimary");
        parameters.Skip(7).Select(p => p.Name).Should().Equal(
            "WorkArea", "Orientation", "EffectiveDpi", "Scale");
    }

    [Fact]
    public void The_four_new_fields_are_optional_so_every_existing_construction_still_compiles()
    {
        // This line is the assertion: it is a pre-B-12 construction, unchanged.
        var m = new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true);

        m.WorkArea.Should().BeNull("null means 'not read', which is honest for a caller that never asked");
        m.Orientation.Should().Be(0);
        m.EffectiveDpi.Should().Be(96, "96 dpi is 100% scaling: the neutral default");
        m.Scale.Should().Be(1.0);
    }

    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(168, 1.75)]
    [InlineData(192, 2.0)]
    [InlineData(240, 2.5)]
    public void Scale_is_the_effective_dpi_over_ninety_six(int dpi, double scale)
    {
        // The relationship the tool advertises. A monitor built with one and not the other is a
        // monitor whose two numbers disagree, which is worse than reporting neither.
        var m = new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true, EffectiveDpi: dpi, Scale: dpi / 96.0);

        m.Scale.Should().Be(scale);
        m.Scale.Should().Be(m.EffectiveDpi / 96.0);
    }

    [Fact]
    public void The_work_area_is_a_Bounds_in_the_same_coordinate_space_as_the_bounds()
    {
        var m = new MonitorInfo(1, "Monitor1", 1920, 0, 1920, 1080, false,
            WorkArea: new Bounds(1920, 0, 1920, 1032), Orientation: 180, EffectiveDpi: 96, Scale: 1.0);

        m.WorkArea.Should().Be(new Bounds(1920, 0, 1920, 1032));
        m.WorkArea!.X.Should().Be(m.X, "the work area is virtual-desktop pixels, not monitor-relative ones");
    }

    [Fact]
    public void The_record_serialises_the_new_fields_by_their_property_names()
    {
        var json = JsonSerializer.Serialize(new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true,
            WorkArea: new Bounds(0, 0, 1920, 1032), Orientation: 270, EffectiveDpi: 144, Scale: 1.5));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("WorkArea").GetProperty("Height").GetInt32().Should().Be(1032);
        root.GetProperty("Orientation").GetInt32().Should().Be(270);
        root.GetProperty("EffectiveDpi").GetInt32().Should().Be(144);
        root.GetProperty("Scale").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public void A_monitor_with_no_detail_serialises_WorkArea_as_null_not_as_a_zero_rect()
    {
        var json = JsonSerializer.Serialize(new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("WorkArea").ValueKind.Should().Be(JsonValueKind.Null,
            "a 0x0 rect would read as a desktop with no usable area");
    }

    [Fact]
    public void The_snapshot_does_not_carry_monitors_so_its_header_cannot_change()
    {
        // B-12's detail belongs to multi_monitor. SnapshotResult has never held a MonitorInfo
        // (the snapshot header reports only the cursor's display index), and this is what says so
        // - if a monitor ever leaks into the snapshot, SnapshotRenderer's output changes and
        // every A-2 rendering test has to be revisited.
        typeof(SnapshotResult).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(p => p.PropertyType == typeof(MonitorInfo)
                                      || p.PropertyType == typeof(MonitorInfo[]));
    }

    [Fact]
    public void MonitorInfo_gained_no_other_field()
    {
        // A guard on the shape rather than a count for its own sake: A-8's `display` selection and
        // the screenshot metadata project these by name, so an unexpected extra field means one of
        // them is out of date.
        typeof(MonitorInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(
                "Index", "DeviceName", "X", "Y", "Width", "Height", "IsPrimary",
                "WorkArea", "Orientation", "EffectiveDpi", "Scale");
    }
}
