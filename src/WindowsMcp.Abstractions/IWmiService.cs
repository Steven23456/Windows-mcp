namespace WindowsMcp.Abstractions;

public interface IWmiService
{
    Task<object[]> QueryAsync(string className, string? @namespace = null, string? where = null, CancellationToken ct = default);
}
