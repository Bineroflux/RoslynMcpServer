using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// The list-returning query tools put their results in a fixed order before truncating.
/// SymbolFinder searches in parallel and compilations bind concurrently, so the raw order
/// varied between calls and <c>maxResults</c> kept a different subset each time. Each test
/// loads the same code twice, with projects and documents in opposite order, and expects
/// identical output: sorted by file, line and column (find_references: declarations first).
/// </summary>
public sealed class ResultOrderTests
{
    private const string Shapes = """
        namespace App.Core;

        public interface IShape { void Draw(); }

        public class Widget : IShape
        {
            public virtual void Draw() { }
            public void Render() { int unusedInWidget; }
        }
        """;

    private const string CoreUserA = """
        namespace App.Core;

        internal class UserA
        {
            void M(Widget w) { w.Render(); int unusedInA; }
            void N(Widget w) => w.Render();
        }
        """;

    private const string CoreUserB = """
        namespace App.Core;

        internal class UserB : Widget
        {
            public override void Draw() { Render(); int unusedInB; }
        }
        """;

    private const string AppUserC = """
        using App.Core;

        namespace App;

        internal class Circle : IShape
        {
            public void Draw() => new Widget().Render();
        }
        """;

    private const string AppUserD = """
        using App.Core;

        namespace App;

        internal class Square : Widget
        {
            void M() { Render(); int unusedInD; }
        }

        internal sealed class SmallSquare : Square { }
        """;

    [Fact]
    public async Task FindReferences_IsOrderedTheSameWhateverTheLoadOrder_DeclarationsFirst()
    {
        var (forward, reversed) = await RunBothAsync(async ws =>
        {
            var result = await new FindReferencesOperation(ws.Context).ExecuteAsync(
                new FindReferencesParams { SourceFile = ws.PathOf("Core", "Shapes.cs"), SymbolName = "Render" },
                TestContext.Current.CancellationToken);
            return result.Data!.References.Select(r => $"{Relative(ws, r.File)}:{r.Line}:{r.Column}:{r.IsDefinition}").ToList();
        });

        Assert.Equal(forward, reversed);
        Assert.Equal(
            ["Core/Shapes.cs:8:17:True",
             "App/AppUserC.cs:7:40:False", "App/AppUserD.cs:7:16:False",
             "Core/CoreUserA.cs:5:26:False", "Core/CoreUserA.cs:6:27:False", "Core/CoreUserB.cs:5:35:False"],
            forward);
    }

    [Fact]
    public async Task FindReferences_MaxResults_KeepsTheFirstOfTheOrder()
    {
        var (forward, reversed) = await RunBothAsync(async ws =>
        {
            var result = await new FindReferencesOperation(ws.Context).ExecuteAsync(
                new FindReferencesParams
                {
                    SourceFile = ws.PathOf("Core", "Shapes.cs"),
                    SymbolName = "Render",
                    IncludeDeclaration = false,
                    MaxResults = 2
                },
                TestContext.Current.CancellationToken);
            return result.Data!.References.Select(r => $"{Relative(ws, r.File)}:{r.Line}").ToList();
        });

        Assert.Equal(["App/AppUserC.cs:7", "App/AppUserD.cs:7"], forward);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public async Task FindCallers_IsOrderedByCallSite()
    {
        var (forward, reversed) = await RunBothAsync(async ws =>
        {
            var result = await new FindCallersOperation(ws.Context).ExecuteAsync(
                new FindCallersParams { SourceFile = ws.PathOf("Core", "Shapes.cs"), SymbolName = "Render" },
                TestContext.Current.CancellationToken);
            return result.Data!.Callers.Select(c => $"{Relative(ws, c.File)}:{c.Line}:{c.Column}:{c.CallerName}").ToList();
        });

        Assert.Equal(forward, reversed);
        Assert.Equal(
            ["App/AppUserC.cs:7:40:Draw", "App/AppUserD.cs:7:16:M",
             "Core/CoreUserA.cs:5:26:M", "Core/CoreUserA.cs:6:27:N", "Core/CoreUserB.cs:5:35:Draw"],
            forward);
    }

    [Fact]
    public async Task FindImplementations_IsOrderedByLocation()
    {
        var (forward, reversed) = await RunBothAsync(async ws =>
        {
            var result = await new FindImplementationsOperation(ws.Context).ExecuteAsync(
                new FindImplementationsParams { SourceFile = ws.PathOf("Core", "Shapes.cs"), SymbolName = "IShape" },
                TestContext.Current.CancellationToken);
            return result.Data!.Implementations.Select(i => i.FullyQualifiedName).ToList();
        });

        Assert.Equal(forward, reversed);
        Assert.Equal(["App.Circle", "App.Square", "App.SmallSquare", "App.Core.UserB", "App.Core.Widget"], forward);
    }

    [Fact]
    public async Task TypeHierarchy_DerivedTypesAreOrderedByLocation()
    {
        var (forward, reversed) = await RunBothAsync(async ws =>
        {
            var result = await new GetTypeHierarchyOperation(ws.Context).ExecuteAsync(
                new GetTypeHierarchyParams { SourceFile = ws.PathOf("Core", "Shapes.cs"), SymbolName = "Widget", Direction = "Descendants" },
                TestContext.Current.CancellationToken);
            return result.Data!.DerivedTypes.Select(t => t.FullyQualifiedName).ToList();
        });

        Assert.Equal(forward, reversed);
        Assert.Equal(["App.Square", "App.SmallSquare", "App.Core.UserB"], forward);
    }

    [Fact]
    public async Task GetDiagnostics_IsOrderedByLocation()
    {
        var (forward, reversed) = await RunBothAsync(async ws =>
        {
            var result = await new GetDiagnosticsOperation(ws.Context).ExecuteAsync(
                new GetDiagnosticsParams(), TestContext.Current.CancellationToken);
            return result.Data!.Diagnostics.Select(d => $"{Relative(ws, d.File!)}:{d.Line}:{d.Id}").ToList();
        });

        Assert.Equal(forward, reversed);
        Assert.Equal(
            ["App/AppUserD.cs:7:CS0168", "Core/CoreUserA.cs:5:CS0168", "Core/CoreUserB.cs:5:CS0168", "Core/Shapes.cs:8:CS0168"],
            forward);
    }

    /// <summary>
    /// Runs <paramref name="query"/> against the fixture loaded in order and in reverse
    /// order (projects and the documents in each).
    /// </summary>
    private static async Task<(List<string> Forward, List<string> Reversed)> RunBothAsync(
        Func<TestWorkspace, Task<List<string>>> query)
    {
        TestWorkspace.ProjectSpec[] projects =
        [
            new("Core", [("Shapes.cs", Shapes), ("CoreUserA.cs", CoreUserA), ("CoreUserB.cs", CoreUserB)]),
            new("App", [("AppUserC.cs", AppUserC), ("AppUserD.cs", AppUserD)], References: ["Core"]),
        ];

        using var forward = TestWorkspace.CreateSolution(projects);
        using var reversed = TestWorkspace.CreateSolution(projects
            .Reverse()
            .Select(p => p with { Files = p.Files.Reverse().ToArray() })
            .ToArray());

        return (await query(forward), await query(reversed));
    }

    private static string Relative(TestWorkspace ws, string path) =>
        Path.GetRelativePath(ws.Directory, path).Replace('\\', '/');
}
