---
name: version-bump
description: Bump the Windows-mcp version (Directory.Build.props + plugin.json), roll CHANGELOG [Unreleased] into a release section, build/test, publish the single-file exe, and prepare the release commit and tag.
disable-model-invocation: true
---

# Version Bump

Cut a release of the C# server. The version lives in **two** places that must match —
`ServerInfoTests` fails when they drift.

## Steps

1. Read the current version from `<Version>` in `Directory.Build.props` and confirm
   `.claude-plugin/plugin.json` `version` matches it.
2. Ask the user which bump: patch (0.0.x), minor (0.x.0), or major (x.0.0). Compute the new
   version.
3. Update `<Version>` in `Directory.Build.props` and `version` in `.claude-plugin/plugin.json`.
4. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [<new>] - <today, YYYY-MM-DD>` and insert
   a fresh empty `## [Unreleased]` above it. Do not rewrite the entries.
5. Fix any count that changed in this release: the tool count in `README.md` ("Tool
   reference"), `CLAUDE.md` (Overview), `docs/architecture/*`, and the tool list in
   `skills/windows/SKILL.md` if tools were added or removed.
6. Build and run the headless-safe suite:
   `dotnet build` then `dotnet test --filter "Category!=UIAutomation"`
   (a lone `ClipboardServiceTests` failure is environmental — see `CLAUDE.md`).
7. Publish the single-file exe: `.\scripts\build-release.ps1` (publishes `bundle/WindowsMcp.exe`
   with the native libraries embedded and strips the stray `libSkiaSharp.pdb`), then confirm
   `bundle/WindowsMcp.exe --help` runs. The MCP `serverInfo` version is derived from
   `<Version>`, so the passing `ServerInfoTests` in step 6 is the version check.
8. Show the user `git status` and `git diff --stat`, plus the proposed commit message
   `release: v<new>` and tag `v<new>`.
9. Do NOT commit, tag, or push automatically — the user runs those.
10. Do NOT commit the exe. `bundle/` is gitignored — binaries never enter the repo; distribution
    (a release asset or a remote host) is a separate step. See `CLAUDE.md` "Testing a change
    against the LIVE MCP server" for how a running server picks up a new build.
