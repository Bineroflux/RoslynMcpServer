using System.Text.RegularExpressions;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// get_symbol_info's member options for a type: <c>kindFilter</c>, <c>nameFilter</c>,
/// <c>accessibilityFilter</c> and <c>maxResults</c>.
/// </summary>
public sealed class GetSymbolInfoMemberFilterTests
{
    private const string Source = """
        namespace App;

        public class Widget
        {
            public Widget() { }
            public void Render() { }
            public void RenderFast() { }
            protected void OnRender() { }
            internal int Size { get; }
            private int _count;
            protected internal string? Name { get; set; }
            private protected void Reset() { }
            public const int Max = 3;
            public event System.EventHandler? Changed;
            public class Part { }
        }
        """;

    [Fact]
    public async Task WithoutOptions_ListsEveryMember()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var info = await InfoAsync(ws, new GetSymbolInfoParams { SourceFile = "" });

        Assert.Equal(
            ["Changed", "Max", "Name", "OnRender", "Part", "Render", "RenderFast", "Reset", "Size", "_count"],
            MemberNames(info));
        Assert.Equal(10, info.MembersTotalCount);
        Assert.False(info.MembersTruncated);
    }

    [Fact]
    public async Task KindFilter_KeepsMembersOfThatKind()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var methods = await InfoAsync(ws, new GetSymbolInfoParams { SourceFile = "", KindFilter = "method" });
        var constants = await InfoAsync(ws, new GetSymbolInfoParams { SourceFile = "", KindFilter = "Constant" });

        Assert.Equal(["OnRender", "Render", "RenderFast", "Reset"], MemberNames(methods));
        Assert.Equal(4, methods.MembersTotalCount);
        Assert.Equal(["Max"], MemberNames(constants));
    }

    [Fact]
    public async Task NameFilter_IsACaseInsensitiveSubstring()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var info = await InfoAsync(ws, new GetSymbolInfoParams { SourceFile = "", NameFilter = "RENDER" });

        Assert.Equal(["OnRender", "Render", "RenderFast"], MemberNames(info));
    }

    [Theory]
    [InlineData("public", new[] { "Changed", "Max", "Part", "Render", "RenderFast" })]
    [InlineData("Protected", new[] { "OnRender" })]
    [InlineData("internal", new[] { "Size" })]
    [InlineData("private", new[] { "_count" })]
    [InlineData("protected internal", new[] { "Name" })]
    [InlineData("  Protected   Internal ", new[] { "Name" })]
    [InlineData("ProtectedOrInternal", new[] { "Name" })]
    [InlineData("private protected", new[] { "Reset" })]
    public async Task AccessibilityFilter_MatchesTheDeclaredAccessibilityExactly(string accessibility, string[] expected)
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var info = await InfoAsync(ws, new GetSymbolInfoParams { SourceFile = "", AccessibilityFilter = accessibility });

        Assert.Equal(expected, MemberNames(info));
    }

    [Fact]
    public async Task Filters_Combine()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var info = await InfoAsync(ws, new GetSymbolInfoParams
        {
            SourceFile = "",
            KindFilter = "Method",
            AccessibilityFilter = "public",
            NameFilter = "fast"
        });

        Assert.Equal(["RenderFast"], MemberNames(info));
    }

    [Fact]
    public async Task MaxResults_TruncatesTheFilteredMembers()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var info = await InfoAsync(ws, new GetSymbolInfoParams { SourceFile = "", KindFilter = "Method", MaxResults = 2 });

        Assert.Equal(2, info.Members!.Count);
        Assert.Equal(4, info.MembersTotalCount);
        Assert.True(info.MembersTruncated);
    }

    [Fact]
    public async Task ForANonType_TheOptionsAreIgnored()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var info = await InfoAsync(ws, new GetSymbolInfoParams
        {
            SourceFile = "",
            SymbolName = "RenderFast",
            KindFilter = "Method",
            MaxResults = 1
        });

        Assert.Equal("RenderFast", info.Name);
        Assert.Null(info.Members);
        Assert.Null(info.MembersTotalCount);
        Assert.Null(info.MembersTruncated);
    }

    [Theory]
    [InlineData("Kind", "Widget", ErrorCodes.InvalidSymbolKind)]
    [InlineData("Accessibility", "friend", ErrorCodes.InvalidVisibility)]
    [InlineData("MaxResults", "0", ErrorCodes.MissingRequiredParam)]
    public async Task InvalidOptions_AreRejected(string option, string value, string expectedCode)
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => InfoAsync(ws, new GetSymbolInfoParams
        {
            SourceFile = "",
            KindFilter = option == "Kind" ? value : null,
            AccessibilityFilter = option == "Accessibility" ? value : null,
            MaxResults = option == "MaxResults" ? int.Parse(value) : null
        }));

        Assert.Equal(expectedCode, ex.ErrorCode);
    }

    /// <summary>Runs the query on <c>Widget</c>, or the symbol SymbolName names.</summary>
    private static async Task<DetailedSymbolInfo> InfoAsync(TestWorkspace ws, GetSymbolInfoParams @params)
    {
        var result = await new GetSymbolInfoOperation(ws.Context).ExecuteAsync(
            new GetSymbolInfoParams
            {
                SourceFile = ws.PathOf("Widget.cs"),
                SymbolName = @params.SymbolName ?? "Widget",
                KindFilter = @params.KindFilter,
                NameFilter = @params.NameFilter,
                AccessibilityFilter = @params.AccessibilityFilter,
                MaxResults = @params.MaxResults
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    /// <summary>
    /// The listed members' names, taken from their display strings (e.g. "void Widget.Render()",
    /// "int Widget.Size { get; }", or just "Part" for the nested type), sorted.
    /// </summary>
    private static List<string> MemberNames(DetailedSymbolInfo info) =>
        info.Members!
            .Select(m => Regex.Match(m, @"Widget\.(\w+)") is { Success: true } match ? match.Groups[1].Value : m)
            .Order(StringComparer.Ordinal)
            .ToList();
}
