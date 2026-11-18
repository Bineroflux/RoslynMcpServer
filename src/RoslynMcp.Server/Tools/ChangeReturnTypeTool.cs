using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for change_return_type operation.
/// </summary>
public sealed class ChangeReturnTypeTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new change return type tool.
    /// </summary>
    public ChangeReturnTypeTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "change_return_type";

    /// <inheritdoc />
    public string Description =>
        "Change the return type of a method, or the type of a property or indexer, together with the members it overrides " +
        "or implements and its overrides and implementations. Only the declarations change: return statements, bodies and " +
        "callers are not rewritten; the compiler errors this causes are listed in 'warnings'. Refused when a member of the " +
        "family is declared in metadata (e.g. an override of object.ToString).";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "memberName", "newType" },
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
                description = "Absolute path to the source file declaring the member"
            },
            memberName = new
            {
                type = "string",
                description = "Name of the method or property; 'this' for an indexer"
            },
            line = new
            {
                type = "integer",
                description = "Line of the declaration if several members have the name (1-based)",
                minimum = 1
            },
            newType = new
            {
                type = "string",
                description = "The new type as written in C# where the member is declared, e.g. 'long', 'Task<string?>' or 'void' (methods only)"
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes and warnings without applying",
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
            {
                return ToolResult.Error("Arguments required");
            }

            var args = JsonSerializer.Deserialize<ChangeReturnTypeArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ChangeReturnTypeOperation(context);
            var @params = new ChangeReturnTypeParams
            {
                SourceFile = args.SourceFile,
                MemberName = args.MemberName,
                Line = args.Line,
                NewType = args.NewType,
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

    private sealed class ChangeReturnTypeArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string MemberName { get; init; } = "";
        public int? Line { get; init; }
        public string NewType { get; init; } = "";
        public bool? Preview { get; init; }
    }
}
