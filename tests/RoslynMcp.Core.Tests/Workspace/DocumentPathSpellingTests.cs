using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Tests.Query;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// A <c>sourceFile</c> spelled differently from the path the workspace loaded the document
/// under still finds the document. <c>GetDocumentByPath</c> compared normalized paths with
/// <c>==</c>, so on Windows a path differing only in letter case failed in every tool.
/// </summary>
public sealed class DocumentPathSpellingTests
{
    private const string Source = """
        using System.Text;
        using System;

        namespace App;

        public class Widget
        {
            public void Render() => Console.WriteLine(new StringBuilder());
        }
        """;

    [Fact]
    public void GetDocumentByPath_FindsEveryEquivalentSpelling()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));
        var expected = ws.Context.Solution.Projects.Single().Documents.Single().Id;

        foreach (var spelling in EquivalentPaths.Of(ws.PathOf("Widget.cs")))
        {
            Assert.Equal(expected, ws.Context.GetDocumentByPath(spelling)?.Id);
        }
    }

    [Fact]
    public void GetDocumentByPath_DoesNotMatchAnotherFile()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source), ("Other.cs", "class Other { }"));

        Assert.EndsWith("Other.cs", ws.Context.GetDocumentByPath(ws.PathOf("Other.cs"))!.FilePath);
        Assert.Null(ws.Context.GetDocumentByPath(ws.PathOf("Missing.cs")));
    }

    [Fact]
    public async Task QueryTools_AcceptEveryEquivalentSpelling()
    {
        using var ws = TestWorkspace.Create(("Widget.cs", Source));
        var ct = TestContext.Current.CancellationToken;

        foreach (var spelling in EquivalentPaths.Of(ws.PathOf("Widget.cs")))
        {
            var outline = await new GetDocumentOutlineOperation(ws.Context).ExecuteAsync(
                new GetDocumentOutlineParams { SourceFile = spelling }, ct);
            var references = await new FindReferencesOperation(ws.Context).ExecuteAsync(
                new FindReferencesParams { SourceFile = spelling, SymbolName = "Render" }, ct);

            Assert.True(outline.Success, outline.Error?.Message);
            Assert.Equal(3, outline.Data!.TotalCount);
            Assert.True(references.Success, references.Error?.Message);
            Assert.Single(references.Data!.References);
        }
    }

    [Fact]
    public async Task RefactoringTools_AcceptEveryEquivalentSpelling()
    {
        var spellings = 0;
        using (var probe = TestWorkspace.Create(("Widget.cs", Source)))
            spellings = EquivalentPaths.Of(probe.PathOf("Widget.cs")).Count();

        for (var i = 0; i < spellings; i++)
        {
            using var ws = TestWorkspace.Create(("Widget.cs", Source));
            var spelling = EquivalentPaths.Of(ws.PathOf("Widget.cs")).ElementAt(i);

            var result = await new SortUsingsOperation(ws.Context).ExecuteAsync(
                new SortUsingsParams { SourceFile = spelling }, TestContext.Current.CancellationToken);

            Assert.True(result.Success, $"{spelling}: {result.Error?.Message}");
            Assert.StartsWith("using System;", ws.ReadFile("Widget.cs"));
        }
    }
}
