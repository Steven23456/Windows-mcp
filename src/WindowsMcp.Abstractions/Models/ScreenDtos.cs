namespace WindowsMcp.Abstractions.Models;

public record ScreenRegion(int X, int Y, int Width, int Height);
public enum ImageFormat { Png, Jpeg }
public record ScreenshotResult(byte[] Bytes, int Width, int Height, ImageFormat Format);
