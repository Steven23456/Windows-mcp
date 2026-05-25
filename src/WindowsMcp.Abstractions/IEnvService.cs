namespace WindowsMcp.Abstractions;

public interface IEnvService
{
    Task<string?> GetAsync(string name, EnvironmentVariableTarget scope = EnvironmentVariableTarget.Process, CancellationToken ct = default);
    Task SetAsync(string name, string? value, EnvironmentVariableTarget scope, CancellationToken ct = default);
    Task<Dictionary<string, string>> ListAsync(EnvironmentVariableTarget scope = EnvironmentVariableTarget.Process, CancellationToken ct = default);
}
