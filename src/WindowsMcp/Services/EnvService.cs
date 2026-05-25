using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class EnvService : IEnvService
{
    public Task<string?> GetAsync(string name, EnvironmentVariableTarget scope = EnvironmentVariableTarget.Process, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Environment.GetEnvironmentVariable(name, scope));
    }

    public Task SetAsync(string name, string? value, EnvironmentVariableTarget scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Environment.SetEnvironmentVariable(name, value, scope);
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, string>> ListAsync(EnvironmentVariableTarget scope = EnvironmentVariableTarget.Process, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var vars = Environment.GetEnvironmentVariables(scope);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in vars)
        {
            if (entry.Key is string k && entry.Value is string v)
                result[k] = v;
        }
        return Task.FromResult(result);
    }
}
