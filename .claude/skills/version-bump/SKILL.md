---
name: version-bump
description: Bump version in pyproject.toml, rebuild package, and prepare for PyPI upload
disable-model-invocation: true
---

# Version Bump

Bump the project version and prepare for PyPI release.

## Steps

1. Read the current version from `pyproject.toml`
2. Ask the user which bump type: patch (0.0.x), minor (0.x.0), or major (x.0.0)
3. Update the version in `pyproject.toml`
4. Update the version in `CLAUDE.md` if referenced
5. Clean old build artifacts: `rm -rf dist/ build/ *.egg-info`
6. Build the package: `python -m build`
7. Show the user the files in `dist/` and the twine upload command:
   ```
   python -m twine upload --username __token__ --password "$(cat C:/mcp-servers/PyPi_key.txt | tail -1 | cut -d: -f2)" dist/windows_mcp_server-<NEW_VERSION>*
   ```
8. Remind the user to run the upload command manually (requires PyPI token)
9. Do NOT run twine upload automatically
