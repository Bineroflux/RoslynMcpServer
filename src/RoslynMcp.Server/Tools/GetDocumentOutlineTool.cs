using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new get document outline tool.
    /// </summary>
    public GetDocumentOutlineTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "get_document_outline";

    /// <inheritdoc />
    public string Description => "Get a structured outline of all declarations in a C# file: namespaces, types, methods, properties, fields, and events with their line numbers and accessibility.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile" },
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
                description = "Absolute path to the source file"
            },
            maxDepth = new
            {
                type = "integer",
                description = "Maximum nesting depth; namespaces don't count. 1 = top-level types only, 2 = plus their members.",
                minimum = 1
            },
            kindFilter = new
            {
                type = "string",
                description = "Only entries of this kind, with their containing namespaces/types: Class, Struct, Interface, Enum, Record, Delegate, Method (incl. constructors), Property, Field, Event, Constant (incl. enum members), Namespace"
            },
            nameFilter = new
            {
                type = "string",
                description = "Only entries whose name contains this text (case-insensitive substring), with their containing namespaces/types"
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of entries, counted depth-first with parents first; totalCount and truncated report the rest",
                minimum = 1
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

            var args = JsonSerializer.Deserialize<GetDocumentOutlineArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new GetDocumentOutlineOperation(context);
            var @params = new GetDocumentOutlineParams
            {
                SourceFile = args.SourceFile,
                MaxDepth = args.MaxDepth,
                KindFilter = args.KindFilter,
                NameFilter = args.NameFilter,
                MaxResults = args.MaxResults
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

    private sealed class GetDocumentOutlineArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public int? MaxDepth { get; init; }
        public string? KindFilter { get; init; }
        public string? NameFilter { get; init; }
        public int? MaxResults { get; init; }
    }
}
