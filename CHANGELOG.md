# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased (0.5.0)]

### Added
- **Standalone `.cs` file support** — any tool's `solutionPath` now also accepts a single `.cs` file (a .NET 10 "file-based program" you'd launch with `dotnet run file.cs`), with or without `#:` directives (`#:package`, `#:project`, `#:sdk`, `#:property`). The SDK itself turns the file into a project: the server calls `dotnet run-api` (the same mechanism Roslyn's own language server uses) to get the exact virtual project, materializes it to a temporary `.csproj`, and loads it through the normal MSBuildWorkspace pipeline. A standalone file therefore behaves like a real project — it gets the SDK's **default analyzers and source generators** (so `[GeneratedRegex]` and friends work even with no directives), `#:package` references resolve, `#:project` references load as **source** projects (go-to-definition / find-references cross into them), and diagnostics match `dotnet build`. Editing a file's `#:` directives rebuilds the project; editing only its code updates incrementally. The temporary project is written outside your source tree (`#:project` paths are rewritten to absolute) yet still inherits the entry file's repo-level `Directory.Build.props`/`Directory.Build.targets` (via `DirectoryBuildPropsPath`, the way the language server inherits them through its in-place virtual project) so diagnostics honor repo MSBuild settings like `NoWarn` — while build output stays in a temp directory, never your repo. If the SDK can't produce a project (e.g. `dotnet run-api` unavailable) the server falls back to a framework-only in-memory workspace so the file still loads.
- **Workspace caching** — a loaded solution is now cached (default 20-minute idle TTL) and reused across tool calls instead of paying a full MSBuild load every time. Edits to tracked files are applied incrementally as in-place document text updates; project-file and Razor changes still trigger a reload.
- **Incremental document reconciliation** — newly added, removed, or renamed `.cs` files are picked up by re-evaluating just the affected project with MSBuild and applying the difference, instead of reloading the whole solution. MSBuild — not a path guess — decides which files belong to the project (globs, `<Compile Remove>`, links).
- **Analyzer & source-generator shadow-copy loader** — analyzer/generator assemblies load from a private per-process temp copy (the way Visual Studio does), so a concurrent `dotnet build` is no longer blocked by build output the server holds open (MSB3026/MSB3027/MSB3021). Each analyzer directory gets its own isolated loader, dependencies resolve to the highest matching version, and stale copies are cleaned up during the session. Opt out with `ROSLYNMCP_DISABLE_ANALYZER_SHADOW_COPY=1`.
- **HTTP transport mode** — the server can run over HTTP in addition to stdio.
- **Razor / Blazor diagnostics** — `get_diagnostics` now accepts `.razor` and `.cshtml` files and reports RZ diagnostics against the source file (not the generated `.g.cs`). When the Razor generator can't be loaded it reports an `RMCP0001` info diagnostic, and a generator that throws at runtime reports `RMCP0002`, so an unexpectedly empty result is explained instead of silent.
- **Symbol-search candidates on build errors** — symbol search responds with possible matching candidates when the project has build errors instead of failing outright.
- **Wrong-column recovery** — when a supplied `line`/`column` lands on the wrong identifier (e.g. `TimeSpan` instead of `FromSeconds` in `TimeSpan.FromSeconds`) but a `symbolName` was given, the tool scans the line for a unique match, retries there, and reports the corrected location.
- **Claude Code plugin definition** plus a `/setup` slash command that guides installation of the `roslyn-mcp` .NET global tool after the plugin is cloned.
- Local install scripts [install-local.ps1](./install-local.ps1) for the `roslyn-mcp` dev tool and `roslyn-cli`.

### Changed
- Upgraded to **.NET 10** and **xUnit v3**.
- Workspace load now tolerates per-project failures the way the VS IDE does — NuGet restore problems (e.g. NU1903/NU1904 advisories escalated to errors) no longer abort the whole load; it fails only when no project loads at all.
- External file changes detected by the watcher now wait for any in-flight tool call to finish before updating the solution, so a background edit can't change the code mid-operation.
- Default idle cache TTL raised from 5 to 20 minutes to cover typical "step away" gaps without a cold reload.
- The package version is centralized in `src/Directory.Build.props` (now `0.5.0`); `install-local.ps1` reads that prefix and appends a `-local` suffix, so dev builds (`0.5.0-local`) stay distinguishable from released packages.
- A project is now fully compiled at load time so the reported `workspaceLoadMs` reflects the real cold-load cost rather than deferring it to the first query.

### Fixed
- `rename_symbol` now finds fields, events, and locals when a `line` is given without a `column`. It previously failed with "symbol not found" in that case, because the default column landed on the leading keyword (e.g. `private`) instead of the name. It now uses the same line-scanning resolution as `find_references` and `go_to_definition`.
- The server's own refactor writes (and genuine external edits shortly after) no longer trigger a full MSBuild reload. File-watcher `.cs` events are reconciled against settled disk + workspace state rather than blindly invalidating the cache.
- Cached workspace now reloads when a referenced generator assembly changes on disk, so stale source-generator output is no longer served after a `dotnet build`.
- Analyzer-reference rewrites (shadow-copy and unresolved-reference stripping) are kept in-memory and never persisted to the `.csproj` — previously every solution load rewrote project files with absolute `<Analyzer Include="…"/>` paths and mangled formatting (BOM, dropped blank lines/trailing newline).
- `get_diagnostics` no longer reports spurious Razor errors (RZ3600/RZ9985/RZ10009): the generator now receives the project's MSBuild properties (e.g. `RootNamespace`, `RazorLangVersion`) and leftover generator output is removed before re-running, so results match `dotnet build`.
- Analyzers that MSBuild reports but the host can't load (missing DLL, target-framework mismatch) are dropped after load, fixing a `4003` error on the first tool call after an otherwise successful load.
- Cache-invalidation race (a concurrent reload could evict a freshly loaded entry), a leak that kept the old workspace and analyzer loader alive when the cache was invalidated while a tool call was still using it, and analyzer-loader locking/GC hardening.
- Responses now report timing broken out into the operation's own time (`executionTimeMs`), the workspace load time (`workspaceLoadMs`), and the combined total (`totalExecutionTimeMs`); previously the load time was lost on the way back.

## [0.4.0] - 2026-02-23

### Added
- `.slnx` (Visual Studio 2022+ XML solution format) support in workspace loading and CLI
- Codex CLI compatibility via JSON-RPC 2.0 notification handling (`notifications/initialized`)
- `TestSolution.slnx` test fixture

### Changed
- Roslyn upgraded from 4.8.0 to 5.0.0 (`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`)
- Workspace failure handler migrated to `RegisterWorkspaceFailedHandler` API (Roslyn 5.0)
- xunit upgraded from 2.6.0 to 2.9.3 across all test projects
- Microsoft.NET.Test.Sdk upgraded from 17.8.0 to 18.0.1 across all test projects
- coverlet.collector upgraded from 6.0.0 to 6.0.4 across all test projects
- GitHub Actions: checkout v4 to v6, setup-dotnet v4 to v5, upload-artifact v4 to v6
- 566 total tests (270 Core + 206 Server + 90 Cli)

### Fixed
- RS1039 analyzer warning from Roslyn 5.0 upgrade (suppressed in SymbolKindMapperTests)
- Import ordering in Cli.Tests to pass format check

## [0.3.1] - 2026-02-09

### Fixed
- Republish all packages — NuGet 0.3.0 was immutable from a prior incomplete release

## [0.3.0] - 2026-02-09

### Added
- 22 new tools (41 total), organized across five categories:

  **Code Navigation (5 tools)**
  - `find_references` — find all references to a symbol across the solution
  - `go_to_definition` — navigate to the source definition of a symbol
  - `get_symbol_info` — retrieve detailed metadata for any symbol (type, accessibility, modifiers, members, docs)
  - `find_implementations` — find all implementations of an interface or abstract member
  - `search_symbols` — search for symbols by name pattern across the workspace

  **Analysis & Metrics (6 tools)**
  - `get_diagnostics` — retrieve compiler diagnostics filtered by severity and file
  - `get_code_metrics` — calculate cyclomatic complexity, lines of code, maintainability index, class coupling, and depth of inheritance
  - `analyze_control_flow` — analyze control flow for a code region (reachability, return/exit points)
  - `analyze_data_flow` — analyze data flow for a code region (reads, writes, captured variables)
  - `get_document_outline` — get a hierarchical outline of all symbols in a file
  - `get_type_hierarchy` — retrieve base types and derived classes for a type

  **Code Generation & Formatting (4 tools)**
  - `generate_equals_hashcode` — generate Equals() and GetHashCode() overrides for a type
  - `generate_tostring` — generate ToString() override for a type
  - `format_document` — format a C# file using Roslyn's built-in formatter
  - `add_null_checks` — add null-check statements for method parameters

  **Code Conversions (7 tools)**
  - `convert_expression_body` — toggle between expression body and block body for methods/properties
  - `convert_property` — convert between auto-property and full property with backing field
  - `introduce_parameter` — promote a local variable to a method parameter, updating call sites
  - `convert_foreach_linq` — convert foreach loops with Add patterns to LINQ expressions
  - `convert_to_pattern_matching` — convert if/is chains and switch statements to switch expressions
  - `convert_to_interpolated_string` — convert string.Format() and concatenation to interpolated strings
  - `find_callers` — find all callers of a symbol across the solution

- `roslyn-cli` standalone CLI tool (`RoslynMcp.Cli` package)
  - All 41 Roslyn tools accessible from the command line without an AI assistant
  - JSON output by default (pipeable to `jq`), with `--format text` for human-readable output
  - Per-tool help via `roslyn-cli <tool-name> --help`
  - Exit codes: 0=success, 1=tool error, 2=CLI error, 3=environment error

- `QueryOperationBase<TParams, TResult>` — new base class for read-only query operations
- `SymbolResolver` — general-purpose symbol resolver (position-based and name-based)
- New contract models, error codes, and enums for all 22 new tools
- New shared utilities: `MetricsCalculator`, `EqualityMemberCollector`, `NullCheckGenerator`

### Fixed
- Null-ref in `GetCodeMetrics` when metrics are unavailable
- Null-unsafe `GetHashCode` generation for types with >8 members
- Redundant allocation in `GetDiagnostics`
- `PascalToKebab` producing `x-m-l-path` instead of `xml-path` for acronyms
- `IsRequired` not detecting required value-type properties
- `IsHelpFlag` case sensitivity inconsistent with other CLI flag parsing
- `IsEnvironmentError` using fragile message-based detection instead of exception types

### Changed
- 557 total tests (269 Core + 206 Server + 82 CLI)

## [0.2.1] - 2026-02-06

### Added
- `sort_usings` tool -- sort using directives alphabetically in a C# file (19th tool)
- `allFiles` parameter for `add_missing_usings` and `remove_unused_usings` -- process every C# file in the solution with a single call

### Changed
- Server now reports its actual assembly version instead of a hardcoded value
- README updated to document all 19 tools

## [0.2.0] - 2026-02-06

### Added
- 10 new refactoring operations (18 total): extract variable, extract constant, extract interface, extract base class, inline variable, change signature, encapsulate field, generate overrides, implement interface, convert to async
- File-based logging for troubleshooting (`%TEMP%/roslyn-mcp/` or `/tmp/roslyn-mcp/`)
- JSON-RPC error responses and structured logging
- Unit tests for StdioTransport
- Pinned .NET SDK version via `global.json`

### Fixed
- Blocking async calls that could cause deadlocks in MoveTypeToFile and MoveTypeToNamespace
- MSBuildWorkspaceProvider reliability with better error handling
- CI workflow branch triggers (now correctly target `master`)
- NuGet push wildcard handling on Windows in CI

### Removed
- Broken integration tests with MSBuild assembly conflicts

## [0.1.0] - 2026-01-30

### Added
- Initial public release
- 8 Roslyn-powered C# refactoring operations
- Cross-platform .NET global tool (`roslyn-mcp`)
- MCP protocol support for Claude Code and Claude Desktop

[0.4.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/JoshuaRamirez/RoslynMcpServer/releases/tag/v0.1.0
