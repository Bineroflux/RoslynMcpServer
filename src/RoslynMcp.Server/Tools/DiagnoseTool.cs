using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for environment diagnostics.
/// </summary>
public sealed class DiagnoseTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly Func<IEnumerable<string>> _toolNames;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new diagnose tool.
    /// </summary>
    /// <param name="workspaceProvider">Workspace provider to check.</param>
    /// <param name="toolNames">The registered tools, reported as capabilities.</param>
    public DiagnoseTool(IWorkspaceProvider workspaceProvider, Func<IEnumerable<string>> toolNames)
    {
        _workspaceProvider = workspaceProvider;
        _toolNames = toolNames;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "diagnose";

    /// <inheritdoc />
    public string Description => "Check the health of the Roslyn MCP server environment (Roslyn, MSBuild, .NET SDK and runtime versions) and optionally test-load a solution.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Absolute path to a .sln, .slnx, .csproj or standalone .cs file to test loading (optional)"
            },
            verbose = new
            {
                type = "boolean",
                description = "Also report every MSBuild instance found and why one was selected, the registered MSBuild path, and for solutionPath each project's target framework, document count, analyzer/generator references and load failures, plus workspace load diagnostics",
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
            var args = arguments.HasValue
                ? JsonSerializer.Deserialize<DiagnoseArgs>(arguments.Value.GetRawText(), _jsonOptions)
                : null;
            args ??= new DiagnoseArgs();

            var result = await WorkspaceDiagnoser.RunAsync(
                _workspaceProvider,
                new DiagnoseParams { SolutionPath = args.SolutionPath, Verbose = args.Verbose ?? false },
                _toolNames(),
                cancellationToken);

            var json = JsonSerializer.Serialize(result, _jsonOptions);
            return ToolResult.Success(json);
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new
            {
                healthy = false,
                error = new { code = "INTERNAL_ERROR", message = ex.Message }
            }, _jsonOptions);
            return ToolResult.Error(json);
        }
    }

    private sealed class DiagnoseArgs
    {
        public string? SolutionPath { get; init; }
        public bool? Verbose { get; init; }
    }
}
