using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// A multi-targeted project compiles each file once per target framework. Roslyn's renamer
/// renamed each of those documents on its own, so a reference in an <c>#if</c> branch that only
/// one framework compiles was renamed in that framework's document only, and the commit wrote
/// one version: <c>Read</c> → <c>Fetch</c> left <c>Read()</c> in the other branch, and that
/// framework's build broke.
/// </summary>
public sealed class RenameSymbolMultiTargetTests
{
    private const string Feed = """
        namespace App;

        public class Feed
        {
            public string Read() => "x";

            public int Length()
            {
        #if NET8
                return Read().Length;
        #else
                return Read().Length + 1;
        #endif
            }
        }
        """;

    private const string Use = """
        namespace App;

        public static class Use
        {
        #if NET9
            public static string Twice(Feed feed) => feed.Read() + feed.Read();
        #else
            public static string Once(Feed feed) => feed.Read();
        #endif
        }
        """;

    [Fact]
    public async Task RenamingAMethod_RenamesTheReferencesOfEveryTargetFramework()
    {
        using var ws = CreateWorkspace();

        var result = await RenameAsync(ws, "Feed.cs", "Read", "Fetch");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace App;

            public class Feed
            {
                public string Fetch() => "x";

                public int Length()
                {
            #if NET8
                    return Fetch().Length;
            #else
                    return Fetch().Length + 1;
            #endif
                }
            }
            """), File.ReadAllText(ws.PathOf("Multi", "Feed.cs")));
        var use = File.ReadAllText(ws.PathOf("Multi", "Use.cs"));
        Assert.Contains("=> feed.Fetch() + feed.Fetch();", use);
        Assert.Contains("=> feed.Fetch();", use);

        // Each file is reported once, and both frameworks' documents hold the merged text.
        Assert.Equal(
            [ws.PathOf("Multi", "Feed.cs"), ws.PathOf("Multi", "Use.cs")],
            result.Changes!.FilesModified.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var id in ws.Context.Solution.GetDocumentIdsWithFilePath(ws.PathOf("Multi", "Use.cs")))
            Assert.Equal(use, (await ws.Context.Solution.GetDocument(id)!.GetTextAsync(TestContext.Current.CancellationToken)).ToString());
    }

    [Fact]
    public async Task RenamingAType_MovesTheFileOfEveryTargetFramework()
    {
        using var ws = CreateWorkspace();

        var result = await RenameAsync(ws, "Feed.cs", "Feed", "Source");

        Assert.True(result.Success, result.Error?.Message);
        var newPath = ws.PathOf("Multi", "Source.cs");
        Assert.False(File.Exists(ws.PathOf("Multi", "Feed.cs")));
        Assert.Contains("public class Source", File.ReadAllText(newPath));
        Assert.Equal([newPath], result.Changes!.FilesCreated);
        Assert.Equal([ws.PathOf("Multi", "Use.cs")], result.Changes.FilesModified);
        Assert.Equal(2, ws.Context.Solution.GetDocumentIdsWithFilePath(newPath).Length);
        Assert.DoesNotContain(
            ws.Context.Solution.Projects.SelectMany(p => p.Documents),
            d => PathResolver.PathsEqual(d.FilePath!, ws.PathOf("Multi", "Feed.cs")));

        // The workspace is consistent: the next rename finds both branches in the moved file.
        var next = await RenameAsync(ws, "Source.cs", "Read", "Fetch");
        Assert.True(next.Success, next.Error?.Message);
        Assert.DoesNotContain("Read()", File.ReadAllText(newPath));
    }

    private static TestWorkspace CreateWorkspace() =>
        TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Multi", [("Feed.cs", Lf(Feed)), ("Use.cs", Lf(Use))], TargetFrameworks: ["net8", "net9"]));

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> RenameAsync(TestWorkspace ws, string file, string symbol, string newName) =>
        new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Multi", file), SymbolName = symbol, NewName = newName },
            TestContext.Current.CancellationToken);
}
