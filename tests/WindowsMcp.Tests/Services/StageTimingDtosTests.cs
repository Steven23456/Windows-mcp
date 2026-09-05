using System.Text.Json;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-14 (R4): the wire contract of the profiling DTOs. Profiling is off by default and its fields
/// must be INVISIBLE when it is off — otherwise every existing <c>snapshot format:"json"</c>
/// response changes shape for callers who never asked for timings (the same rule
/// <see cref="SnapshotDtosTests"/> pins for A-4's truncation fields).
/// </summary>
[Trait("Category", "Unit")]
public class StageTimingDtosTests
{
    private static SnapshotResult Snapshot(StageTiming[]? stages) =>
        new([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 12, stages);

    private static ScreenshotResult Capture(StageTiming[]? stages) =>
        new([1, 2], 2, 2, ImageFormat.Png, 2, 2, 1.0, null, 0, stages);

    // ---- StageTiming itself -------------------------------------------------------------------

    [Fact]
    public void StageTiming_carries_a_name_and_whole_milliseconds()
    {
        var timing = new StageTiming("walk", 130);

        timing.Stage.Should().Be("walk");
        timing.Ms.Should().Be(130L);
    }

    // ---- SnapshotResult --------------------------------------------------------------------

    [Fact]
    public void SnapshotResult_stages_default_to_null_so_every_pre_A14_construction_still_compiles()
    {
        // The eleven-argument form is what every caller in the tree uses today.
        var result = new SnapshotResult([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 12);

        result.Stages.Should().BeNull();
    }

    [Fact]
    public void SnapshotResult_without_stages_serialises_exactly_as_it_did_before_A14()
    {
        var json = JsonSerializer.Serialize(Snapshot(null));

        json.Should().NotContain("Stages", "an unprofiled snapshot's JSON is unchanged");
        json.Should().Contain("\"CaptureMs\":12", "and still carries the total it always did");
    }

    [Fact]
    public void SnapshotResult_with_stages_serialises_them_in_order()
    {
        var json = JsonSerializer.Serialize(Snapshot([new StageTiming("header", 12), new StageTiming("walk", 130)]));

        using var doc = JsonDocument.Parse(json);
        var stages = doc.RootElement.GetProperty("Stages");
        stages.GetArrayLength().Should().Be(2);
        stages[0].GetProperty("Stage").GetString().Should().Be("header");
        stages[0].GetProperty("Ms").GetInt64().Should().Be(12);
        stages[1].GetProperty("Stage").GetString().Should().Be("walk");
        stages[1].GetProperty("Ms").GetInt64().Should().Be(130);
    }

    // ---- ScreenshotResult / CaptureOptions ---------------------------------------------------

    [Fact]
    public void ScreenshotResult_stages_default_to_null()
    {
        new ScreenshotResult([1], 1, 1, ImageFormat.Png, 1, 1, 1.0).Stages.Should().BeNull();
    }

    [Fact]
    public void ScreenshotResult_without_stages_writes_no_stages_key()
    {
        JsonSerializer.Serialize(Capture(null)).Should().NotContain("Stages");
    }

    [Fact]
    public void ScreenshotResult_with_stages_writes_them()
    {
        JsonSerializer.Serialize(Capture([new StageTiming("encode", 7)]))
            .Should().Contain("\"Stage\":\"encode\"").And.Contain("\"Ms\":7");
    }

    [Fact]
    public void CaptureOptions_does_not_profile_by_default()
    {
        new CaptureOptions().Profile.Should().BeFalse("profiling costs stopwatch reads on every capture");
    }
}
