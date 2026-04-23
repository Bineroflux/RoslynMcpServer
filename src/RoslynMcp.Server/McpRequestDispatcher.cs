using System.Text.Json;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Logging;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server;

/// <summary>
/// Transport-agnostic MCP request dispatcher. Owns the tool registry and the
/// JSON-RPC method switch. Hosts (<see cref="McpServerHost"/> for stdio,
/// <see cref="HttpMcpServerHost"/> for HTTP) wrap this with their transport logic.
/// </summary>
public sealed class McpRequestDispatcher
{
    private readonly ToolRegistry _toolRegistry;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new dispatcher and registers every tool against the given workspace provider.
    /// </summary>
    public McpRequestDispatcher(IWorkspaceProvider workspaceProvider)
    {
        _toolRegistry = new ToolRegistry();

        _toolRegistry.Register(new MoveTypeToFileTool(workspaceProvider));
        _toolRegistry.Register(new MoveTypeToNamespaceTool(workspaceProvider));
        _toolRegistry.Register(new DiagnoseTool(workspaceProvider));

        // Phase 1 - Tier 1 Operations
        _toolRegistry.Register(new RenameSymbolTool(workspaceProvider));
        _toolRegistry.Register(new ExtractMethodTool(workspaceProvider));
        _toolRegistry.Register(new AddMissingUsingsTool(workspaceProvider));
        _toolRegistry.Register(new RemoveUnusedUsingsTool(workspaceProvider));
        _toolRegistry.Register(new SortUsingsTool(workspaceProvider));
        _toolRegistry.Register(new GenerateConstructorTool(workspaceProvider));

        // Phase 2 - Expanded Operations
        _toolRegistry.Register(new ExtractInterfaceTool(workspaceProvider));
        _toolRegistry.Register(new ImplementInterfaceTool(workspaceProvider));
        _toolRegistry.Register(new GenerateOverridesTool(workspaceProvider));
        _toolRegistry.Register(new ExtractVariableTool(workspaceProvider));
        _toolRegistry.Register(new InlineVariableTool(workspaceProvider));
        _toolRegistry.Register(new ExtractConstantTool(workspaceProvider));
        _toolRegistry.Register(new ChangeSignatureTool(workspaceProvider));
        _toolRegistry.Register(new EncapsulateFieldTool(workspaceProvider));
        _toolRegistry.Register(new ConvertToAsyncTool(workspaceProvider));
        _toolRegistry.Register(new ExtractBaseClassTool(workspaceProvider));

        // Code Navigation / Query Tools
        _toolRegistry.Register(new FindReferencesTool(workspaceProvider));
        _toolRegistry.Register(new GoToDefinitionTool(workspaceProvider));
        _toolRegistry.Register(new GetSymbolInfoTool(workspaceProvider));
        _toolRegistry.Register(new FindImplementationsTool(workspaceProvider));
        _toolRegistry.Register(new SearchSymbolsTool(workspaceProvider));

        // Analysis & Metrics Tools
        _toolRegistry.Register(new GetDiagnosticsTool(workspaceProvider));
        _toolRegistry.Register(new GetCodeMetricsTool(workspaceProvider));
        _toolRegistry.Register(new AnalyzeControlFlowTool(workspaceProvider));

        // Navigation & Hierarchy Tools
        _toolRegistry.Register(new FindCallersTool(workspaceProvider));
        _toolRegistry.Register(new GetTypeHierarchyTool(workspaceProvider));
        _toolRegistry.Register(new GetDocumentOutlineTool(workspaceProvider));

        // Code Generation & Formatting Tools
        _toolRegistry.Register(new GenerateEqualsHashCodeTool(workspaceProvider));
        _toolRegistry.Register(new GenerateToStringTool(workspaceProvider));
        _toolRegistry.Register(new FormatDocumentTool(workspaceProvider));
        _toolRegistry.Register(new AddNullChecksTool(workspaceProvider));

        // Data Flow & Conversion Tools
        _toolRegistry.Register(new AnalyzeDataFlowTool(workspaceProvider));
        _toolRegistry.Register(new ConvertExpressionBodyTool(workspaceProvider));
        _toolRegistry.Register(new ConvertPropertyTool(workspaceProvider));
        _toolRegistry.Register(new IntroduceParameterTool(workspaceProvider));

        // Syntax Conversion Tools
        _toolRegistry.Register(new ConvertForeachLinqTool(workspaceProvider));
        _toolRegistry.Register(new ConvertToPatternMatchingTool(workspaceProvider));
        _toolRegistry.Register(new ConvertToInterpolatedStringTool(workspaceProvider));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Dispatches a parsed JSON-RPC request to the appropriate handler.
    /// </summary>
    public async Task<McpResponse> DispatchAsync(McpRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            "initialize" => HandleInitialize(request),
            "initialized" => HandleInitialized(request),
            "notifications/initialized" => HandleInitialized(request),
            "tools/list" => HandleToolsList(request),
            "tools/call" => await HandleToolCallAsync(request, cancellationToken),
            "shutdown" => HandleShutdown(request),
            _ => McpResponse.Failure(request.Id, -32601, $"Method not found: {request.Method}")
        };
    }

    private static McpResponse HandleInitialize(McpRequest request)
    {
        return McpResponse.Success(request.Id, new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "roslyn-mcp",
                version = typeof(McpRequestDispatcher).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            }
        });
    }

    private static McpResponse HandleInitialized(McpRequest request)
    {
        return McpResponse.Success(request.Id, new { });
    }

    private McpResponse HandleToolsList(McpRequest request)
    {
        var tools = _toolRegistry.GetToolDefinitions();
        return McpResponse.Success(request.Id, new { tools });
    }

    private async Task<McpResponse> HandleToolCallAsync(McpRequest request, CancellationToken cancellationToken)
    {
        if (request.Params == null)
        {
            return McpResponse.Failure(request.Id, -32602, "Missing params");
        }

        ToolCallParams? callParams;
        try
        {
            callParams = JsonSerializer.Deserialize<ToolCallParams>(
                request.Params.Value.GetRawText(),
                _jsonOptions);
        }
        catch
        {
            return McpResponse.Failure(request.Id, -32602, "Invalid params");
        }

        if (callParams == null || string.IsNullOrEmpty(callParams.Name))
        {
            return McpResponse.Failure(request.Id, -32602, "Missing tool name");
        }

        var handler = _toolRegistry.GetHandler(callParams.Name);
        if (handler == null)
        {
            FileLogger.LogWarning($"Unknown tool requested: {callParams.Name}");
            return McpResponse.Failure(request.Id, -32602, $"Unknown tool: {callParams.Name}");
        }

        try
        {
            FileLogger.Log($"Executing tool: {callParams.Name}");
            var result = await handler.ExecuteAsync(callParams.Arguments, cancellationToken);
            FileLogger.Log($"Tool completed successfully: {callParams.Name}");
            return McpResponse.Success(request.Id, result);
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"Tool execution failed: {callParams.Name}", ex);
            return McpResponse.Failure(request.Id, -32603, $"Tool execution failed: {ex.Message}");
        }
    }

    private static McpResponse HandleShutdown(McpRequest request)
    {
        return McpResponse.Success(request.Id, new { });
    }
}
