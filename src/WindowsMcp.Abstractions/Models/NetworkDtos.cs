namespace WindowsMcp.Abstractions.Models;

public record NetworkAdapterDto(string Name, string Description, string Status, string[] IpAddresses);
public record PortInfoDto(string LocalAddress, int LocalPort, string? RemoteAddress, int? RemotePort, string State, int? OwningPid = null, string? ProcessName = null);
public record PingResult(string Host, bool Success, long? RoundtripMs);
public record WifiInfoDto(string Ssid, int SignalStrength, string Status);
