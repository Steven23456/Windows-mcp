# C-4 — Notification `app_id`, in-process toasts

**Checklist item:** [C-4](../upstream-parity-checklist.md#c-4--notification-app_id-aumid--p3--s) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 1, last item — decision R7 (in-process WinRT, the
default AUMID registered under HKCU once) settled in section 7; spike result recorded there ·
**Status:** implemented 2026-09-06 (build clean, headless suite green, a real toast shown by
the integration test — see CHANGELOG [Unreleased]) ·
**Effort:** ~2 h including the spike and the RED/GREEN passes.

## Problem

`NotificationService` runs a PowerShell script (a cold start and the serialization gate for a
toast) with the AppUserModelId hard-coded to `Windows-MCP`. Windows uses the AUMID as the
toast's identity; an id the platform does not know is dropped with `0x80070490`. Upstream
makes `app_id` a parameter.

## Decision

- **In-process.** `Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(appId)
  .Show(new ToastNotification(xml))` through the `net10.0-windows10.0.19041.0` projection,
  behind an internal `IToastSink.Show(appId, toastXml)` seam so the service is unit-tested with a
  fake. The XML payload and its escaping are unchanged. The service no longer depends on
  `IPowerShellService`; it depends on `IRegistryService`.
- **`notification(title, message, app_id = "Windows-MCP")`** → `{shown, appId, registered,
  note?}`. A blank `app_id` is an `ArgumentException`.
- **Registration, for the default id only.** Before the first show in the process, the service
  checks `HKCU\Software\Classes\AppUserModelId\Windows-MCP` (through `IRegistryService.ListAsync`,
  C-2) and, when absent, writes `DisplayName = "Windows-MCP"` there (`SetAsync`, which creates
  the key). Once per process. A caller-supplied id is never written.
- **`registered`** is what the platform will accept: `true` for a packaged AUMID (contains
  `!`), or when the `AppUserModelId\<id>` key exists under HKCU or HKLM; `false` otherwise. It
  is reported, not enforced — the show is attempted anyway.
- **The `0x80070490` retry.** The spike showed the first call right after registration can
  fail before the platform has picked the key up. On a `COMException` with that HResult the
  service waits (1 s, injectable) and retries once; if it fails again the result is
  `shown:false` with a `note` naming the id, the HResult and the registration requirement.
  Any other exception propagates.

## Changes

- `Abstractions/INotificationService.cs` — `ShowAsync(title, message, appId, ct)` returning
  `NotificationResult(Shown, AppId, Registered, Note)` (new record in a new
  `Models/NotificationDtos.cs`).
- `Services/NotificationService.cs` — rewritten; `Services/IToastSink.cs` and
  `Services/WinRtToastSink.cs` (new).
- `Tools/SystemTools.cs` — the parameter and the JSON result.
- `Hosting/WindowsMcpHost.cs` — unchanged registration line; DI resolves the new constructor.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | The default id registers `DisplayName` once across two calls when the key is absent, and not at all when present; a custom id never writes; `registered` from the key (HKCU, HKLM) and from `!`; a blank id refused | `NotificationServiceTests` | Unit |
| R2 | The sink receives the escaped title and message in a `ToastGeneric` payload with the given id; `0x80070490` once → retried after the injected delay and `shown:true`; twice → `shown:false` with the note; another HResult propagates; the old PowerShell dependency is gone (the constructor takes `IRegistryService`) | `NotificationServiceTests` | Unit |
| R3 | The tool's default `app_id`, the JSON result, the refusal | `SystemToolsTests` | Unit |
| R4 | A real toast with the default id: `shown:true`, no `powershell.exe` spawned (it is visible on the desktop — documented) | `NotificationServiceTests` | Integration |
| R5 | Annotations per C-7; the schema over HTTP | `ToolInventoryTests`, `HttpTransportTests` | Unit / Integration |

## Deviations and follow-ups

- **Spike (2026-09-06, build 28000):** an unregistered id fails at the first property read or
  `Show` with `COMException 0x80070490`; a packaged AUMID works as-is; the HKCU key makes the
  default id work after a lag of about a second on the first call, and the platform remembers
  the id even after the key is removed. The retry-once rule and the `shown:false` note come from
  that lag.
- Once the service has written the default id's key in this process, `registered` is reported
  `true` from memory rather than re-read — the write is the registration.
- `registered:true` for a packaged AUMID is inferred from the `!`, not verified against the
  package catalog (B-8's `IAppCatalogService` could do that; not worth the dependency).
