using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// The <c>transitive</c> option of find_implementations and get_type_hierarchy, and
/// get_type_hierarchy's <c>maxResults</c> on derived types.
/// </summary>
public sealed class TransitiveDescendantsTests
{
    private const string Shapes = """
        namespace App;

        public interface IShape { void Draw(); }
        public interface ISolid : IShape { }
        public interface IHeavySolid : ISolid { }

        public class Circle : IShape { public virtual void Draw() { } }
        public class Ring : Circle { public override void Draw() { } }
        public sealed class ThinRing : Ring { }
        public class Cube : ISolid { public void Draw() { } }

        public class Base { public void Draw() { } }
        public class Derived : Base, IShape { }
        public class Derived2 : Base, IShape { }

        public interface IRepo<T> { T Get(); }
        public class IntRepo : IRepo<int> { public int Get() => 0; }
        public class CachedRepo : IntRepo { }
        """;

    // A second project implementing the first one's interface, to cross compilations.
    private const string Plugin = """
        using App;

        namespace Plugins;

        public class PluginShape : IShape { public void Draw() { } }
        public class PluginRepo : IRepo<string> { public string Get() => ""; }
        """;

    [Fact]
    public async Task Hierarchy_OfAnInterface_NotTransitive_HasOnlyTypesListingIt()
    {
        using var ws = CreateWorkspace();

        var direct = await HierarchyAsync(ws, "IShape", transitive: false);
        var all = await HierarchyAsync(ws, "IShape", transitive: null);

        Assert.Equal(["Circle", "Derived", "Derived2", "ISolid", "PluginShape"], Names(direct.DerivedTypes));
        Assert.Equal(
            ["Circle", "Cube", "Derived", "Derived2", "IHeavySolid", "ISolid", "PluginShape", "Ring", "ThinRing"],
            Names(all.DerivedTypes));
    }

    [Fact]
    public async Task Hierarchy_OfAClass_NotTransitive_HasOnlyDirectSubclasses()
    {
        using var ws = CreateWorkspace();

        var direct = await HierarchyAsync(ws, "Circle", transitive: false);
        var all = await HierarchyAsync(ws, "Circle", transitive: true);

        Assert.Equal(["Ring"], Names(direct.DerivedTypes));
        Assert.Equal(["Ring", "ThinRing"], Names(all.DerivedTypes));
    }

    [Fact]
    public async Task Hierarchy_MaxResults_CapsDerivedTypes()
    {
        using var ws = CreateWorkspace();

        var capped = await HierarchyAsync(ws, "Circle", transitive: null, maxResults: 1);
        var uncapped = await HierarchyAsync(ws, "Circle", transitive: null);

        Assert.Single(capped.DerivedTypes);
        Assert.Equal(2, capped.DerivedTypesTotalCount);
        Assert.True(capped.DerivedTypesTruncated);
        Assert.Equal(2, uncapped.DerivedTypesTotalCount);
        Assert.False(uncapped.DerivedTypesTruncated);
    }

    [Fact]
    public async Task Hierarchy_MaxResults_CountsTheNonTransitiveSet()
    {
        using var ws = CreateWorkspace();

        var result = await HierarchyAsync(ws, "IShape", transitive: false, maxResults: 2);

        Assert.Equal(2, result.DerivedTypes.Count);
        Assert.Equal(5, result.DerivedTypesTotalCount);
        Assert.True(result.DerivedTypesTruncated);
    }

    [Fact]
    public async Task Hierarchy_MaxResults_LeavesAncestorsAndInterfacesAlone()
    {
        using var ws = CreateWorkspace();

        var result = await HierarchyAsync(ws, "ThinRing", transitive: null, maxResults: 1);

        Assert.Equal(["Circle", "Ring"], Names(result.BaseTypes));
        Assert.Equal(["IShape"], Names(result.Interfaces));
        Assert.Empty(result.DerivedTypes);
        Assert.Equal(0, result.DerivedTypesTotalCount);
    }

    [Fact]
    public async Task Hierarchy_MaxResultsBelowOne_IsRejected()
    {
        using var ws = CreateWorkspace();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => HierarchyAsync(ws, "Circle", transitive: null, maxResults: 0));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public async Task Implementations_OfAnInterface_NotTransitive_AreTypesListingIt()
    {
        using var ws = CreateWorkspace();

        var direct = await ImplementationsAsync(ws, "IShape", transitive: false);
        var all = await ImplementationsAsync(ws, "IShape", transitive: null);

        Assert.Equal(["App.Circle", "App.Derived", "App.Derived2", "Plugins.PluginShape"], Names(direct));
        Assert.Equal(
            ["App.Circle", "App.Cube", "App.Derived", "App.Derived2", "App.Ring", "App.ThinRing", "Plugins.PluginShape"],
            Names(all));
    }

    [Fact]
    public async Task Implementations_OfAnInterfaceMember_NotTransitive_AreTheDirectImplementersOnes()
    {
        // Derived and Derived2 both get Draw from Base: it is reported once. Cube implements
        // IShape only through ISolid, so its Draw is left out.
        using var ws = CreateWorkspace();

        var direct = await ImplementationsAsync(ws, "Draw", transitive: false, line: 3);
        var all = await ImplementationsAsync(ws, "Draw", transitive: null, line: 3);

        Assert.Equal(["App.Base.Draw()", "App.Circle.Draw()", "Plugins.PluginShape.Draw()"], Names(direct));
        Assert.Equal(direct.TotalCount, direct.Implementations.Count);
        Assert.Contains("App.Cube.Draw()", Names(all));
    }

    [Fact]
    public async Task Implementations_OfAGenericInterfaceMember_NotTransitive_FollowTheConstruction()
    {
        using var ws = CreateWorkspace();

        var direct = await ImplementationsAsync(ws, "Get", transitive: false, line: 16);

        Assert.Equal(["App.IntRepo.Get()", "Plugins.PluginRepo.Get()"], Names(direct));
    }

    [Fact]
    public async Task Implementations_NotTransitive_IsAppliedBeforeMaxResults()
    {
        using var ws = CreateWorkspace();

        var result = await ImplementationsAsync(ws, "IShape", transitive: false, maxResults: 1);

        Assert.Single(result.Implementations);
        Assert.Equal(4, result.TotalCount);
        Assert.True(result.Truncated);
    }

    private static TestWorkspace CreateWorkspace() =>
        TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("App", [("Shapes.cs", Shapes)]),
            new TestWorkspace.ProjectSpec("Plugins", [("Plugin.cs", Plugin)], References: ["App"]));

    private static async Task<GetTypeHierarchyResult> HierarchyAsync(
        TestWorkspace ws, string typeName, bool? transitive, int? maxResults = null)
    {
        var result = await new GetTypeHierarchyOperation(ws.Context).ExecuteAsync(
            new GetTypeHierarchyParams
            {
                SourceFile = ws.PathOf("App", "Shapes.cs"),
                SymbolName = typeName,
                Direction = "Both",
                Transitive = transitive,
                MaxResults = maxResults
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    private static async Task<FindImplementationsResult> ImplementationsAsync(
        TestWorkspace ws, string symbolName, bool? transitive, int? line = null, int? maxResults = null)
    {
        var result = await new FindImplementationsOperation(ws.Context).ExecuteAsync(
            new FindImplementationsParams
            {
                SourceFile = ws.PathOf("App", "Shapes.cs"),
                SymbolName = symbolName,
                Line = line,
                Transitive = transitive,
                MaxResults = maxResults
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    private static List<string> Names(IEnumerable<TypeHierarchyEntry> entries) =>
        entries.Select(e => e.TypeName).Order(StringComparer.Ordinal).ToList();

    private static List<string> Names(FindImplementationsResult result) =>
        result.Implementations.Select(i => i.FullyQualifiedName).Order(StringComparer.Ordinal).ToList();
}
