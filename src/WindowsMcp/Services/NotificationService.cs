using System.Runtime.InteropServices;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// C-4: toasts in-process through <see cref="IToastSink"/> (WinRT in production, a fake in
/// tests) instead of a PowerShell cold start. Windows uses the AppUserModelId as the toast's
/// identity and drops an id it does not know (<c>0x80070490</c>, element not found); for the
/// server's own default id the service registers <c>HKCU\Software\Classes\AppUserModelId\&lt;id&gt;</c>
/// once, which is the documented registration for an unpackaged exe. A caller's id is never
/// written. The registration takes a moment to be picked up, so the one known transient
/// failure is retried once.
/// </summary>
public sealed class NotificationService : INotificationService
{
    internal const string DefaultAppId = "Windows-MCP";
    private const string RegistrationRoot = @"Software\Classes\AppUserModelId\";
    private const int ElementNotFound = unchecked((int)0x80070490);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly IRegistryService _registry;
    private readonly IToastSink _sink;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // An instance field, not a static: the DI singleton makes it once per process, and a test
    // can prove the registration happens by creating a second instance.
    private bool _defaultRegistered;

    public NotificationService(IRegistryService registry) : this(registry, WinRtToastSink.Instance) { }

    internal NotificationService(IRegistryService registry, IToastSink sink, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _registry = registry;
        _sink = sink;
        _delay = delay ?? Task.Delay;
    }

    public async Task<NotificationResult> ShowAsync(string title, string message, string? appId = null, CancellationToken ct = default)
    {
        appId ??= DefaultAppId;
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("'app_id' must not be blank", nameof(appId));
        ct.ThrowIfCancellationRequested();

        var registered = await EnsureRegisteredAsync(appId, ct);
        var xml = BuildToast(title, message);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _sink.Show(appId, xml);
                return new NotificationResult(true, appId, registered, null);
            }
            catch (COMException ex) when (ex.HResult == ElementNotFound && attempt == 0)
            {
                // The platform has not picked the registration up yet (seen right after the key
                // is written); give it a moment and try once more.
                await _delay(RetryDelay, ct);
            }
            catch (COMException ex) when (ex.HResult == ElementNotFound)
            {
                return new NotificationResult(false, appId, registered,
                    $"Windows dropped the toast: AppUserModelId '{appId}' is not registered with the " +
                    "notification platform (COMException 0x80070490, element not found). An unpackaged app's " +
                    "id needs HKCU\\Software\\Classes\\AppUserModelId\\<id> with a DisplayName value; a " +
                    "packaged app's AUMID (the form 'Package_hash!App') works as-is.");
            }
        }
    }

    /// <summary>
    /// Whether the platform will accept <paramref name="appId"/>: a packaged AUMID (contains
    /// <c>!</c>) always; otherwise when the <c>AppUserModelId</c> key exists under HKCU or HKLM.
    /// The default id is registered under HKCU when it is absent — once per instance.
    /// </summary>
    private async Task<bool> EnsureRegisteredAsync(string appId, CancellationToken ct)
    {
        if (appId.Contains('!'))
            return true;
        var isDefault = appId.Equals(DefaultAppId, StringComparison.OrdinalIgnoreCase);
        if (isDefault && _defaultRegistered)
            return true;

        var path = RegistrationRoot + appId;
        if (await KeyExistsAsync("HKCU", path, ct) || await KeyExistsAsync("HKLM", path, ct))
            return true;
        if (!isDefault)
            return false;

        await _registry.SetAsync("HKCU", path, "DisplayName", DefaultAppId, "String", ct);
        _defaultRegistered = true;
        return true;
    }

    private async Task<bool> KeyExistsAsync(string hive, string path, CancellationToken ct)
    {
        try
        {
            await _registry.ListAsync(hive, path, ct);
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    internal static string BuildToast(string title, string message) =>
        "<toast><visual><binding template=\"ToastGeneric\">" +
        $"<text>{EscapeXml(title)}</text><text>{EscapeXml(message)}</text>" +
        "</binding></visual></toast>";

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
