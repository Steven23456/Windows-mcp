# Windows-mcp — todo

Cross-session task tracker. Done items kept briefly for context; see `CHANGELOG.md` for the
full record.

## 🟢 Ready / candidates (none blocking)

- [ ] **Cut a release for the `startup_report` work.** `CHANGELOG.md [Unreleased]` holds a
  substantial, complete, tested feature (the whole `startup_report` tool + expansion). Ready to
  rename to `[0.3.0]` + tag `v0.3.0` whenever desired (feature → minor bump).
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

## ✅ Done (this work — see CHANGELOG [Unreleased])

- `startup_report` MCP tool: HiJackThis-style boot/persistence report, catalog-aware code-signing
  trust, enabled-state decode, file-missing detection — meets/beats HiJackThis on every actionable
  persistence category, plus IFEO / Winlogon / AppInit_DLLs / Active Setup that HJT lacks.
- Coverage expansion + `format=summary` (default, inline) + `includeProcesses`; Control-Panel
  `System32`/`SysWOW64` `*.cpl` scan; per-SID `HKU` Run; DNS; proxy/trusted-zone.
- All e2e-found bugs fixed (catalog `hCatAdmin`, full-path signer resolution, accessibility noise
  filter, ComHandler-task flagging). `npm audit fix` on `tools/*`.
