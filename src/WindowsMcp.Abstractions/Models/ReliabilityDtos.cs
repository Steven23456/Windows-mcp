namespace WindowsMcp.Abstractions.Models;

/// <summary>A crash minidump file in C:\Windows\Minidump.</summary>
public record MinidumpInfo(string Name, long SizeBytes, DateTime TimeUtc);

/// <summary>A Win32_ReliabilityRecords failure entry (app/OS/hardware failure).</summary>
public record ReliabilityRecord(string? SourceName, string? Message, int? EventId);

/// <summary>
/// System stability snapshot: crash minidumps + recent reliability failure records.
/// <see cref="Note"/> is set when the reliability records can't be read.
/// </summary>
public record ReliabilityReport(MinidumpInfo[] Minidumps, ReliabilityRecord[] RecentFailures, string? Note);
