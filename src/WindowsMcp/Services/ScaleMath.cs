namespace WindowsMcp.Services;

/// <summary>
/// The whole of A-9's downscale arithmetic, with no dependency on a screen, a bitmap or Win32 —
/// so every rule below is unit-tested without a capture (roadmap C10).
/// </summary>
internal static class ScaleMath
{
    /// <summary>
    /// Fits <paramref name="origW"/>x<paramref name="origH"/> inside the
    /// <paramref name="maxW"/>x<paramref name="maxH"/> cap (a cap of zero or less is ignored),
    /// then applies <paramref name="userScale"/> on top. Never upscales.
    /// </summary>
    /// <returns>
    /// The output size and <c>CoordinateScale</c> = <paramref name="origW"/> / Width — the
    /// factor a caller multiplies image pixel coordinates by to get virtual-desktop pixels. It is
    /// derived from the width because that is the transform actually applied to the pixels; when
    /// the height is the limiting side the two differ by rounding, and the width one is the
    /// honest one.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="userScale"/> outside (0, 1] (NaN included); <paramref name="origW"/> or
    /// <paramref name="origH"/> not positive.
    /// </exception>
    internal static (int Width, int Height, double CoordinateScale) Fit(
        int origW, int origH, int maxW, int maxH, double userScale)
    {
        // Written as a positive test so NaN (which fails every comparison) is rejected too.
        if (!(userScale > 0 && userScale <= 1))
            throw new ArgumentOutOfRangeException(nameof(userScale), userScale, "Scale must be in (0, 1].");
        if (origW <= 0)
            throw new ArgumentOutOfRangeException(nameof(origW), origW, "Source width must be positive.");
        if (origH <= 0)
            throw new ArgumentOutOfRangeException(nameof(origH), origH, "Source height must be positive.");

        double fit = 1.0;
        if (maxW > 0) fit = Math.Min(fit, maxW / (double)origW);
        if (maxH > 0) fit = Math.Min(fit, maxH / (double)origH);
        double total = fit * userScale;

        int width = Math.Max(1, (int)Math.Round(origW * total, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(origH * total, MidpointRounding.AwayFromZero));
        return (width, height, origW / (double)width);
    }
}
