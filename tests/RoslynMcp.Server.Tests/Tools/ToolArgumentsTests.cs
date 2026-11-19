using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Every tool's schema says <c>additionalProperties: false</c>, but the tools deserialize their
/// arguments leniently, so a mistyped or differently cased key (<c>MaxResults</c>,
/// <c>filePath</c>) was dropped and the option silently took its default. The server now
/// checks each call's arguments against the tool's schema first and refuses unknown keys.
/// </summary>
public sealed class ToolArgumentsTests
{
    [Fact]
    public async Task MisspelledArgument_IsRefused_WithTheClosestKeyAndTheValidOnes()
    {
        var error = await CallForErrorAsync("search_symbols", """{"solutionPath": "C:\\s.sln", "querry": "Foo"}""");

        Assert.Equal(ErrorCodes.UnknownParameter, error.GetProperty("code").GetString());
        Assert.Equal(
            "Unknown argument 'querry' (did you mean 'query'?). search_symbols takes: solutionPath, query, kindFilter, " +
            "maxResults, exactMatch, caseSensitive, projectPath, namespaceFilter, sourceFile.",
            error.GetProperty("message").GetString());
        Assert.Equal(["querry"], error.GetProperty("details").GetProperty("unknownArguments").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("query", error.GetProperty("details").GetProperty("validArguments").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ArgumentsInAnotherCase_AreRefused_AndTheMessageSaysNamesAreCaseSensitive()
    {
        var error = await CallForErrorAsync(
            "find_references",
            """{"solutionPath": "C:\\s.sln", "sourceFile": "C:\\a.cs", "line": 3, "MaxResults": 5, "SymbolName": "X", "colour": 1}""");

        var message = error.GetProperty("message").GetString()!;
        Assert.StartsWith(
            "Unknown arguments 'MaxResults' (did you mean 'maxResults'?), 'SymbolName' (did you mean 'symbolName'?) and 'colour'. find_references takes: solutionPath, ",
            message);
        Assert.EndsWith("Argument names are case-sensitive.", message);
    }

    [Fact]
    public async Task UnknownKeyInAParameterChange_IsRefused_WithItsPlace()
    {
        var error = await CallForErrorAsync(
            "change_signature",
            """
            {"solutionPath": "C:\\s.sln", "sourceFile": "C:\\a.cs", "methodName": "M",
             "parameters": [{"originalName": "a"}, {"orignalName": "b", "remove": true}]}
            """);

        Assert.Equal(ErrorCodes.UnknownParameter, error.GetProperty("code").GetString());
        Assert.Equal(
            "Unknown key 'orignalName' (did you mean 'originalName'?) in parameters[1], which takes: originalName, name, type, " +
            "defaultValue, newPosition, remove.",
            error.GetProperty("message").GetString());
        Assert.Equal(["parameters[1].orignalName"], error.GetProperty("details").GetProperty("unknownArguments").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ArgumentsThatAreNoObject_AreRefused()
    {
        var error = await CallForErrorAsync("search_symbols", "[1, 2]");

        Assert.Equal(ErrorCodes.InvalidParameterValue, error.GetProperty("code").GetString());
        Assert.Equal("The arguments of search_symbols must be a JSON object, not array.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ValidArguments_ReachTheTool()
    {
        // The fake provider refuses to load anything, so a call that passes the check fails there.
        var result = await CallAsync(
            "change_signature",
            """
            {"solutionPath": "C:\\s.sln", "sourceFile": "C:\\a.cs", "methodName": "M", "line": 3, "preview": true,
             "parameters": [{"originalName": "a", "name": "b", "type": "int", "defaultValue": "0", "newPosition": 1, "remove": false}]}
            """);

        Assert.True(result.IsError);
        Assert.Contains("No workspace in this test.", result.Content[0].Text);
    }

    [Fact]
    public void EverySchema_DeclaresExactlyTheKeysItsToolReads_WithMatchingTypes()
    {
        // The check refuses what a schema does not declare, so a key a tool reads but its schema
        // leaves out would break valid calls; one the schema declares but the tool never reads
        // would be accepted and ignored.
        var problems = new List<string>();
        var tools = CreateAllTools();

        foreach (var tool in tools)
        {
            var argsType = tool.GetType().GetNestedType(tool.GetType().Name[..^"Tool".Length] + "Args", BindingFlags.NonPublic);
            if (argsType == null)
            {
                problems.Add($"{tool.Name}: no nested {tool.GetType().Name[..^"Tool".Length]}Args class");
                continue;
            }

            CompareSchema(tool.Name, JsonSerializer.SerializeToElement(tool.InputSchema), argsType, problems);
        }

        Assert.Equal(47, tools.Count);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void CompareSchema(string path, JsonElement schema, Type type, List<string> problems)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var schemaType = schema.TryGetProperty("type", out var t) ? t.GetString() : null;

        string? expected = underlying == typeof(string) ? "string"
            : underlying == typeof(int) || underlying == typeof(long) ? "integer"
            : underlying == typeof(bool) ? "boolean"
            : ElementType(underlying) != null ? "array"
            : underlying.IsClass ? "object"
            : null;

        if (expected != schemaType)
        {
            problems.Add($"{path}: the schema says {schemaType ?? "no type"}, the tool reads {underlying.Name}");
            return;
        }

        if (ElementType(underlying) is { } element)
        {
            if (!schema.TryGetProperty("items", out var items))
                problems.Add($"{path}: an array without items");
            else
                CompareSchema(path + "[]", items, element, problems);
            return;
        }

        if (expected != "object")
            return;

        if (!schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
            problems.Add($"{path}: the object schema does not say additionalProperties: false");

        var declared = schema.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var read = ReadOptions.GetTypeInfo(underlying).Properties
            .Where(p => p.Set != null)
            .ToDictionary(p => p.Name, p => p.PropertyType);

        foreach (var name in declared.Keys.Except(read.Keys))
            problems.Add($"{path}: the schema declares '{name}', which the tool does not read");
        foreach (var name in read.Keys.Except(declared.Keys))
            problems.Add($"{path}: the tool reads '{name}', which the schema does not declare");
        foreach (var name in declared.Keys.Intersect(read.Keys))
            CompareSchema($"{path}.{name}", declared[name], read[name], problems);

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!).Where(r => !declared.ContainsKey(r)))
                problems.Add($"{path}: '{name}' is required but not declared");
        }
    }

    /// <summary>The tools' serializer options, able to describe any type.</summary>
    private static readonly JsonSerializerOptions ReadOptions = new(ToolJsonOptions.Default)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static Type? ElementType(Type type) =>
        type == typeof(string) ? null
        : type.IsArray ? type.GetElementType()
        : type.IsGenericType && type.GetGenericArguments() is [var argument] &&
          typeof(IEnumerable<>).MakeGenericType(argument).IsAssignableFrom(type) ? argument
        : null;

    /// <summary>One instance of every tool the server assembly declares.</summary>
    internal static List<IToolHandler> CreateAllTools()
    {
        var provider = new FakeProvider();
        return typeof(IToolHandler).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IToolHandler).IsAssignableFrom(t))
            .Select(t =>
            {
                var constructor = t.GetConstructors().Single();
                var arguments = constructor.GetParameters().Select(p =>
                    p.ParameterType == typeof(IWorkspaceProvider) ? provider
                    : p.ParameterType == typeof(Func<IEnumerable<string>>) ? (object)(Func<IEnumerable<string>>)(() => [])
                    : throw new InvalidOperationException($"{t.Name}: cannot create a {p.ParameterType}"));
                return (IToolHandler)constructor.Invoke(arguments.ToArray());
            })
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<JsonElement> CallForErrorAsync(string tool, string arguments)
    {
        var result = await CallAsync(tool, arguments);
        Assert.True(result.IsError);
        var root = JsonDocument.Parse(result.Content[0].Text!).RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        return root.GetProperty("error");
    }

    private static async Task<ToolResult> CallAsync(string tool, string arguments)
    {
        var dispatcher = new McpRequestDispatcher(new FakeProvider());
        var response = await dispatcher.DispatchAsync(
            new McpRequest
            {
                Id = 1,
                Method = "tools/call",
                Params = JsonDocument.Parse($$"""{"name": "{{tool}}", "arguments": {{arguments}}}""").RootElement
            },
            TestContext.Current.CancellationToken);

        Assert.Null(response.Error);
        return Assert.IsType<ToolResult>(response.Result);
    }

    private sealed class FakeProvider : IWorkspaceProvider
    {
        public Task<WorkspaceContext> CreateContextAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No workspace in this test.");

        public EnvironmentDiagnostics CheckEnvironment() => new()
        {
            MsBuildFound = true,
            DotnetRuntimeVersion = "11.0.0"
        };
    }
}
