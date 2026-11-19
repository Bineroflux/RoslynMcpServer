using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for the inline_method operation.
/// </summary>
public sealed class InlineMethodTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new inline method tool.
    /// </summary>
    public InlineMethodTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "inline_method";

    /// <inheritdoc />
    public string Description =>
        "Replace calls to a method with its body: every call, or the one at callLine (callFile, callColumn). A body " +
        "that is an expression or a single return inlines anywhere; a void method's statements replace calls that are " +
        "statements. Arguments are substituted, or first stored in locals when moving, repeating or dropping them " +
        "could change what happens; calls that can't take those locals (expression-bodied members, initializers, " +
        "conditional evaluation), or whose body uses members not accessible there, are left and listed in 'warnings' " +
        "with any compiler errors, and so is a receiver that may be null where the inlined code no longer throws " +
        "NullReferenceException for it. Refuses recursive, virtual, abstract, override, interface, partial, extern, async " +
        "and iterator methods and ref/out/in parameters.";

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
                description = "Absolute path to the source file declaring the method"
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to inline"
            },
            line = new
            {
                type = "integer",
                description = "1-based line of the method's declaration, when the file declares several methods of that name",
                minimum = 1
            },
            callFile = new
            {
                type = "string",
                description = "Absolute path of the file with the one call to inline (default: sourceFile); needs callLine"
            },
            callLine = new
            {
                type = "integer",
                description = "1-based line of the one call to inline; without it every call is inlined",
                minimum = 1
            },
            callColumn = new
            {
                type = "integer",
                description = "1-based column of the method name in the call, when the line has several calls",
                minimum = 1
            },
            deleteMethod = new
            {
                type = "boolean",
                description = "Delete the method when nothing references it afterwards (default: true when inlining every call, false for one call)"
            },
            preview = new
            {
                type = "boolean",
                description = "Return each call before and after (and the warnings) without applying",
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

            var args = JsonSerializer.Deserialize<InlineMethodArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new InlineMethodOperation(context);
            var @params = new InlineMethodParams
            {
                SourceFile = args.SourceFile,
                MethodName = args.MethodName,
                Line = args.Line,
                CallFile = args.CallFile,
                CallLine = args.CallLine,
                CallColumn = args.CallColumn,
                DeleteMethod = args.DeleteMethod,
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

    private sealed class InlineMethodArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string MethodName { get; init; } = "";
        public int? Line { get; init; }
        public string? CallFile { get; init; }
        public int? CallLine { get; init; }
        public int? CallColumn { get; init; }
        public bool? DeleteMethod { get; init; }
        public bool? Preview { get; init; }
    }
}
