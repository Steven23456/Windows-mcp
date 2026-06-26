namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// A PnP device driver (from Win32_PnPSignedDriver). Old or unsigned drivers are a real attack
/// surface (BYOVD — bring-your-own-vulnerable-driver).
/// </summary>
public record DriverInfo(
    string? DeviceName,
    string? Manufacturer,
    string? DriverVersion,
    string? DriverDate,
    bool? IsSigned,
    string? InfName);
