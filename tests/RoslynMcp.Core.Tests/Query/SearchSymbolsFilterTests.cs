using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// search_symbols' matching and scoping options: <c>exactMatch</c>, <c>caseSensitive</c>,
/// <c>projectPath</c>, <c>namespaceFilter</c> and <c>sourceFile</c>.
/// </summary>
public sealed class SearchSymbolsFilterTests
{
    private const string WidgetPart1 = """
        namespace App.Core;

        public partial class Widget
        {
            public void Render() { }
        }

        public class WidgetFactory { }

        public class widget_helper { }
        """;

    private const string WidgetPart2 = """
        namespace App.Core;

        public partial class Widget
        {
            public int Size { get; }
        }
        """;

    private const string Nested = """
        namespace App.Core.Nested;

        public class Widget { }
        """;

    private const string Utils = """
        namespace App.CoreUtils;

        public class Widget { }
        """;

    private const string Other = """
        namespace App.Other;

        public class Widget
        {
            public void Render() { }
        }

        public class WidgetView { }
        """;

    [Fact]
    public async Task WithoutOptions_MatchesACaseInsensitiveSubstring()
    {
        using var ws = CreateWorkspace();

        var result = await SearchAsync(ws, new SearchSymbolsParams { Query = "widget", KindFilter = "Class" });

        Assert.Equal(
            ["App.Core.Nested.Widget", "App.Core.Widget", "App.Core.WidgetFactory", "App.Core.widget_helper",
             "App.CoreUtils.Widget", "App.Other.Widget", "App.Other.WidgetView"],
            Names(result));
    }

    [Fact]
    public async Task ExactMatch_MatchesTheWholeName()
    {
        using var ws = CreateWorkspace();

        var result = await SearchAsync(ws, new SearchSymbolsParams { Query = "widget", ExactMatch = true });

        Assert.Equal(
            ["App.Core.Nested.Widget", "App.Core.Widget", "App.CoreUtils.Widget", "App.Other.Widget"],
            Names(result));
    }

    [Fact]
    public async Task CaseSensitive_RespectsCase()
    {
        using var ws = CreateWorkspace();

        var substring = await SearchAsync(ws, new SearchSymbolsParams { Query = "widget", CaseSensitive = true });
        var exact = await SearchAsync(ws, new SearchSymbolsParams { Query = "Widget", CaseSensitive = true, ExactMatch = true });
        var exactWrongCase = await SearchAsync(ws, new SearchSymbolsParams { Query = "widget", CaseSensitive = true, ExactMatch = true });

        Assert.Equal(["App.Core.widget_helper"], Names(substring));
        Assert.Equal(
            ["App.Core.Nested.Widget", "App.Core.Widget", "App.CoreUtils.Widget", "App.Other.Widget"],
            Names(exact));
        Assert.Empty(exactWrongCase.Symbols);
    }

    [Theory]
    [InlineData("App.Core")]
    [InlineData("app.core")]
    [InlineData("global::App.Core")]
    public async Task NamespaceFilter_MatchesTheNamespaceAndNestedOnesByWholeSegments(string namespaceFilter)
    {
        using var ws = CreateWorkspace();

        var result = await SearchAsync(ws, new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = namespaceFilter
        });

        Assert.Equal(["App.Core.Nested.Widget", "App.Core.Widget"], Names(result));
    }

    [Fact]
    public async Task NamespaceFilter_AppliesToMembersThroughTheirType()
    {
        using var ws = CreateWorkspace();

        var result = await SearchAsync(ws, new SearchSymbolsParams { Query = "Render", NamespaceFilter = "App.Other" });

        Assert.Equal(["App.Other.Widget.Render()"], Names(result));
    }

    [Fact]
    public async Task NamespaceFilter_FindsTheNamespaceItNames()
    {
        using var ws = CreateWorkspace();

        var result = await SearchAsync(ws, new SearchSymbolsParams
        {
            Query = "Core",
            KindFilter = "Namespace",
            NamespaceFilter = "App.Core"
        });

        Assert.Equal(["App.Core"], Names(result));
    }

    [Fact]
    public async Task ProjectPath_SearchesOnlyThatProject()
    {
        using var ws = CreateWorkspace();

        foreach (var spelling in EquivalentPaths.Of(ws.ProjectPathOf("Other")).Prepend(ws.ProjectPathOf("Other")))
        {
            var result = await SearchAsync(ws, new SearchSymbolsParams { Query = "Widget", ExactMatch = true, ProjectPath = spelling });

            Assert.Equal(["App.Other.Widget"], Names(result));
        }
    }

    [Fact]
    public async Task ProjectPath_CompilesOnlyThatProject()
    {
        // Compiling Other throws once a generator that can't be enumerated is attached to it.
        using var ws = CreateWorkspace();
        var other = ws.Context.Solution.Projects.Single(p => p.Name == "Other");
        await ws.Context.UpdateSolutionInWriterAsync(s => s.WithProjectAnalyzerReferences(other.Id, [new ThrowingAnalyzerReference()]), TestContext.Current.CancellationToken);

        var result = await SearchAsync(ws, new SearchSymbolsParams { Query = "Widget", ExactMatch = true, ProjectPath = ws.ProjectPathOf("Core") });
        await Assert.ThrowsAsync<RefactoringException>(() => new SearchSymbolsOperation(ws.Context).ExecuteAsync(
            new SearchSymbolsParams { Query = "Widget" }, TestContext.Current.CancellationToken));

        Assert.Equal(["App.Core.Nested.Widget", "App.Core.Widget", "App.CoreUtils.Widget"], Names(result));
    }

    [Fact]
    public async Task SourceFile_KeepsSymbolsDeclaredInIt_AnyPartOfAPartialType()
    {
        using var ws = CreateWorkspace();
        var part2 = ws.PathOf("Core", "Widget.Part2.cs");

        foreach (var spelling in EquivalentPaths.Of(part2).Prepend(part2))
        {
            var widget = await SearchAsync(ws, new SearchSymbolsParams { Query = "Widget", ExactMatch = true, SourceFile = spelling });
            var members = await SearchAsync(ws, new SearchSymbolsParams { Query = "e", KindFilter = "Property", SourceFile = spelling });
            var render = await SearchAsync(ws, new SearchSymbolsParams { Query = "Render", SourceFile = spelling });

            var entry = Assert.Single(widget.Symbols);
            Assert.Equal("App.Core.Widget", entry.FullyQualifiedName);
            Assert.EndsWith("Widget.Part2.cs", entry.File);
            Assert.Equal(3, entry.Line);
            Assert.Equal(["App.Core.Widget.Size"], Names(members));
            Assert.Empty(render.Symbols);
        }
    }

    [Fact]
    public async Task SourceFile_OutsideTheProjectPath_FindsNothing()
    {
        using var ws = CreateWorkspace();

        var result = await SearchAsync(ws, new SearchSymbolsParams
        {
            Query = "Widget",
            SourceFile = ws.PathOf("Core", "Widget.cs"),
            ProjectPath = ws.ProjectPathOf("Other")
        });

        Assert.Empty(result.Symbols);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task MaxResults_TruncatesTheFilteredSet()
    {
        using var ws = CreateWorkspace();

        var result = await SearchAsync(ws, new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = "App.Core",
            MaxResults = 1
        });

        Assert.Single(result.Symbols);
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task TotalCount_CountsASymbolOfSeveralTargetFrameworksOnce()
    {
        // Each framework compiles the same three classes. Duplicates used to be checked only
        // against the returned entries, so past maxResults they were counted again.
        using var ws = TestWorkspace.CreateSolution(new TestWorkspace.ProjectSpec(
            "Multi",
            [("Dups.cs", "public class Dup1 { } public class Dup2 { } public class Dup3 { }")],
            TargetFrameworks: ["net8", "net9"]));

        var result = await SearchAsync(ws, new SearchSymbolsParams { Query = "Dup", MaxResults = 1 });

        Assert.Single(result.Symbols);
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task SourceFile_NotInTheWorkspace_IsRejected()
    {
        using var ws = CreateWorkspace();
        var stray = Path.Combine(ws.Directory, "Stray.cs");
        File.WriteAllText(stray, "class Stray { }");

        var ex = await FailAsync(ws, new SearchSymbolsParams { Query = "Stray", SourceFile = stray });

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
    }

    [Theory]
    [InlineData("Missing.cs", ErrorCodes.SourceFileNotFound)]
    [InlineData("Core/Core.csproj", ErrorCodes.InvalidSourcePath)]
    public async Task SourceFile_MissingOrNotCSharp_IsRejected(string relativePath, string expectedCode)
    {
        using var ws = CreateWorkspace();

        var ex = await FailAsync(ws, new SearchSymbolsParams { Query = "Widget", SourceFile = Path.Combine(ws.Directory, relativePath) });

        Assert.Equal(expectedCode, ex.ErrorCode);
    }

    [Fact]
    public async Task ProjectPath_NotInTheWorkspace_IsRejected()
    {
        using var ws = CreateWorkspace();
        var stray = Path.Combine(ws.Directory, "Stray.csproj");
        File.WriteAllText(stray, "<Project />");

        var ex = await FailAsync(ws, new SearchSymbolsParams { Query = "Widget", ProjectPath = stray });

        Assert.Equal(ErrorCodes.ProjectNotInWorkspace, ex.ErrorCode);
    }

    private static TestWorkspace CreateWorkspace() =>
        TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec(
                "Core",
                [("Widget.cs", WidgetPart1), ("Widget.Part2.cs", WidgetPart2), ("Nested.cs", Nested), ("Utils.cs", Utils)]),
            new TestWorkspace.ProjectSpec("Other", [("Other.cs", Other)]));

    private static List<string> Names(SearchSymbolsResult result) =>
        result.Symbols.Select(s => s.FullyQualifiedName).Order(StringComparer.Ordinal).ToList();

    private static async Task<SearchSymbolsResult> SearchAsync(TestWorkspace ws, SearchSymbolsParams @params)
    {
        var result = await new SearchSymbolsOperation(ws.Context).ExecuteAsync(@params, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    private static Task<RefactoringException> FailAsync(TestWorkspace ws, SearchSymbolsParams @params) =>
        Assert.ThrowsAsync<RefactoringException>(() =>
            new SearchSymbolsOperation(ws.Context).ExecuteAsync(@params, TestContext.Current.CancellationToken));

    /// <summary>An analyzer reference whose generators can't be enumerated.</summary>
    private sealed class ThrowingAnalyzerReference : AnalyzerReference
    {
        public override string FullPath => "throwing.dll";
        public override string Display => "ThrowingAnalyzerReference";
        public override object Id => "throwing-id";

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];

        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages()
            => throw new InvalidOperationException("ThrowingAnalyzerReference test failure");
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
            => throw new InvalidOperationException("ThrowingAnalyzerReference test failure");
    }
}
