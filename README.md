# Roslyn MCP Server

[![Build and Test](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/build.yml)
[![Code Quality](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml/badge.svg)](https://github.com/JoshuaRamirez/RoslynMcpServer/actions/workflows/quality.yml)
[![NuGet](https://img.shields.io/nuget/v/RoslynMcp.Server.svg)](https://www.nuget.org/packages/RoslynMcp.Server)
[![NuGet Downloads](https://img.shields.io/nuget/dt/RoslynMcp.Server.svg)](https://www.nuget.org/packages/RoslynMcp.Server)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Let AI assistants like Claude safely refactor your C# codebase using the same Roslyn compiler platform that powers Visual Studio.

Roslyn MCP Server is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that exposes **47 Roslyn-powered tools** to AI assistants and other MCP clients. It combines 24 refactoring operations, 5 code navigation tools, 7 analysis and metrics tools, 4 code generation tools, and 7 code conversion tools -- giving your AI deep code intelligence, comprehensive refactoring, and modern C# syntax transformations with full solution-wide reference tracking and preview support.

---

## Table of Contents

- [Why RoslynMcpServer?](#why-roslynmcpserver)
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Standalone CLI](#standalone-cli)
- [Configuration](#configuration)
- [Available Tools](#available-tools)
- [Preview Mode](#preview-mode)
- [File Encodings and Line Endings](#file-encodings-and-line-endings)
- [Analyzer Shadow Copying](#analyzer-shadow-copying)
- [Troubleshooting](#troubleshooting)
- [NuGet Libraries](#nuget-libraries)
- [Contributing](#contributing)
- [License](#license)

---

## Why RoslynMcpServer?

- **47 tools** -- refactoring, navigation, analysis, generation, and conversion tools, the most comprehensive Roslyn MCP server available
- **Preview mode on every operation** -- see exactly what will change before applying
- **Atomic file writes with rollback** -- if any file write fails, all changes are reverted
- **Solution-wide reference updates** -- renames and moves propagate across your entire solution
- **Single command install** -- `dotnet tool install -g RoslynMcp.Server`, no repo cloning needed
- **Cross-platform** -- works on Windows, Linux, and macOS
- **Build while it's connected** -- analyzers and source generators are [shadow-copied](#analyzer-shadow-copying), so a concurrent `dotnet build` never fails on a locked DLL

---

## Prerequisites

Before installing, make sure you have:

1. **.NET 11 SDK or later** -- [Download here](https://dotnet.microsoft.com/download/dotnet/11.0)
2. **A C# solution (`.sln` or `.slnx`) or project (`.csproj`) to work with**

Verify your .NET SDK version:

```bash
dotnet --version
```

The output should be `11.0.x` or higher.

---

## Quick Start

### 1. Install

```bash
dotnet tool install -g RoslynMcp.Server
roslyn-mcp --version   # prints the installed version, e.g. 0.6.0
```

### 2. Configure

Create a `.mcp.json` file in your project root (for Claude Code):

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "type": "stdio",
      "command": "roslyn-mcp",
      "args": []
    }
  }
}
```

Then restart Claude Code or run `/mcp` to connect.

### 3. Verify

Ask Claude:

> "Run the roslyn diagnose tool for my solution at C:/path/to/MySolution.sln"

You should see a health report with Roslyn version, MSBuild status, and workspace details.

### 4. Try It

Ask Claude:

> "Rename the class UserService to AccountService in C:/path/to/MySolution.sln"

Claude will use the `rename_symbol` tool to rename the class and update every reference across your entire solution.

---

## Standalone CLI

All 47 tools are also available as a standalone CLI for use in scripts, CI/CD pipelines, and terminals without an AI assistant.

### Install

```bash
dotnet tool install -g RoslynMcp.Cli
```

### Usage

```bash
roslyn-cli <solution-path> <tool-name> [--option value ...]
roslyn-cli <tool-name> --help
roslyn-cli --help
roslyn-cli --version
```

### Examples

```bash
# Check environment health
roslyn-cli C:/path/to/MySolution.sln diagnose --format text

# Rename a symbol across the entire solution
roslyn-cli C:/path/to/MySolution.sln rename-symbol --source-file C:/path/to/Foo.cs --symbol-name Bar --new-name Baz

# Get compiler diagnostics (errors only), pipe to jq
roslyn-cli C:/path/to/MySolution.sln get-diagnostics --severity-filter Error | jq '.data'

# Preview a refactoring without applying
roslyn-cli C:/path/to/MySolution.sln extract-method --source-file Foo.cs --start-line 10 --end-line 20 --method-name DoWork --preview
```

Output is JSON by default (pipeable to `jq`). Use `--format text` for human-readable output. Exit codes: 0=success, 1=tool error, 2=CLI error, 3=environment error.

`roslyn-cli <tool-name> --help` lists a tool's options. Paths may be relative to the current directory: the solution path and every file or project option (`--source-file`, `--target-file`, `--reference-file`, `--caller-file`, `--call-file`, `--project-path`, `--solution-path`). A value is converted by the option's type: a text option stays text (`--query 42`, `--query true`), an integer or boolean option must be one (`--line abc` and `--preview yes` are errors), and a list option takes a JSON array (`--members '["A","B"]'`). An option the tool does not take (`--max-result`, or a key inside a JSON value such as `--parameters '[{"orignalName": ...}]'`) is an error (exit code 2) that names it, suggests the closest valid one and lists the valid options; it used to be ignored silently.

---

## Configuration

### Claude Code

Create `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "type": "stdio",
      "command": "roslyn-mcp",
      "args": []
    }
  }
}
```

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "roslyn-refactor": {
      "command": "roslyn-mcp",
      "args": []
    }
  }
}
```

Config file locations:

| OS      | Path                                                        |
|---------|-------------------------------------------------------------|
| Windows | `%APPDATA%\Claude\claude_desktop_config.json`               |
| macOS   | `~/Library/Application Support/Claude/claude_desktop_config.json` |

### Environment Variables

| Variable | Effect |
|----------|--------|
| `ROSLYNMCP_DISABLE_ANALYZER_SHADOW_COPY` | Set to `1` to disable [analyzer shadow copying](#analyzer-shadow-copying) and load analyzers in place. Rarely needed. |

---

## Available Tools

All tools accept a `solutionPath` parameter (absolute path to a `.sln`, `.slnx`, or `.csproj` file). You can also point it at a single standalone `.cs` file — for example a .NET 10 [file-based program](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/sdk#file-based-apps) you'd run with `dotnet run file.cs`. The SDK turns it into a project (via `dotnet run-api`) which is loaded through MSBuildWorkspace, so it behaves like a real project: the SDK's default analyzers and source generators run (`[GeneratedRegex]` works even with no directives), and `#:package`/`#:project`/`#:sdk`/`#:property` directives resolve — with `#:project` references navigable as source, and the repo's full MSBuild context (`Directory.Build.props`, `global.json`, `NuGet.config`, …) honored exactly as `dotnet run` does. The transient `<file>.cs.csproj` is created next to the file only while it loads (git-ignored via `.git/info/exclude`, then deleted). The first load builds/restores once (then it's cached); a .NET 10 or later SDK is required for directive resolution. Refactoring tools also accept a `preview` parameter (set to `true` to see changes without applying them).

Every call's arguments are checked against the tool's input schema before the tool runs. A key the tool does not take — misspelled, or in another case (argument names are case-sensitive camelCase: `maxResults`, not `MaxResults`), also inside `change_signature`'s `parameters` entries — fails the call with a tool error (code `1018`, `UnknownParameter`) that names each such key, suggests the key it probably means, and lists the valid ones. Such keys used to be dropped silently, so the option took its default. The values are checked too: a value of the wrong type (`"line": "ten"`, `1.5` or `3000000000` for an integer, `"yes"` for a boolean), one outside the schema's `enum` (compared ignoring case) or below its `minimum` fails with code `1017` naming the key, the expected type and the value given, also inside `parameters` entries. An argument that is `null` counts as left out. Paths (`solutionPath`, `sourceFile`, `targetFile`, `projectPath`, ...) must be absolute: the server does not know the client's current directory, so a relative one fails the call before anything loads (code `1001`, `1002` for `targetFile`), saying so.

### Move and Rename

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `move_type_to_file` | Move a C# type declaration to a different file. Updates all references automatically. | `sourceFile`, `symbolName`, `targetFile`, `createTargetFile` |
| `move_type_to_namespace` | Change the namespace of a C# type. Updates all using directives and qualified references. The type must be alone in its namespace declaration (otherwise refused with 3130: run `move_type_to_file` first). | `sourceFile`, `symbolName`, `targetNamespace`, `updateFileLocation` |
| `rename_symbol` | Rename any C# symbol (type, method, property, field, variable, etc.) with automatic reference updates across the solution. Interface implementations and overrides of a renamed member are always renamed with it. | `sourceFile`, `symbolName`, `newName`, `line`, `column`, `renameOverloads`, `renameFile` |
| `rename_namespace` | Rename a namespace declared in source, with its sub-namespaces, across the solution; the depth may change (`App.Old` to `Company.App.New`). Rewrites block and file-scoped declarations (nested and dotted ones), `using` / `global using` / `using static` / alias directives, qualified and `global::` names and crefs, but not comments or strings; names inside a renamed declaration that relied on its old surroundings are qualified, and `using` directives the rename makes duplicates (or that import a parent namespace it empties) are removed. In `.razor` / `.cshtml` files (`_Imports.razor` included) the `@using` and `@namespace` directives are rewritten; other mentions there (C# in `@code`, `@inherits`, markup) are listed in `warnings` by line, with the errors the Razor generator's code gets. Refused when a type would collide with an existing one or an enclosing namespace block that also declares other things would have to change. Generated files and project-level `<Using>` / `<RootNamespace>` are left alone and reported in `warnings` with the compiler errors that leaves. `updateFolders` moves the files that sit in the folder of their old namespace into the folder of the new one where the project's default globbing makes that safe, confirmed by MSBuild; other files stay, with a warning. | `oldNamespace`, `newNamespace`, `updateFolders` |

### Extract

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `extract_method` | Extract the selected statements or expression into a new method. Variables the selection reads become parameters; variables it assigns that the code after it reads are returned (a tuple for several). An instance method when the selection uses instance members, otherwise static; async when it awaits. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `methodName`, `visibility` |
| `extract_variable` | Extract an expression to a local variable. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `variableName`, `useVar` |
| `extract_constant` | Extract a literal value to a named constant. | `sourceFile`, `startLine`, `startColumn`, `endLine`, `endColumn`, `constantName`, `visibility`, `replaceAll` |
| `extract_interface` | Extract an interface from a class's public members. | `sourceFile`, `typeName`, `interfaceName`, `members`, `targetFile` |
| `extract_base_class` | Extract members to a new base class. Members are picked by name like `pull_members_up` picks them (every overload, field-like events, `this` for indexers, one variable of `int a, b;`); private members become protected, `private protected` ones stay. Refused when a member uses members that would stay behind (they are listed). | `sourceFile`, `typeName`, `baseClassName`, `members`, `targetFile`, `makeAbstract` |

### Hierarchy

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `pull_members_up` | Move fields, methods (every overload), properties, events and indexers (`this`) of a type into its direct base class, or add their signatures to an interface it implements (`targetType`). Declarations move with their doc comments and attributes; a private member becomes protected (`private protected` stays, or becomes protected when the base class is in another assembly), and types using the type's own type parameters are written in the base class's (`Derived<U> : Base<U>`). With `makeAbstract`, methods, properties, indexers and events are declared abstract in the base class, which is made abstract if nothing instantiates it, and stay in the type as overrides. Into an interface, public instance members add their signature (public accessors only) and keep their implementation. Refused when a member uses members that stay behind (listed in the error), when the target already declares the name or signature, for an override of the base class's own member, for `base.` calls whose meaning would change, and for types the target's project can't see. Adds the usings the moved code needs to the target's file; accessibility notes, other derived types that now hide a member, and compiler errors left (e.g. implementers lacking the new member) are returned as `warnings`. | `sourceFile`, `typeName`, `members`, `targetType`, `makeAbstract`, `line` |

### Inline

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `inline_variable` | Inline a local variable by replacing all usages with its initializer value. | `sourceFile`, `variableName`, `line` |
| `inline_method` | Replace calls to a method with its body: every call, or one picked by `callLine` (`callFile`, `callColumn`), then delete the method when nothing references it any more (`deleteMethod`, default `true` when inlining every call and `false` for one call). A body that is an expression or a single `return` inlines into any expression; a `void` method's statements replace calls that are statements of their own. Arguments are substituted (parenthesized and cast where needed, defaults filled in, type arguments substituted, the receiver replacing `this`), or stored in locals first, in their original order, when moving, repeating or dropping them could change what happens; body locals that would clash are renamed. Calls that can't take those locals (expression-bodied members, initializers, conditional evaluation), whose body uses members not accessible there, or where a name would bind to something else are left and listed in `warnings`, with any compiler errors. Refuses recursive, virtual, abstract, override, interface, partial, extern, async, iterator and unsafe methods, `ref`/`out`/`in` parameters, and other block bodies. | `sourceFile`, `methodName`, `line`, `callFile`, `callLine`, `callColumn`, `deleteMethod` |

### Delete

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `safe_delete` | Delete a type or member only when nothing outside its own declaration references it; otherwise fail and list the references (`nameof` and doc-comment `cref`s count, crefs marked as such). Removes every partial part, one variable of `int a, b;`, the comments right above the declaration, the `using` directives only it used (those unused before stay), and the file when a type was all it declared. Refuses overrides, interface implementations, members that are overridden or implemented, and symbols used implicitly (entry point, static constructors, finalizers, implicit conversions). Compiler errors left behind are returned as `warnings`. | `sourceFile`, `symbolName`, `line`, `column` |

### Signature and Encapsulation

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `change_signature` | Add, remove, rename, retype or reorder method parameters and update all call sites, keeping each call's argument order. `parameters` lists only the changes: a parameter it does not list stays, in its relative order. An entry with `originalName` changes that parameter — `name` renames it, `type` retypes it, `defaultValue` sets its default (`""` removes it, and calls that left the argument out then pass the old default value; left out, the default stays), `newPosition` moves it (0-based index in the new list), `remove: true` removes it — and an entry without `originalName` adds a parameter (`name` and `type` required) at `newPosition` or at the end. A new parameter with a default value is left out of existing calls (later positional arguments are then passed by name); one without gets a `default /* TODO */` placeholder. Refused: an unknown `originalName`, a parameter listed twice, two parameters with one name, positions outside the new list or taken twice, a required parameter after optional ones, and a rename to a name the body already uses (a local, a lambda or local function parameter, a range or pattern variable, or a member it reads by simple name). Applies to the whole override / implementation family (base and interface members, overrides, implementations), matching parameters by position. Compiler errors left behind (a method group converted to a delegate, a removed parameter the body still uses) and the TODO placeholders are returned as `warnings`. | `sourceFile`, `methodName`, `parameters` (array of changes), `line` |
| `change_return_type` | Change the return type of a method, or the type of a property or indexer (`memberName` `this`), on the whole override / implementation family. Only declarations change; the compiler errors this causes (return statements, callers) are returned as `warnings`. | `sourceFile`, `memberName`, `newType`, `line` |
| `encapsulate_field` | Encapsulate a field in a property: the field becomes private (renamed to _name when only the case would tell it from the property), code outside its type uses the property, code inside the type keeps using the field. Uses that need the field itself (ref, out, address-of) are listed in warnings. | `sourceFile`, `fieldName`, `propertyName`, `readOnly` |

### Generate

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `generate_constructor` | Generate a constructor that initializes fields and/or properties of a type. | `sourceFile`, `typeName`, `members`, `addNullChecks` |
| `generate_overrides` | Generate override methods for base class virtual/abstract members. | `sourceFile`, `typeName`, `members`, `callBase` |
| `implement_interface` | Generate interface member implementations for a type. | `sourceFile`, `typeName`, `interfaceName`, `explicitImplementation`, `members` |

### Convert

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `convert_to_async` | Convert a synchronous method to async: rename it to `<name>Async` with every reference (`renameToAsync`, default `true`), add `async` and return `Task` / `Task<T>`. In its body, `.Result`, `.Wait()` and `.GetAwaiter().GetResult()` on a task become `await`, and a call whose task was discarded is awaited; lambdas and local functions are left alone. Async callers await the call; the other callers are left as they are and listed in `warnings`, with the compiler errors this causes. Refuses async, iterator, abstract, virtual, override, interface-implementing, partial and extern methods, `ref`/`out`/`in` parameters, `ref` returns, methods that already return a task and methods with nothing to await. | `sourceFile`, `methodName`, `line`, `renameToAsync` |
| `convert_expression_body` | Toggle between expression body (`=> expr;`) and block body (`{ return expr; }`). | `sourceFile`, `direction`, `memberName`, `line` |
| `convert_property` | Convert a C# property between auto-property and full property with backing field. To an auto-property only when the accessors just read and write a field of the type: the field is removed and its other uses go through the property. | `sourceFile`, `direction`, `propertyName`, `line` |
| `convert_foreach_linq` | Convert foreach loops with Add patterns to LINQ Select/Where expressions. | `sourceFile`, `line` |
| `convert_to_pattern_matching` | Convert if/is chains and switch statements to switch expressions. | `sourceFile`, `line` |
| `convert_to_interpolated_string` | Convert string.Format() calls and concatenation to interpolated strings. | `sourceFile`, `line` |
| `introduce_parameter` | Promote a local variable to a method parameter. Every call, in any project, passes the variable's initializer with the call's own arguments in it; a call where it cannot be written gets a placeholder, listed in warnings with method groups that cannot be updated. Refused for a method that overrides, implements or is overridden. | `sourceFile`, `variableName`, `line` |

### Using Directives

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `add_missing_usings` | Add missing using directives required to resolve unbound type references. Process a single file or all files in the solution. | `sourceFile`, `allFiles` |
| `remove_unused_usings` | Remove unused using directives. Process a single file or all files in the solution. | `sourceFile`, `allFiles` |
| `sort_usings` | Sort using directives alphabetically in a C# file. | `sourceFile` |

### Diagnostics

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `diagnose` | Check the health of the Roslyn MCP server environment and workspace status. | `solutionPath` (optional), `verbose` |

### Code Navigation

These read-only tools let you explore and understand your codebase without making changes. Use them to discover symbols, trace references, and inspect type information before refactoring.

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `find_references` | Find all references to a symbol across the entire solution, or within one file or project. Returns file locations, context snippets, and write/definition indicators. | `sourceFile`, `symbolName`, `line`, `column`, `maxResults`, `includeDeclaration`, `referenceFile`, `projectPath` |
| `go_to_definition` | Navigate to the source definition of a symbol. Supports partial classes with multiple definition locations. | `sourceFile`, `symbolName`, `line`, `column` |
| `get_symbol_info` | Get detailed metadata for any symbol: kind, accessibility, modifiers, base types, interfaces, members (filterable by kind, name and accessibility), parameters, return type, and XML documentation. | `sourceFile`, `symbolName`, `line`, `column`, `kindFilter`, `nameFilter`, `accessibilityFilter`, `maxResults` |
| `find_implementations` | Find the types implementing an interface, the classes derived from a class, or the implementations/overrides of a member; all or only the direct ones. | `sourceFile`, `symbolName`, `line`, `column`, `maxResults`, `transitive` |
| `search_symbols` | Search for symbols by name across the entire workspace: substring or exact, optionally case-sensitive. Filter by kind (class, method, property, etc.), project, namespace or file. | `query`, `kindFilter`, `maxResults`, `exactMatch`, `caseSensitive`, `projectPath`, `namespaceFilter`, `sourceFile` |

### Analysis & Metrics

These tools analyze your code without making changes. Use them to understand code quality, data flow, and control flow.

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `get_diagnostics` | Retrieve compiler diagnostics (errors, warnings, info) filtered by severity and optionally by file, project, ID or message. | `sourceFile`, `severityFilter`, `projectPath`, `diagnosticIds`, `excludeDiagnosticIds`, `messageFilter`, `maxResults` |
| `get_code_metrics` | Calculate code metrics: cyclomatic complexity, lines of code, maintainability index, class coupling, depth of inheritance. | `sourceFile`, `symbolName`, `line` |
| `analyze_control_flow` | Analyze control flow for a code region: start/end point reachability, return statements, and exit points. | `sourceFile`, `startLine`, `endLine` |
| `analyze_data_flow` | Analyze data flow for a code region: variables read/written inside, data flowing in/out, captured variables. | `sourceFile`, `startLine`, `endLine` |
| `find_callers` | Find all callers of a symbol across the entire solution, or within one file or project. | `sourceFile`, `symbolName`, `line`, `column`, `maxResults`, `callerFile`, `projectPath` |
| `get_type_hierarchy` | Retrieve the type hierarchy (base types and/or derived types, all or only direct ones) for a given type. | `sourceFile`, `symbolName`, `line`, `column`, `direction`, `transitive`, `maxResults` |
| `get_document_outline` | Get a hierarchical outline of all symbols in a file (namespaces, types, members), optionally limited by depth, kind or name. | `sourceFile`, `maxDepth`, `kindFilter`, `nameFilter`, `maxResults` |
| `find_unused_symbols` | List types and members declared in source that nothing references outside their own declaration (the members of an unused type are not listed separately). By default only symbols not visible outside their assembly, leaving out overrides, interface implementations, entry points, operators, parameterless constructors and attributed members. The results are candidates: reflection, serialization, XAML/Razor bindings and other assemblies are invisible. | `projectPath`, `sourceFile`, `kindFilter`, `includePublic`, `includeAttributed`, `maxResults` |

### Code Generation

These tools generate new code members for existing types.

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `generate_equals_hashcode` | Generate Equals() and GetHashCode() overrides for a type based on its fields/properties. | `sourceFile`, `typeName`, `fields` |
| `generate_tostring` | Generate a ToString() override for a type. | `sourceFile`, `typeName`, `fields`, `format` |
| `format_document` | Format a C# file using Roslyn's built-in formatter. | `sourceFile` |
| `add_null_checks` | Add null-check statements (ArgumentNullException.ThrowIfNull or guard clauses) for method parameters. | `sourceFile`, `methodName`, `line`, `style` |

---

## Preview Mode

Every refactoring tool supports a `preview` parameter. When set to `true`, the tool computes and returns the changes that would be made without writing anything to disk. This lets you review diffs before committing to a refactoring. A preview's `warnings` also say what writing the change would do to the files' encodings: a file that would be saved as UTF-8 with BOM because its code page cannot hold the new text, and a file whose bytes are not valid in its own encoding, which makes the refactoring fail (see [File Encodings and Line Endings](#file-encodings-and-line-endings)); the preview itself succeeds, so the changes are still shown.

Example (as a natural language prompt to Claude):

> "Preview what would happen if I renamed OrderProcessor to OrderHandler in C:/path/to/MySolution.sln"

Claude will call `rename_symbol` with `preview: true` and show you the affected files and diffs.

---

## File Encodings and Line Endings

Files are read the way the compiler reads them (byte order mark, else the project's `<CodePage>`, else UTF-8 when the bytes are valid UTF-8, else the system's ANSI code page), and a file a refactoring edits keeps its encoding and line endings, so only the text it changes changes. A renamed or moved file keeps them too.

A file a refactoring creates (`move_type_to_file`, `extract_interface` / `extract_base_class` with a new `targetFile`) is written in the `.editorconfig` `charset` that applies to its path; else in the encoding of the file its content comes from (`move_type_to_file`); else in the encoding most of the project's files are in; else as UTF-8 with BOM. Generated text uses the file's line ending; a new file's comes from `end_of_line`, else from the file its content comes from (`move_type_to_file`), else from most of the project's files. Code moved into another file (a member pulled up, a type moved, a method body inlined) takes that file's line ending, except inside a verbatim or raw string literal, whose line breaks are part of its value: such a literal is kept as it is and reported in `warnings`.

When a file's code page cannot hold the new text (a `→` in a windows-1252 file), the file is saved as UTF-8 with BOM instead and the result's `warnings` say so. A file whose bytes are not valid in its own encoding is not rewritten: the refactoring fails with `FilesystemError` (4002) before writing anything.

---

## Analyzer Shadow Copying

Roslyn's default loader maps analyzer and source-generator assemblies in place, which locks the files on Windows -- a concurrent `dotnet build` then fails with MSB3021 (*"being used by another process: RoslynMcp.Server"*). The server instead loads private copies from a temp directory (`%TEMP%/RoslynMcp/AnalyzerShadowCopy`), the same approach Visual Studio uses, so build output is never locked.

Copies are per-process and cleaned up automatically -- on workspace eviction, on process exit (even a kill), and by a sweep at next start. If a generator is rebuilt, the workspace reloads on the next request. To disable, set `ROSLYNMCP_DISABLE_ANALYZER_SHADOW_COPY=1`.

---

## Troubleshooting

### .NET SDK not found

If you see errors about the SDK not being found:

1. Verify the SDK is installed: `dotnet --list-sdks`
2. Make sure .NET 11 or later appears in the list
3. If not, install it from [https://dotnet.microsoft.com/download/dotnet/11.0](https://dotnet.microsoft.com/download/dotnet/11.0)

### MSBuild or solution loading issues

If MSBuild cannot be located or your solution fails to load:

1. Make sure you can build the solution from the command line first: `dotnet build /path/to/MySolution.sln`
2. On Windows, ensure Visual Studio Build Tools or a Visual Studio installation is available
3. Check that `solutionPath` is an absolute path to a valid `.sln`, `.slnx`, `.csproj`, or standalone `.cs` file

### Build fails: file "being used by another process (RoslynMcp.Server)"

The running server predates [analyzer shadow copying](#analyzer-shadow-copying). Stop the server, run `dotnet tool update -g RoslynMcp.Server` (a running tool locks its own binaries), and reconnect.

### Using the diagnose tool

The `diagnose` tool is the first thing to try when something is not working. It reports:

- Whether Roslyn is loaded and its version
- Whether MSBuild was found and its version
- Whether the .NET SDK is available and its version
- Whether a given solution can be loaded, including project and document counts

Run it through Claude:

> "Run the roslyn diagnose tool with verbose output for C:/path/to/MySolution.sln"

Or without a solution path to check just the environment:

> "Run the roslyn diagnose tool"

---

## NuGet Libraries

In addition to the global tool, the project publishes libraries for building custom integrations:

```bash
# Core library -- refactoring operations and workspace management
dotnet add package RoslynMcp.Core

# Contracts library -- shared models and interfaces
dotnet add package RoslynMcp.Contracts
```

---

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

### Build from Source

```bash
git clone https://github.com/JoshuaRamirez/RoslynMcpServer.git
cd RoslynMcpServer
dotnet build -c Release
```

### Run Tests

```bash
# All tests
dotnet test

# Specific test projects
dotnet test tests/RoslynMcp.Core.Tests
dotnet test tests/RoslynMcp.Server.Tests
dotnet test tests/RoslynMcp.Cli.Tests
```

---

## License

This project is licensed under the MIT License. See the [LICENSE](./LICENSE) file for details.

---

## Acknowledgments

- Built with [Roslyn](https://github.com/dotnet/roslyn) -- the .NET Compiler Platform
- Implements the [Model Context Protocol](https://modelcontextprotocol.io)

## Support

- **Issues**: [GitHub Issues](https://github.com/JoshuaRamirez/RoslynMcpServer/issues)
- **Discussions**: [GitHub Discussions](https://github.com/JoshuaRamirez/RoslynMcpServer/discussions)
