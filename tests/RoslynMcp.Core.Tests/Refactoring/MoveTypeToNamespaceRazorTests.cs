using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// move_type_to_namespace and the type's uses in Razor files (here <see cref="ViewGenerator"/>'s
/// stand-in): it updated the using directives of C# files only, and a Razor file's generated code
/// silently kept naming the old namespace. A Razor file that uses the type by its simple name now
/// gets a <c>@using</c> of the new namespace; one that writes the old namespace is reported; and
/// the errors the move leaves anywhere are reported as warnings.
/// </summary>
public sealed class MoveTypeToNamespaceRazorTests
{
    private const string Shapes = """
        namespace App;

        public static class Circle
        {
            public static double Area(double radius) => 3 * radius * radius;
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARazorFileUsingTheSimpleName_GetsAUsingOfTheNewNamespace_AfterItsUsings()
    {
        using var ws = Create("@using System.Text\n<h1>Shapes</h1>\n@Circle.Area(2)\n");

        var result = await MoveAsync(ws, preview: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("namespace App.Curves;", ws.ReadFile("Shapes.cs"));
        Assert.Equal("@using System.Text\n@using App.Curves\n<h1>Shapes</h1>\n@Circle.Area(2)\n", ws.ReadFile("Widget.razor"));
        Assert.Equal(1, result.UsingDirectivesAdded);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Preview_ListsTheRazorFile_AndWritesNothing()
    {
        using var ws = Create("<h1>Shapes</h1>\n@Circle.Area(2)\n");

        var result = await MoveAsync(ws, preview: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.PendingChanges!, c => c.File == ws.PathOf("Widget.razor") && c.Description == "Add '@using App.Curves' for 'Circle'");
        Assert.Equal("<h1>Shapes</h1>\n@Circle.Area(2)\n", ws.ReadFile("Widget.razor"));
    }

    [Fact]
    public async Task ARazorFileWritingTheOldNamespace_IsReported_WithTheErrorItGets()
    {
        using var ws = Create("<h1>Shapes</h1>\n@Circle.Area(1)\n@App.Circle.Area(2)\n");

        var result = await MoveAsync(ws, preview: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("@using App.Curves\n<h1>Shapes</h1>\n@Circle.Area(1)\n@App.Circle.Area(2)\n", ws.ReadFile("Widget.razor"));

        // Reported where it is now, below the added @using.
        Assert.Contains($"{ws.PathOf("Widget.razor")}(4,6): uses 'App.Circle', now 'App.Curves.Circle'; qualify it here by hand (it is written with its namespace).", result.Warnings!);
        Assert.Contains(result.Warnings!, w => w.StartsWith($"{ws.PathOf("Widget.razor")}(4,", StringComparison.Ordinal) && w.Contains(": error CS", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task AUsingInImports_IsEnough()
    {
        using var ws = TestWorkspace.Create(
            [("Shapes.cs", Shapes)],
            [],
            additionalFiles: [("Pages/Widget.razor", "@Circle.Area(2)\n"), ("_Imports.razor", "@using App.Curves\n")],
            generators: [ViewGenerator.Instance]);

        var result = await MoveAsync(ws, preview: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("@Circle.Area(2)\n", ws.ReadFile("Pages/Widget.razor"));
        Assert.Equal(0, result.UsingDirectivesAdded);
    }

    private static TestWorkspace Create(string view) =>
        TestWorkspace.Create([("Shapes.cs", Shapes)], [], additionalFiles: [("Widget.razor", view)], generators: [ViewGenerator.Instance]);

    private static Task<RefactoringResult> MoveAsync(TestWorkspace ws, bool preview) =>
        new Core.Refactoring.MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf("Shapes.cs"), SymbolName = "Circle", TargetNamespace = "App.Curves", Preview = preview },
            Ct);
}
