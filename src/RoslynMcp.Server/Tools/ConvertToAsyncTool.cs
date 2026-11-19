using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for convert_to_async operation.
/// </summary>
public sealed class ConvertToAsyncTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new convert to async tool.
    /// </summary>
    public ConvertToAsyncTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "convert_to_async";

    /// <inheritdoc />
    public string Description =>
        "Convert a synchronous method to async: rename it to <name>Async with every reference (renameToAsync), add 'async' " +
        "and return Task / Task<T>. In its body, '.Result', '.Wait()' and '.GetAwaiter().GetResult()' on a task become " +
        "'await', and a call whose task was discarded is awaited; lambdas and local functions are left alone. Callers " +
        "that are async await the call; the others are left as they are and listed in 'warnings' with the compiler " +
        "errors this causes. Refused for methods that are async, iterators, abstract, virtual, overrides, interface " +
        "implementations, partial or extern, have ref/out/in parameters, return by ref or a task already, or have " +
        "nothing to await.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "methodName" },
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
            methodName = new
            {
                type = "string",
                description = "Name of the method to convert"
            },
            line = new
            {
                type = "integer",
                description = "Line number for disambiguation if multiple methods have the same name (1-based)"
            },
            renameToAsync = new
            {
                type = "boolean",
                description = "Add the Async suffix to the name, together with every reference (not when the name already ends in Async or the method is the entry point)",
                @default = true
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes without applying",
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

            var args = JsonSerializer.Deserialize<ConvertToAsyncArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ConvertToAsyncOperation(context);
            var @params = new ConvertToAsyncParams
            {
                SourceFile = args.SourceFile,
                MethodName = args.MethodName,
                Line = args.Line,
                RenameToAsync = args.RenameToAsync ?? true,
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

    private sealed class ConvertToAsyncArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string MethodName { get; init; } = "";
        public int? Line { get; init; }
        public bool? RenameToAsync { get; init; }
        public bool? Preview { get; init; }
    }
}
