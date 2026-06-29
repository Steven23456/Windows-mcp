namespace WindowsMcp.Abstractions.Models;

/// <summary>Top-level storage-health report. Empty arrays + a Notes entry signal a degraded probe.</summary>
public record StorageHealthReport(
    PhysicalDiskInfo[] PhysicalDisks,
    DiskInfo[] Disks,
    VolumeInfo[] Volumes,
    DiskEventInfo[] RecentEvents,
    string[] Notes);

/// <summary>A physical disk: model, bus/media type, and SMART health + reliability counters.</summary>
public record PhysicalDiskInfo(
    int? DeviceId,
    string? FriendlyName,
    string? MediaType,
    string? BusType,
    double? SizeGb,
    string? HealthStatus,
    string? OperationalStatus,
    ReliabilityInfo? Reliability);

/// <summary>SMART reliability counters (null fields = not reported by the device, common over USB).</summary>
public record ReliabilityInfo(
    int? Temperature,
    long? ReadErrorsUncorrected,
    long? WriteErrorsUncorrected,
    int? Wear,
    long? PowerOnHours);

/// <summary>A disk at the storage-management layer (online/offline, health, partition style).</summary>
public record DiskInfo(
    int Number,
    string? FriendlyName,
    string? OperationalStatus,
    string? HealthStatus,
    bool IsOffline,
    string? PartitionStyle,
    double? SizeGb);

/// <summary>A volume mapped to its disk/partition. FreeGb is only populated when usage was probed.</summary>
public record VolumeInfo(
    string? DriveLetter,
    string? FileSystemLabel,
    string? FileSystem,
    string? HealthStatus,
    string? OperationalStatus,
    double? SizeGb,
    double? FreeGb,
    int? DiskNumber,
    int? PartitionNumber,
    bool UsageProbed,
    bool UsageTimedOut);

/// <summary>A recent disk-stack Error/Warning event from the System log.</summary>
public record DiskEventInfo(
    DateTime? Time,
    int Id,
    string? Source,
    string? Level,
    string? Message);
