using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IEventLogService
{
    Task<EventLogEntryDto[]> QueryAsync(string log, string? level, string? source, DateTime? since, int max, CancellationToken ct = default);
}
