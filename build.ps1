# Publish Windows-mcp as ONE self-contained exe: <repo>/bundle/WindowsMcp.exe.
# Repo-root entry point; the publish flags live in scripts/build-release.ps1.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts/build-release.ps1')
exit $LASTEXITCODE
