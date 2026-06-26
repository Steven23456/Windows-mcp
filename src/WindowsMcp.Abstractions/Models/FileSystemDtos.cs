namespace WindowsMcp.Abstractions.Models;

/// <summary>An NTFS alternate data stream (e.g. <c>:Zone.Identifier:$DATA</c>).</summary>
public record AlternateStreamInfo(string Name, long Size);

/// <summary>
/// NTFS forensic view of a path: alternate data streams (hidden payloads / Zone.Identifier) and
/// the reparse target if the path is a symlink/junction. <see cref="LinkTarget"/> is null for a
/// normal file/dir.
/// </summary>
public record FileStreamsDto(string Path, string? LinkTarget, AlternateStreamInfo[] AlternateStreams);

public record FileInfoDto(
    string Path,
    long Size,
    DateTime Created,
    DateTime Modified,
    DateTime Accessed,
    string Attributes,
    bool IsDirectory);

public record FileSearchHit(string Path, long Size, DateTime Modified);

public record RegistryValueDto(string Path, string Name, object? Data, string Kind);

public record ServiceDto(string Name, string DisplayName, string Status, string StartType);

public record EventLogEntryDto(int Id, string Source, string Message, string Level, DateTime Time);

public record ScheduledTaskDto(string Name, string Path, string State, DateTime? LastRun, DateTime? NextRun);

/// <summary>
/// Richer scheduled-task projection used by persistence reporting: includes the primary
/// exec action (path + arguments) and the distinct trigger types, across all task folders.
/// </summary>
public record ScheduledTaskDetailDto(
    string Name,
    string Path,
    string State,
    string? ActionPath,
    string? ActionArguments,
    string[] Triggers);
