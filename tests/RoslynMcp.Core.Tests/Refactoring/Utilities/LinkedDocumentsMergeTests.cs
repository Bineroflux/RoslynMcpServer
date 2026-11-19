using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// <see cref="LinkedDocuments.MergeAsync"/> gives the documents of a multi-targeted file one
/// text with the changes each framework's document got on its own, lines added or removed
/// included (the rename-only merge it replaces left such a file as one version).
/// </summary>
public sealed class LinkedDocumentsMergeTests
{
    private static readonly string Original = Lf("""
        class C
        {
            void M(
                int a,
                int b)
            {
        #if NET11
                M(1, 2);
        #else
                M(3, 4);
        #endif
            }
        }
        """);

    [Fact]
    public async Task ChangesOfEveryVersion_AreCombined_SharedOnesOnce()
    {
        // Both versions add the parameter line; each rewrites the call its framework compiles.
        var net10 = Original
            .Replace("        int b)", "        int b,\n        int c)")
            .Replace("M(3, 4);", "M(3, 4, 0);");
        var net11 = Original
            .Replace("        int b)", "        int b,\n        int c)")
            .Replace("M(1, 2);", "M(\n            1,\n            2,\n            0);");

        var (merged, conflicts) = await MergeAsync(net10, net11);

        Assert.Equal(Original
            .Replace("        int b)", "        int b,\n        int c)")
            .Replace("M(3, 4);", "M(3, 4, 0);")
            .Replace("M(1, 2);", "M(\n            1,\n            2,\n            0);"), merged);
        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task AChangeOnlyOneVersionMakes_IsKept()
    {
        var net10 = Original.Replace("void M(", "void N(");

        var (merged, conflicts) = await MergeAsync(net10, Original);

        Assert.Equal(net10, merged);
        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task LinesChangedDifferently_KeepThePreferredVersion_AndAreReported()
    {
        var net10 = Original.Replace("class C", "class D").Replace("M(3, 4);", "M(4, 3);");
        var net11 = Original.Replace("class C", "class E").Replace("M(1, 2);", "M(2, 1);");

        var (merged, conflicts) = await MergeAsync(net10, net11);

        Assert.Equal(Original.Replace("class C", "class D").Replace("M(3, 4);", "M(4, 3);").Replace("M(1, 2);", "M(2, 1);"), merged);
        var conflict = Assert.Single(conflicts);
        Assert.Contains("C.cs(1): the target frameworks change this line differently; kept the change of 'App(net10)', not that of 'App(net11)'", conflict);
    }

    [Fact]
    public void LineDiff_FindsInsertionsDeletionsAndChangedLines()
    {
        string[] before = ["a\n", "b\n", "c\n", "d\n", "e\n"];
        string[] after = ["a\n", "B\n", "c\n", "x\n", "y\n", "e\n"];

        var hunks = LineDiff.Hunks(before, after, version: 0);

        Assert.Collection(
            hunks,
            h => Assert.Equal((1, 1, "B\n"), (h.Start, h.Count, string.Concat(h.Lines))),
            h => Assert.Equal((3, 1, "x\ny\n"), (h.Start, h.Count, string.Concat(h.Lines))));
        Assert.Empty(LineDiff.Hunks(before, before, version: 0));
        Assert.Equal((0, 5, ""), LineDiff.Hunks(before, [], version: 0).Select(h => (h.Start, h.Count, string.Concat(h.Lines))).Single());
    }

    private static async Task<(string Merged, List<string> Conflicts)> MergeAsync(string net10, string net11)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var ids = new List<DocumentId>();
        foreach (var framework in new[] { "net10", "net11" })
        {
            var project = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(project, VersionStamp.Default, $"App({framework})", "App", LanguageNames.CSharp));
            var id = DocumentId.CreateNewId(project);
            solution = solution.AddDocument(id, "C.cs", SourceText.From(Original), filePath: @"C:\App\C.cs");
            ids.Add(id);
        }

        var changed = solution
            .WithDocumentText(ids[0], SourceText.From(net10))
            .WithDocumentText(ids[1], SourceText.From(net11));
        var conflicts = new List<string>();

        var merged = await LinkedDocuments.MergeAsync(solution, changed, ids[0], TestContext.Current.CancellationToken, conflicts);

        var texts = new List<string>();
        foreach (var id in ids)
            texts.Add((await merged.GetDocument(id)!.GetTextAsync(TestContext.Current.CancellationToken)).ToString());
        Assert.Equal(texts[0], texts[1]);
        return (texts[0], conflicts);
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";
}
