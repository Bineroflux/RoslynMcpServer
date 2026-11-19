using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for change_signature operation.
/// </summary>
public sealed class ChangeSignatureTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new change signature tool.
    /// </summary>
    public ChangeSignatureTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "change_signature";

    /// <inheritdoc />
    public string Description =>
        "Add, remove, rename, retype or reorder method parameters and update all call sites. 'parameters' lists only " +
        "the changes: parameters it does not list stay, in their order. The change also applies to the members the " +
        "method overrides or implements and to its overrides and implementations, matching parameters by position. " +
        "Calls keep their argument order; a new parameter with a default value is left out of them (later positional " +
        "arguments are then passed by name). Compiler errors left behind (e.g. a method group converted to a delegate, " +
        "a removed parameter the body still uses) and the TODO placeholders passed for new parameters without a " +
        "default are listed in 'warnings'. Refused when a member of that family is declared in metadata (e.g. an " +
        "override of object.ToString), or when a new name is already used in a member's body or parameter list.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "methodName", "parameters" },
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
                description = "Absolute path to the source file containing the method"
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to modify"
            },
            line = new
            {
                type = "integer",
                description = "Line number for disambiguation if multiple methods have the same name (1-based)"
            },
            parameters = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        originalName = new
                        {
                            type = "string",
                            description = "The parameter to change or remove, as the method names it. Leave out to add a new parameter."
                        },
                        name = new
                        {
                            type = "string",
                            description = "New name (renames the parameter in every member of the family, in their bodies and in named arguments), or the new parameter's name (required then). Leave out to keep the name."
                        },
                        type = new
                        {
                            type = "string",
                            description = "New type, or the new parameter's type (required then). Leave out to keep the type."
                        },
                        defaultValue = new
                        {
                            type = "string",
                            description = "Default value as a C# expression. For an existing parameter, leave out to keep its default and give \"\" to remove it. A new parameter with a default is left out of existing calls; one without gets a 'default /* TODO */' placeholder in them (listed in warnings)."
                        },
                        newPosition = new
                        {
                            type = "integer",
                            description = "0-based position in the new parameter list. Leave out to keep the parameter's place among the parameters that do not move; a new parameter then goes at the end. Positions must lie within the new list and differ.",
                            minimum = 0
                        },
                        remove = new
                        {
                            type = "boolean",
                            description = "Remove the parameter named by originalName; the other fields must then be left out (or equal the parameter's).",
                            @default = false
                        }
                    },
                    additionalProperties = false
                },
                description = "The changes, one entry per parameter to change, remove or add. Parameters not listed stay as they are, in their relative order. An entry with originalName changes that parameter; one without adds a parameter (name and type required)."
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

            var args = JsonSerializer.Deserialize<ChangeSignatureArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ChangeSignatureOperation(context);
            var @params = new ChangeSignatureParams
            {
                SourceFile = args.SourceFile,
                MethodName = args.MethodName,
                Line = args.Line,
                Parameters = args.Parameters?.Select(p => new ParameterChange
                {
                    OriginalName = p.OriginalName,
                    Name = p.Name,
                    Type = p.Type,
                    DefaultValue = p.DefaultValue,
                    NewPosition = p.NewPosition,
                    Remove = p.Remove ?? false
                }).ToList() ?? new List<ParameterChange>(),
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

    private sealed class ChangeSignatureArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string MethodName { get; init; } = "";
        public int? Line { get; init; }
        public List<ParameterChangeArg>? Parameters { get; init; }
        public bool? Preview { get; init; }
    }

    private sealed class ParameterChangeArg
    {
        public string? OriginalName { get; init; }
        public string? Name { get; init; }
        public string? Type { get; init; }
        public string? DefaultValue { get; init; }
        public int? NewPosition { get; init; }
        public bool? Remove { get; init; }
    }
}
