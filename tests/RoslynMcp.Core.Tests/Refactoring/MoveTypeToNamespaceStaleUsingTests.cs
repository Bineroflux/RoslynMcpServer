using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// When move_type_to_namespace moves the last type out of a namespace, the namespace no longer
/// exists, and every <c>using</c> of it failed with CS0234: the operation kept the old using in the
/// files it updated ("harmless"), and never looked at the others. Imports of the old namespace that
/// stop binding are now removed (also <c>global using</c>), static usings and aliases of the moved
/// type or the namespace follow them to the new namespace, and everything that still binds, or did
/// not bind before either, stays.
/// </summary>
public sealed class MoveTypeToNamespaceStaleUsingTests
{
    private const string Fancy =
        "namespace App.Fancy;\n\npublic sealed class Star\n{\n    public static int Points => 5;\n\n    public sealed class Ray\n    {\n    }\n}\n";

    private const string Uses =
        "using App.Fancy;\n\nnamespace App.Use;\n\ninternal static class Uses\n{\n    public static object Make() => new Star();\n}\n";

    private const string Unrelated =
        "using System.Text;\n// Fancy things.\nusing App.Fancy;\n\nnamespace App.Other;\n\ninternal static class Unrelated\n{\n    public static StringBuilder Text() => new();\n}\n";

    private const string Statics =
        "using static App.Fancy.Star;\nusing RayAlias = App.Fancy.Star.Ray;\nusing F = App.Fancy;\n\nnamespace App.Stat;\n\n" +
        "internal static class Statics\n{\n    public static int P => Points;\n\n    public static object R() => new RayAlias();\n\n    public static object S() => new F.Star();\n}\n";

    [Fact]
    public async Task LastTypeMovedOut_ImportsAreRemoved_StaticUsingsAndAliasesFollow()
    {
        using var ws = TestWorkspace.Create(
            ("Fancy.cs", Fancy), ("Uses.cs", Uses), ("Unrelated.cs", Unrelated), ("Statics.cs", Statics), ("GlobalUsings.cs", "global using App.Fancy;\n"));

        var result = await MoveStarAsync(ws);

        Assert.Equal(3, result.UsingDirectivesRemoved);
        Assert.Equal(
            "using App.Parts;\n\nnamespace App.Use;\n\ninternal static class Uses\n{\n    public static object Make() => new Star();\n}\n",
            ws.ReadFile("Uses.cs"));
        Assert.Equal(
            "using System.Text;\n\nnamespace App.Other;\n\ninternal static class Unrelated\n{\n    public static StringBuilder Text() => new();\n}\n",
            ws.ReadFile("Unrelated.cs"));
        Assert.Equal("", ws.ReadFile("GlobalUsings.cs"));

        var statics = ws.ReadFile("Statics.cs");
        Assert.StartsWith("using static App.Parts.Star;\nusing RayAlias = App.Parts.Star.Ray;\nusing F = App.Parts;\n", statics, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Fancy", statics, StringComparison.Ordinal);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task NamespaceStillHoldsTypes_ImportsStay()
    {
        using var ws = TestWorkspace.Create(
            ("Fancy.cs", Fancy), ("Comet.cs", "namespace App.Fancy;\n\npublic sealed class Comet\n{\n}\n"), ("Uses.cs", Uses), ("Unrelated.cs", Unrelated));

        var result = await MoveStarAsync(ws);

        Assert.Equal(0, result.UsingDirectivesRemoved);
        Assert.StartsWith("using App.Fancy;\nusing App.Parts;\n", ws.ReadFile("Uses.cs"), StringComparison.Ordinal);
        Assert.Equal(Unrelated, ws.ReadFile("Unrelated.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task UsingThatDidNotBindBefore_Stays()
    {
        const string broken = "using App.Fancy.Gone;\n\nnamespace App.Broken;\n\ninternal static class Broken\n{\n}\n";
        using var ws = TestWorkspace.Create(("Fancy.cs", Fancy), ("Uses.cs", Uses), ("Broken.cs", broken));

        await MoveStarAsync(ws);

        Assert.Equal(broken, ws.ReadFile("Broken.cs"));
        Assert.All(ws.GetDiskCompilationErrors(), error => Assert.Contains("Broken.cs", error, StringComparison.Ordinal));
    }

    private static async Task<RefactoringResult> MoveStarAsync(TestWorkspace ws)
    {
        var result = await new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf("Fancy.cs"), SymbolName = "Star", TargetNamespace = "App.Parts" },
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result;
    }
}
