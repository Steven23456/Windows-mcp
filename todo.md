# Windows-mcp — todo

Cross-session task tracker. Done items kept briefly for context; see `CHANGELOG.md` for the
full record.

## ✅ Recently done

- [x] **`startup_report` + `storage_health` released.** `v0.3.0` (`ecafe9d`) shipped both;
  `v0.3.1` (`3f1e75f`, 2026-06-26) is the storage_health live-fix — temp-`.ps1` MCP path +
  opt-in SMART/physical (`include_usage`). **Both storage_health paths E2E-verified against the
  live server** (fast default never wakes devices; deep path returns real SMART + free space).

## 🟢 Ready / candidates (none blocking)

- [ ] **`startup_report` — scheduled-task COM-handler resolution.** ComHandler tasks (NGEN,
  CertificateServicesClient, …) expose a CLSID, not an exec path; currently reported with no
  action path (and excluded from summary flags). Could resolve the CLSID → handler DLL for
  fuller coverage. Low priority.
- [ ] **`startup_report` — summary severity tiers.** The `summary` flagged list could rank
  untrusted-third-party vs missing-target vs MS-file-missing, instead of a flat list. Nice-to-have.
- [ ] **Dependabot dev-dep advisories** in `tools/*` (JS). Banner 12→4 after `npm audit fix`;
  remaining need major bumps — let Dependabot PRs handle them.

## ⚪ Deliberately out of scope (decisions, not todos)

- `startup_report` skips IE-era sections (BHO / toolbars / IE search scopes / IE MenuExt) —
  obsolete on Win11; they'd add noise, not signal.
- Full `format=json|text|both` reports are large (~110 KB) and spill to a file by design; the
  default `format=summary` is the inline path. Not worth shrinking the full dump.

## 🔴 Known environmental test flakes (NOT code defects — do not "fix" by disabling)

- `UIAutomationServiceTests.GetStateAsync_returns_tree_with_notepad_root` — needs an interactive
  foreground desktop with Notepad; fails headless. (Fixture documents this.)
- `ClipboardServiceTests.SetTextAsync_then_GetTextAsync_roundtrips` — TextCopy `OpenClipboard`
  access-denied when another app holds the clipboard; transient. Gate headlessly with
  `dotnet test --filter "Category!=UIAutomation"` and treat a lone clipboard failure as environmental.
- `ScreenshotServiceTests.CaptureAsync_returns_non_empty_png_with_dimensions` — fails only under
  full-suite contention (no/contended desktop surface during a parallel run); **passes in isolation**
  (`--filter FullyQualifiedName~ScreenshotServiceTests`). Same screen-capture environmental class as
  the UIAutomation tests — not a regression.

## ✅ Done (shipped in v0.3.0 / v0.3.1 — see CHANGELOG)

- `startup_report` MCP tool: HiJackThis-style boot/persistence report, catalog-aware code-signing
  trust, enabled-state decode, file-missing detection — meets/beats HiJackThis on every actionable
  persistence category, plus IFEO / Winlogon / AppInit_DLLs / Active Setup that HJT lacks.
- Coverage expansion + `format=summary` (default, inline) + `includeProcesses`; Control-Panel
  `System32`/`SysWOW64` `*.cpl` scan; per-SID `HKU` Run; DNS; proxy/trusted-zone.
- All e2e-found bugs fixed (catalog `hCatAdmin`, full-path signer resolution, accessibility noise
  filter, ComHandler-task flagging). `npm audit fix` on `tools/*`.
