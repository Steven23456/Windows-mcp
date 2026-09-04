using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-9's pure core: the whole downscale decision, with no screen, bitmap or Win32 in sight
/// (roadmap C10). Every row here is a requirement of the item; the capture path only has to
/// call this and resize to what it returns.
/// </summary>
[Trait("Category", "Unit")]
public class ScaleMathTests
{
    // ---- R2a — fit to the cap ---------------------------------------------------------------

    [Theory]
    // orig            cap            userScale   expected out    expected coordinate scale
    [InlineData(3840, 2160, 1920, 1080, 1.0, 1920, 1080, 2.0)]     // 4K -> the default cap
    [InlineData(1000, 500, 1920, 1080, 1.0, 1000, 500, 1.0)]      // already inside the cap
    [InlineData(1920, 1080, 1920, 1080, 1.0, 1920, 1080, 1.0)]     // exactly on the cap
    [InlineData(3840, 2160, 1920, 1080, 0.5, 960, 540, 4.0)]       // user scale on top of the fit
    [InlineData(1920, 1080, 1920, 1080, 0.5, 960, 540, 2.0)]       // user scale with no fit needed
    [InlineData(3840, 2160, 0, 0, 1.0, 3840, 2160, 1.0)]           // no cap at all
    [InlineData(3840, 2160, -1, -1, 1.0, 3840, 2160, 1.0)]         // a negative cap is "no limit" too
    [InlineData(1000, 2000, 0, 1000, 1.0, 500, 1000, 2.0)]         // only the height limits
    [InlineData(2000, 1000, 1000, 0, 1.0, 1000, 500, 2.0)]         // only the width limits
    public void Fit_scales_to_the_cap_and_reports_the_coordinate_scale(
        int origW, int origH, int maxW, int maxH, double userScale,
        int expectedW, int expectedH, double expectedScale)
    {
        var (width, height, coordinateScale) = ScaleMath.Fit(origW, origH, maxW, maxH, userScale);

        width.Should().Be(expectedW);
        height.Should().Be(expectedH);
        coordinateScale.Should().BeApproximately(expectedScale, 1e-9);
    }

    [Fact]
    public void Fit_of_a_portrait_image_is_limited_by_the_height_and_rounds_away_from_zero()
    {
        // fit = min(1, 1920/1080, 1080/1920) = 0.5625; 1080 * 0.5625 = 607.5 -> 608 (away from zero).
        var (width, height, coordinateScale) = ScaleMath.Fit(1080, 1920, 1920, 1080, 1.0);

        width.Should().Be(608);
        height.Should().Be(1080);
        // 1080 / 608, NOT 1920 / 1080: the scale is derived from the width so a caller can undo
        // exactly the transform that was applied to the pixels it is looking at.
        coordinateScale.Should().BeApproximately(1.7763157894736843, 1e-9);
    }

    [Theory]
    // A half pixel must go UP, not to the nearest even number: 5 * 0.5 = 2.5 -> 3, and
    // 9 * 0.5 = 4.5 -> 5. Under .NET's default MidpointRounding.ToEven both would come back one
    // pixel smaller, which is the difference between "the cap is a ceiling" and "the cap is a
    // suggestion" — and the portrait case above (607.5 -> 608) cannot tell the two modes apart.
    [InlineData(5, 5, 0.5, 3, 3)]
    [InlineData(9, 5, 0.5, 5, 3)]
    [InlineData(5, 9, 0.5, 3, 5)]
    public void Fit_rounds_a_half_pixel_away_from_zero_not_to_even(
        int origW, int origH, double userScale, int expectedW, int expectedH)
    {
        var (width, height, coordinateScale) = ScaleMath.Fit(origW, origH, 0, 0, userScale);

        width.Should().Be(expectedW);
        height.Should().Be(expectedH);
        coordinateScale.Should().BeApproximately(origW / (double)expectedW, 1e-9);
    }

    // ---- R2b — degenerate sizes never collapse to zero ---------------------------------------

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.1)]
    public void Fit_never_returns_a_dimension_below_one_pixel(double userScale)
    {
        var (width, height, coordinateScale) = ScaleMath.Fit(1, 1, 1920, 1080, userScale);

        width.Should().Be(1, "a dimension is clamped to at least one pixel");
        height.Should().Be(1);
        coordinateScale.Should().Be(1.0);
    }

    [Fact]
    public void Fit_clamps_a_very_wide_thin_image_to_one_pixel_tall()
    {
        // 4000x3 into 1920 wide: fit = 0.48, 3 * 0.48 = 1.44 -> 1.
        var (width, height, _) = ScaleMath.Fit(4000, 3, 1920, 1080, 1.0);

        width.Should().Be(1920);
        height.Should().Be(1);
    }

    // ---- R2c — argument validation ------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(1.0000001)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    public void Fit_rejects_a_user_scale_outside_zero_to_one(double userScale)
    {
        var act = () => ScaleMath.Fit(1920, 1080, 1920, 1080, userScale);

        var ex = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.ParamName.Should().Be("userScale");
        ex.Message.Should().Contain("0").And.Contain("1", "the message must name the (0, 1] range");
    }

    [Theory]
    [InlineData(0, 1080, "origW")]
    [InlineData(-1, 1080, "origW")]
    [InlineData(1920, 0, "origH")]
    [InlineData(1920, -1, "origH")]
    public void Fit_rejects_a_non_positive_source_size(int origW, int origH, string paramName)
    {
        var act = () => ScaleMath.Fit(origW, origH, 1920, 1080, 1.0);

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(paramName);
    }

    // ---- R2d — a scale of exactly 1 is legal and is the identity -------------------------------

    [Fact]
    public void Fit_with_scale_one_and_no_cap_is_the_identity()
    {
        var (width, height, coordinateScale) = ScaleMath.Fit(1234, 567, 0, 0, 1.0);

        width.Should().Be(1234);
        height.Should().Be(567);
        coordinateScale.Should().Be(1.0, "nothing was scaled");
    }
}
