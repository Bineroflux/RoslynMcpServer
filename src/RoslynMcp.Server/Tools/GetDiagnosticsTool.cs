using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for get_diagnostics query.
/// </summary>
public sealed class GetDiagnosticsTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new get diagnostics tool.
    /// </summary>
    public GetDiagnosticsTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "get_diagnostics";

    /// <inheritdoc />
    public string Description => "Get compiler diagnostics (errors, warnings) for the solution or a specific file. Useful for checking compilation status before or after refactoring. Sorted by file, line and column; diagnostics without a file come first.";

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
            sourceFile = new
            {
                type = "string",
                description = "Absolute path to a .cs, .razor, or .cshtml file to restrict diagnostics to (optional). Razor diagnostics are mapped back to the source file via #line pragmas where available."
            },
            severityFilter = new
            {
                type = "string",
                description = "Minimum severity: Error, Warning (default), Info, Hidden, or All",
                @default = "Warning"
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a project file of the solution. Compiles and reports only that project (all its target frameworks)."
            },
            diagnosticIds = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Only these diagnostic IDs (e.g. CS0168), case-insensitive"
            },
            excludeDiagnosticIds = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Leave out these diagnostic IDs, case-insensitive. Wins over diagnosticIds."
            },
            messageFilter = new
            {
                type = "string",
                description = "Only diagnostics whose message contains this text (case-insensitive substring)"
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of diagnostics to return (default: all). totalCount still counts every match.",
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

            var args = JsonSerializer.Deserialize<GetDiagnosticsArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new GetDiagnosticsOperation(context);
            var @params = new GetDiagnosticsParams
            {
                SourceFile = args.SourceFile,
                SeverityFilter = args.SeverityFilter,
                ProjectPath = args.ProjectPath,
                DiagnosticIds = args.DiagnosticIds,
                ExcludeDiagnosticIds = args.ExcludeDiagnosticIds,
                MessageFilter = args.MessageFilter,
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

    private sealed class GetDiagnosticsArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public string? SeverityFilter { get; init; }
        public string? ProjectPath { get; init; }
        public IReadOnlyList<string>? DiagnosticIds { get; init; }
        public IReadOnlyList<string>? ExcludeDiagnosticIds { get; init; }
        public string? MessageFilter { get; init; }
        public int? MaxResults { get; init; }
    }
}
