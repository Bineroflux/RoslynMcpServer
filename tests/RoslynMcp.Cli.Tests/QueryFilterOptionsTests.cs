using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using Xunit;

namespace RoslynMcp.Cli.Tests;

/// <summary>
/// The CLI exposes the query tools' scoping and filtering options, which it reflects off
/// the contract params types.
/// </summary>
public class QueryFilterOptionsTests
{
    [Theory]
    [InlineData("get-diagnostics", "--project-path")]
    [InlineData("get-diagnostics", "--diagnostic-ids")]
    [InlineData("get-diagnostics", "--exclude-diagnostic-ids")]
    [InlineData("get-diagnostics", "--message-filter")]
    [InlineData("get-diagnostics", "--max-results")]
    [InlineData("search-symbols", "--exact-match")]
    [InlineData("search-symbols", "--case-sensitive")]
    [InlineData("search-symbols", "--project-path")]
    [InlineData("search-symbols", "--namespace-filter")]
    [InlineData("search-symbols", "--source-file")]
    [InlineData("find-references", "--include-declaration")]
    [InlineData("find-references", "--reference-file")]
    [InlineData("find-references", "--project-path")]
    public void ToolHelp_ListsTheOption(string toolName, string option)
    {
        var tool = ToolRegistry.BuildDefault().GetTool(toolName)!;

        var optional = HelpGenerator.GenerateToolHelp(tool).Split("OPTIONAL:")[1];

        Assert.Contains(option + " ", optional);
    }

    [Fact]
    public void ListOptions_TakeAJsonArray()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>
        {
            ["diagnostic-ids"] = """["CS0168","CS0219"]""",
            ["exclude-diagnostic-ids"] = """["CS0219"]""",
            ["project-path"] = @"C:\src\App\App.csproj",
            ["max-results"] = "5"
        });

        var @params = JsonSerializer.Deserialize<GetDiagnosticsParams>(json, JsonOptions)!;

        Assert.Equal(["CS0168", "CS0219"], @params.DiagnosticIds);
        Assert.Equal(["CS0219"], @params.ExcludeDiagnosticIds);
        Assert.Equal(@"C:\src\App\App.csproj", @params.ProjectPath);
        Assert.Equal(5, @params.MaxResults);
    }

    // The options the CLI's registry deserializes params with.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
