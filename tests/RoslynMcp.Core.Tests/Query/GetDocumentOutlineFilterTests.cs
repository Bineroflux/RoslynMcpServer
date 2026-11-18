using System.Text.Json;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// get_document_outline's <c>maxDepth</c>, <c>kindFilter</c>, <c>nameFilter</c> and <c>maxResults</c>.
/// </summary>
public sealed class GetDocumentOutlineFilterTests
{
    private const string Source = """
        namespace App.Core
        {
            public class Widget
            {
                public Widget() { }
                public void Render() { }
                public int Size { get; }
                private int _count;
                public const int Max = 3;
                public event System.EventHandler? Changed;

                public class Part
                {
                    public void RenderPart() { }
                }

                public enum Mode { Fast, Slow }
            }

            public interface IRenderer
            {
                void Render();
            }

            public delegate void RenderCallback();
        }
        """;

    // Preorder of the full outline, 16 entries. Nested types come first among a type's children.
    private static readonly string[] FullOutline =
    [
        "Namespace App.Core",
        "Class Widget", "Class Part", "Method RenderPart", "Constructor Widget", "Method Render", "Property Size",
        "Field _count", "Constant Max", "Event Changed", "Enum Mode", "EnumMember Fast", "EnumMember Slow",
        "Interface IRenderer", "Method Render",
        "Delegate RenderCallback",
    ];

    [Fact]
    public async Task WithoutOptions_ReturnsTheWholeOutline()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "" });

        Assert.Equal(FullOutline, Flatten(result.Entries));
        Assert.Equal(16, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task OptionsThatKeepEverything_ReturnTheSameOutline()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var plain = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "" });
        var unfiltered = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", MaxDepth = 10, MaxResults = 1000 });

        Assert.Equal(JsonSerializer.Serialize(plain), JsonSerializer.Serialize(unfiltered));
    }

    [Fact]
    public async Task MaxDepth1_KeepsNamespacesAndTopLevelTypes()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", MaxDepth = 1 });

        Assert.Equal(["Namespace App.Core", "Class Widget", "Interface IRenderer", "Delegate RenderCallback"], Flatten(result.Entries));
        Assert.Equal(4, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task MaxDepth2_AddsTheMembersOfTopLevelTypes()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", MaxDepth = 2 });

        Assert.Equal(
            ["Namespace App.Core",
             "Class Widget", "Class Part", "Constructor Widget", "Method Render", "Property Size", "Field _count",
             "Constant Max", "Event Changed", "Enum Mode",
             "Interface IRenderer", "Method Render",
             "Delegate RenderCallback"],
            Flatten(result.Entries));
    }

    [Theory]
    [InlineData("namespace App;\npublic class A { public void M() { } }\n", "Namespace App", "Class A")]
    [InlineData("public class A { public void M() { } }\n", "Class A", null)]
    public async Task MaxDepth1_WorksWithFileScopedAndWithoutNamespaces(string source, string first, string? second)
    {
        using var ws = TestWorkspace.Create(("A.cs", source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "A.cs", MaxDepth = 1 });

        Assert.Equal(second is null ? [first] : [first, second], Flatten(result.Entries));
    }

    [Fact]
    public async Task KindFilter_KeepsMatchesWithTheirContainers()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", KindFilter = "method" });

        Assert.Equal(
            ["Namespace App.Core", "Class Widget", "Class Part", "Method RenderPart", "Constructor Widget", "Method Render",
             "Interface IRenderer", "Method Render"],
            Flatten(result.Entries));
        Assert.Equal(8, result.TotalCount);
    }

    [Fact]
    public async Task KindFilter_Constant_IncludesEnumMembers()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", KindFilter = "Constant" });

        Assert.Equal(
            ["Namespace App.Core", "Class Widget", "Constant Max", "Enum Mode", "EnumMember Fast", "EnumMember Slow"],
            Flatten(result.Entries));
    }

    [Fact]
    public async Task KindFilter_OfAContainer_DropsItsNonMatchingMembers()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", KindFilter = "Interface" });

        Assert.Equal(["Namespace App.Core", "Interface IRenderer"], Flatten(result.Entries));
    }

    [Fact]
    public async Task NameFilter_IsACaseInsensitiveSubstring()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", NameFilter = "RENDER" });

        Assert.Equal(
            ["Namespace App.Core", "Class Widget", "Class Part", "Method RenderPart", "Method Render",
             "Interface IRenderer", "Method Render", "Delegate RenderCallback"],
            Flatten(result.Entries));
    }

    [Fact]
    public async Task Filters_Combine()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var kindAndName = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", KindFilter = "Method", NameFilter = "part" });
        var nameAndDepth = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", NameFilter = "part", MaxDepth = 2 });

        Assert.Equal(["Namespace App.Core", "Class Widget", "Class Part", "Method RenderPart"], Flatten(kindAndName.Entries));
        Assert.Equal(["Namespace App.Core", "Class Widget", "Class Part"], Flatten(nameAndDepth.Entries));
    }

    [Fact]
    public async Task MaxResults_TakesEntriesInPreorder()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", MaxResults = 5 });

        Assert.Equal(FullOutline.Take(5), Flatten(result.Entries));
        Assert.Equal(16, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task MaxResults_CountsTheFilteredOutline()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var result = await OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", KindFilter = "Method", MaxResults = 4 });

        Assert.Equal(["Namespace App.Core", "Class Widget", "Class Part", "Method RenderPart"], Flatten(result.Entries));
        Assert.Equal(8, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task InvalidKindFilter_IsRejected()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", KindFilter = "Constructor" }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("Method", ex.Message);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 0)]
    public async Task MaxDepthOrMaxResultsBelowOne_IsRejected(int? maxDepth, int? maxResults)
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            OutlineAsync(ws, new GetDocumentOutlineParams { SourceFile = "", MaxDepth = maxDepth, MaxResults = maxResults }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    /// <summary>Runs the query on the workspace's only file, or the one named by SourceFile.</summary>
    private static async Task<GetDocumentOutlineResult> OutlineAsync(TestWorkspace ws, GetDocumentOutlineParams @params)
    {
        var file = string.IsNullOrEmpty(@params.SourceFile) ? "Widget.cs" : @params.SourceFile;
        var result = await new GetDocumentOutlineOperation(ws.Context).ExecuteAsync(
            new GetDocumentOutlineParams
            {
                SourceFile = ws.PathOf(file),
                MaxDepth = @params.MaxDepth,
                KindFilter = @params.KindFilter,
                NameFilter = @params.NameFilter,
                MaxResults = @params.MaxResults
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    private static List<string> Flatten(IEnumerable<OutlineEntry> entries) =>
        entries.SelectMany(e => Flatten(e.Children ?? []).Prepend($"{e.Kind} {e.Name}")).ToList();
}
