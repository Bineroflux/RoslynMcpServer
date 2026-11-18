using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// get_type_hierarchy's descendants came from FindDerivedClassesAsync only, which finds
/// nothing for an interface: <c>direction: Descendants</c> on an interface always returned
/// an empty list.
/// </summary>
public sealed class GetTypeHierarchyDescendantsTests
{
    private const string Shapes = """
        namespace App;

        public interface IShape { }
        public interface ISolid : IShape { }
        public class Circle : IShape { }
        public sealed class Ring : Circle { }
        public class Cube : ISolid { }
        public class Unrelated { }
        """;

    [Fact]
    public async Task Descendants_OfAnInterface_AreDerivedInterfacesAndImplementations()
    {
        var derived = await GetDescendantsAsync("IShape");

        Assert.Equal(["Circle", "Cube", "ISolid", "Ring"], derived);
    }

    [Fact]
    public async Task Descendants_OfAClass_AreStillItsDerivedClasses()
    {
        Assert.Equal(["Ring"], await GetDescendantsAsync("Circle"));
    }

    private static async Task<List<string>> GetDescendantsAsync(string typeName)
    {
        using var ws = TestWorkspace.Create(("Shapes.cs", Shapes));

        var result = await new GetTypeHierarchyOperation(ws.Context).ExecuteAsync(
            new GetTypeHierarchyParams { SourceFile = ws.PathOf("Shapes.cs"), SymbolName = typeName, Direction = "Descendants" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        return result.Data!.DerivedTypes.Select(t => t.TypeName).Order(StringComparer.Ordinal).ToList();
    }
}
