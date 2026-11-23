---
description: Install the Roslyn MCP Server .NET global tool from a local clone
allowed-tools: Bash(dotnet:*), Bash(pwsh:*), Bash(roslyn-mcp:*), Bash(which:*)
---

# Roslyn MCP Server Setup

Install and verify the Roslyn MCP Server .NET global tool. This fork is not published to nuget.org: it is built and installed from a local clone of the repository.

## Step 1: Check Prerequisites

Verify .NET SDK is installed (11.0+ required):

```
dotnet --version
```

If not installed, direct the user to https://dotnet.microsoft.com/download/dotnet/11.0

## Step 2: Check Existing Installation

Check if roslyn-mcp is already installed:

```
dotnet tool list -g | grep -i roslyn
```

This fork installs as `orca.roslynmcp.server`. A `roslynmcp.server` entry is the upstream package from nuget.org; the install below replaces it.

## Step 3: Install the Tool

From the root of the RoslynMcpServer clone (ask the user for its path if it is not the current directory):

```
pwsh ./install-local.ps1
```

The script packs `Orca.RoslynMcp.Server` and `Orca.RoslynMcp.Cli` with a `-local` version suffix, uninstalls them and the upstream packages `RoslynMcp.Server` / `RoslynMcp.Cli` (which install the same commands), and installs both as global tools. Run it again to update. A running server locks its own binaries: stop the MCP clients that use it first.

## Step 4: Verify Installation

Confirm the tool is available on PATH:

```
which roslyn-mcp
```

Then verify it runs:

```
roslyn-mcp --help
```

## Troubleshooting

If `roslyn-mcp` is not found after install:
1. Ensure `~/.dotnet/tools` is on your PATH
2. For zsh: `export PATH="$HOME/.dotnet/tools:$PATH"` in `~/.zshrc`
3. For bash: `export PATH="$HOME/.dotnet/tools:$PATH"` in `~/.bashrc`
4. Restart your terminal after PATH changes
