using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// <see cref="SymbolIdentity"/> compares symbols of different compilations: one declaration
/// seen through each target framework's compilation (or through another project's) is several
/// symbol instances that <see cref="SymbolEqualityComparer"/> tells apart.
/// </summary>
public sealed class SymbolIdentityTests
{
    private const string Source = """
        namespace App;

        public interface IShape
        {
            double Area();
        }

        public class Square : IShape
        {
            public double Area() => 4;

            public System.Collections.Generic.List<int> Ints() => new();

            public System.Collections.Generic.List<string> Strings() => new();
        }
        """;

    [Fact]
    public void TheSameDeclarationInTwoCompilations_IsTheSame_InAnotherAssemblyItIsNot()
    {
        var net10 = Compile("App", Source);
        var net11 = Compile("App", Source);
        var other = Compile("Other", Source);
        var square10 = net10.GetTypeByMetadataName("App.Square")!;
        var square11 = net11.GetTypeByMetadataName("App.Square")!;
        var squareOther = other.GetTypeByMetadataName("App.Square")!;

        Assert.False(SymbolEqualityComparer.Default.Equals(square10, square11));
        Assert.True(SymbolIdentity.SameDeclaration(square10, square11));
        Assert.True(SymbolIdentity.SameDeclaration(square10.GetMembers("Area")[0], square11.GetMembers("Area")[0]));
        Assert.False(SymbolIdentity.SameDeclaration(square10, squareOther));
        Assert.False(SymbolIdentity.SameDeclaration(square10.GetMembers("Area")[0], net11.GetTypeByMetadataName("App.IShape")!.GetMembers("Area")[0]));
        Assert.True(SymbolIdentity.SameDeclaration(net10.Assembly, net11.Assembly));
        Assert.True(SymbolIdentity.SameDeclaration(square10.ContainingNamespace, squareOther.ContainingNamespace));
        Assert.Equal(SymbolIdentity.Key(square10), SymbolIdentity.Key(square11));
        Assert.NotEqual(SymbolIdentity.Key(square10), SymbolIdentity.Key(squareOther));

        var set = new HashSet<ISymbol>(SymbolIdentity.Declarations) { square10, square11, squareOther };
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void Constructions_AreOneDeclaration_ButDifferentTypes()
    {
        var net10 = Compile("App", Source);
        var net11 = Compile("App", Source);
        var ints10 = ((IMethodSymbol)net10.GetTypeByMetadataName("App.Square")!.GetMembers("Ints")[0]).ReturnType;
        var ints11 = ((IMethodSymbol)net11.GetTypeByMetadataName("App.Square")!.GetMembers("Ints")[0]).ReturnType;
        var strings11 = ((IMethodSymbol)net11.GetTypeByMetadataName("App.Square")!.GetMembers("Strings")[0]).ReturnType;

        Assert.True(SymbolIdentity.SameDeclaration(ints10, strings11));
        Assert.True(SymbolIdentity.SameType(ints10, ints11));
        Assert.False(SymbolIdentity.SameType(ints10, strings11));
    }

    [Fact]
    public async Task FindImplementations_ListsAnImplementationOnce_ThoughEachTargetFrameworkSeesIt()
    {
        const string shapes = """
            namespace Lib;

            public interface IShape
            {
                double Area();
            }
            """;
        const string squares = """
            using Lib;

            namespace App;

            public class Square : IShape
            {
                public double Area() => 4;
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Shapes.cs", shapes)]),
            new TestWorkspace.ProjectSpec("App", [("Squares.cs", squares)], References: ["Lib"], TargetFrameworks: ["net10", "net11"]));

        var result = await new FindImplementationsOperation(ws.Context).ExecuteAsync(
            new FindImplementationsParams { SourceFile = ws.PathOf("Lib", "Shapes.cs"), SymbolName = "Area", Transitive = false },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var implementation = Assert.Single(result.Data!.Implementations);
        Assert.Equal("App.Square.Area()", implementation.FullyQualifiedName);
    }

    [Fact]
    public async Task PullMembersUp_DoesNotTakeTheTypeInAnotherTargetFramework_ForAnotherDerivedType()
    {
        const string shapes = """
            namespace Lib;

            public class Shape
            {
            }
            """;
        const string squares = """
            using Lib;

            namespace App;

            public class Square : Shape
            {
                public int Sides() => 4;
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Shapes.cs", shapes)]),
            new TestWorkspace.ProjectSpec("App", [("Squares.cs", squares)], References: ["Lib"], TargetFrameworks: ["net10", "net11"]));

        var result = await new Core.Refactoring.Hierarchy.PullMembersUpOperation(ws.Context).ExecuteAsync(
            new PullMembersUpParams { SourceFile = ws.PathOf("App", "Squares.cs"), TypeName = "Square", Members = ["Sides"] },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Sides() => 4;", File.ReadAllText(ws.PathOf("Lib", "Shapes.cs")));
        Assert.DoesNotContain(result.Warnings ?? [], w => w.Contains("CS0108", StringComparison.Ordinal));
    }

    private static CSharpCompilation Compile(string assemblyName, string source) =>
        CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
