using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for the rename_namespace operation.
/// </summary>
public sealed class RenameNamespaceTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new rename namespace tool.
    /// </summary>
    public RenameNamespaceTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = ToolJsonOptions.Default;
    }

    /// <inheritdoc />
    public string Name => "rename_namespace";

    /// <inheritdoc />
    public string Description =>
        "Rename a namespace declared in source, and its sub-namespaces, across the solution; the new name may have a " +
        "different depth (App.Old -> Company.App.New). Rewrites block and file-scoped declarations, usings (global, " +
        "static, alias), qualified and global:: names and crefs; not comments or strings. Adds the usings extension calls need and removes those it makes duplicates. In .razor/.cshtml files only directives are rewritten (@using, @namespace, and qualified type names in @inherits, @inject, @model, @implements, @attribute, @typeparam, @layout); other mentions are listed in 'warnings'. Refused when a type would " +
        "collide with an existing one, or an enclosing namespace block that also declares other things would have to " +
        "change. Generated files and project-level <Using>/<RootNamespace> are not changed; 'warnings' names them and " +
        "the compiler errors left behind. updateFolders moves the files that sit in their old namespace's folder into the new one's, where that is safe.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "oldNamespace", "newNamespace" },
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Absolute path to the workspace root: a .sln, .slnx, or .csproj file, or a standalone .cs file (e.g. a .NET file-based program loaded into an ad-hoc, project-less workspace)"
            },
            oldNamespace = new
            {
                type = "string",
                description = "Fully qualified namespace to rename, e.g. 'App.Old'; its sub-namespaces are renamed with it"
            },
            newNamespace = new
            {
                type = "string",
                description = "New fully qualified name, e.g. 'App.New' or 'Company.App.New'"
            },
            updateFolders = new
            {
                type = "boolean",
                description = "Also move each file whose types all end up in one renamed namespace, and which sits in the folder of its old namespace, into the folder of the new one (SDK-style projects with default globbing; confirmed by MSBuild, undone otherwise); other files stay, with a warning",
                @default = false
            },
            preview = new
            {
                type = "boolean",
                description = "Return the files that would change and move (and the warnings) without applying",
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

            var args = JsonSerializer.Deserialize<RenameNamespaceArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new RenameNamespaceOperation(context);
            var @params = new RenameNamespaceParams
            {
                OldNamespace = args.OldNamespace,
                NewNamespace = args.NewNamespace,
                UpdateFolders = args.UpdateFolders ?? false,
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

    private sealed class RenameNamespaceArgs
    {
        public string SolutionPath { get; init; } = "";
        public string OldNamespace { get; init; } = "";
        public string NewNamespace { get; init; } = "";
        public bool? UpdateFolders { get; init; }
        public bool? Preview { get; init; }
    }
}
