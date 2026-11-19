using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for the safe_delete operation.
/// </summary>
public sealed class SafeDeleteTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new safe delete tool.
    /// </summary>
    public SafeDeleteTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "safe_delete";

    /// <inheritdoc />
    public string Description =>
        "Delete a type or member only if nothing outside its own declaration references it; otherwise fail and list " +
        "the references (nameof and doc-comment crefs count; crefs are marked). Removes every partial part, a single " +
        "variable of 'int a, b;', the doc comment above it, the usings only it used, and the file when a type was all it declared. Refuses " +
        "overrides, interface implementations and members that are overridden or implemented. Reflection and other " +
        "assemblies are invisible; compiler errors left behind are returned as 'warnings'.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "symbolName" },
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Absolute path to the workspace root: a .sln, .slnx, or .csproj file, or a standalone .cs file (e.g. a .NET file-based program loaded into an ad-hoc, project-less workspace)"
            },
            sourceFile = new
            {
                type = "string",
                description = "Absolute path to a source file declaring or using the symbol"
            },
            symbolName = new
            {
                type = "string",
                description = "Name of the type or member to delete; a constructor by its type's name together with its line; an operator by its metadata name (op_Addition)"
            },
            line = new
            {
                type = "integer",
                description = "1-based line of the symbol, when several in the file have the name",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column of the symbol on that line",
                minimum = 1
            },
            preview = new
            {
                type = "boolean",
                description = "Return the declarations that would be removed (and the warnings) without applying",
                @default = false
            }
        },
        additionalProperties = false
    };

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments == null)
                return ToolResult.Error("Arguments required");

            var args = JsonSerializer.Deserialize<SafeDeleteArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new SafeDeleteOperation(context);
            var @params = new SafeDeleteParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                Column = args.Column,
                Preview = args.Preview ?? false
            };

            var result = await operation.ExecuteAsync(@params, cancellationToken);
            var json = JsonSerializer.Serialize(result, _jsonOptions);
            return result.Success ? ToolResult.Success(json) : ToolResult.Error(json);
        }
        catch (RefactoringException ex)
        {
            var error = ex.ToError();
            var json = JsonSerializer.Serialize(new { success = false, error }, _jsonOptions);
            return ToolResult.Error(json);
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new
            {
                success = false,
                error = new { code = "INTERNAL_ERROR", message = ex.Message }
            }, _jsonOptions);
            return ToolResult.Error(json);
        }
    }

    private sealed class SafeDeleteArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string SymbolName { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? Preview { get; init; }
    }
}
