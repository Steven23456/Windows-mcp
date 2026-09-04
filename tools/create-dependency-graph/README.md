# create-dependency-graph

Generates dependency documentation for this repo's C# sources. The tool predates the C# rewrite
and still understands TypeScript projects; `--lang=auto` (the default) picks C# when it finds a
`.csproj` under the root.

## Usage

```bash
cd tools/create-dependency-graph
npm install
npx tsx create-dependency-graph.ts --root=../.. --lang=csharp
```

Flags (`--help` prints the full list):

| Flag | Meaning |
|---|---|
| `--root=<path>` (or a positional path) | Project root to scan. Default: current directory |
| `--lang=auto\|typescript\|csharp` | Language. Default `auto` |
| `--include-tests` / `-t` | Include test files (TypeScript only) |

`npm run build` compiles to `dist/` and packages a standalone `create-dependency-graph.exe`
(`@yao-pkg/pkg`, node22-win-x64); `npm start` runs the compiled build.

## Output

Written to `docs/architecture/` under the scanned root:

| File | Contents |
|---|---|
| `DEPENDENCY_GRAPH.md` | Per-file imports/exports by module (entry, services, tools, abstractions, models), dependency matrix, circular-dependency analysis, Mermaid graph, summary statistics |
| `dependency-graph.json` / `dependency-graph.yaml` | Machine-readable graph (YAML is the compact form) |
| `dependency-summary.compact.json` | Counts only |
| `unused-analysis.md` | Files and exports nothing imports |

These outputs are **generated and not committed**. Regenerate on demand when you need a
snapshot; a stale copy misreports counts (the July 2026 snapshot that used to live in
`docs/architecture/` listed 33 services and labelled C# `using` directives as "Node.js built-in
dependencies"). If you do regenerate, keep the results out of the tree or add them to
`.gitignore`.

## Notes

- C# mode categorizes files by folder: `Program.cs` + `Startup/` → entry, `Services/`,
  `Tools/`, `WindowsMcp.Abstractions/I*.cs` → abstractions, `Models/` → models.
- Dependabot tracks this tool's JS dependencies (`.github/dependabot.yml`).
