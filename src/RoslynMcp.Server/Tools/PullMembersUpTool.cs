using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for the pull_members_up operation.
/// </summary>
public sealed class PullMembersUpTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new pull members up tool.
    /// </summary>
    public PullMembersUpTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "pull_members_up";

    /// <inheritdoc />
    public string Description =>
        "Move members of a type into its base class, or add their signatures to an interface it implements " +
        "(targetType; static members as static abstract, C# 11+), into the part of a partial target in targetFile or " +
        "else its first part. An interface with a self type parameter (IAddable<TSelf> where TSelf : IAddable<TSelf>) " +
        "gets the signatures with TSelf for the type. Operators go into an interface only; pairs the compiler requires " +
        "(== and !=, a checked operator and its unchecked one) are pulled together. Declarations move with their doc comments and attributes; " +
        "private members become protected. With makeAbstract, methods, properties, " +
        "indexers and events are declared abstract in the base class (made abstract if nothing instantiates it) and " +
        "stay in the type as overrides. Refused when a member uses members that stay behind (they are listed), when the " +
        "target already declares it, or when its types can't be written in the target's type parameters. Adds the " +
        "usings the moved code needs (also using static and aliases) and removes those only it used from the files it " +
        "leaves; notes and the compiler errors left (e.g. other " +
        "derived types or implementers now lacking a member) are returned as 'warnings'.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "typeName", "members" },
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
                description = "Absolute path to the source file declaring the type"
            },
            typeName = new
            {
                type = "string",
                description = "Name of the type whose members move up"
            },
            members = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Names of the members to move: fields, methods (every overload), properties, events; 'this' for indexers; operators as written ('operator +', 'operator checked +', 'explicit operator Money') or by metadata name ('op_Addition')"
            },
            line = new
            {
                type = "integer",
                description = "1-based line of the type's declaration, when the file declares several types of that name",
                minimum = 1
            },
            targetType = new
            {
                type = "string",
                description = "Where the members go: the direct base class (default) or the name of an interface the type implements"
            },
            targetFile = new
            {
                type = "string",
                description = "Absolute path of the file whose part of a partial target gets the members (default: its first part in a file that isn't generated)"
            },
            makeAbstract = new
            {
                type = "boolean",
                description = "Base class only: declare methods, properties, indexers and events abstract there and keep the implementations as overrides",
                @default = false
            },
            preview = new
            {
                type = "boolean",
                description = "Return the declarations added and removed (and the warnings) without applying",
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

            var args = JsonSerializer.Deserialize<PullMembersUpArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new PullMembersUpOperation(context);
            var @params = new PullMembersUpParams
            {
                SourceFile = args.SourceFile,
                TypeName = args.TypeName,
                Members = args.Members ?? [],
                Line = args.Line,
                TargetType = args.TargetType,
                TargetFile = args.TargetFile,
                MakeAbstract = args.MakeAbstract ?? false,
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

    private sealed class PullMembersUpArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string TypeName { get; init; } = "";
        public List<string>? Members { get; init; }
        public int? Line { get; init; }
        public string? TargetType { get; init; }
        public string? TargetFile { get; init; }
        public bool? MakeAbstract { get; init; }
        public bool? Preview { get; init; }
    }
}
