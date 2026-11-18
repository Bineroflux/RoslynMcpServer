using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for the find_unused_symbols query.
/// </summary>
public sealed class FindUnusedSymbolsTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new find unused symbols tool.
    /// </summary>
    public FindUnusedSymbolsTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "find_unused_symbols";

    /// <inheritdoc />
    public string Description =>
        "List types and members declared in source that nothing references outside their own declaration " +
        "(a recursive call or a type naming itself is no use). By default only symbols not visible outside their " +
        "assembly, and without overrides, interface implementations, entry points, operators, parameterless " +
        "constructors and attributed members. These are candidates, not proof: reflection, serialization, " +
        "XAML/Razor bindings, strings and other assemblies are invisible to the search.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath" },
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Absolute path to the workspace root: a .sln, .slnx, or .csproj file, or a standalone .cs file (e.g. a .NET file-based program loaded into an ad-hoc, project-less workspace)"
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a project file of the solution. Reports only symbols declared in it (all its target frameworks); uses are still searched everywhere."
            },
            sourceFile = new
            {
                type = "string",
                description = "Absolute path to a .cs file. Reports only symbols declared in it (any declaration of a partial type)."
            },
            kindFilter = new
            {
                type = "string",
                description = "Only this kind: Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant"
            },
            includePublic = new
            {
                type = "boolean",
                description = "Also report symbols visible outside their assembly: public, or protected in a public type that can be derived from (default: false)"
            },
            includeAttributed = new
            {
                type = "boolean",
                description = "Also report attributed symbols, types containing them, and fields of [StructLayout]/[Serializable] types (default: false)"
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of symbols to return (default: 100); totalCount counts all",
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

            var args = JsonSerializer.Deserialize<FindUnusedSymbolsArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new FindUnusedSymbolsOperation(context);
            var @params = new FindUnusedSymbolsParams
            {
                ProjectPath = args.ProjectPath,
                SourceFile = args.SourceFile,
                KindFilter = args.KindFilter,
                IncludePublic = args.IncludePublic,
                IncludeAttributed = args.IncludeAttributed,
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

    private sealed class FindUnusedSymbolsArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? ProjectPath { get; init; }
        public string? SourceFile { get; init; }
        public string? KindFilter { get; init; }
        public bool? IncludePublic { get; init; }
        public bool? IncludeAttributed { get; init; }
        public int? MaxResults { get; init; }
    }
}
