using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// move_type_to_namespace puts the type in exactly the target namespace, whatever declaration it
/// is in: a file-scoped one, a block, nested blocks (which used to give <c>Outer.Target</c>, reported
/// as success), nested blocks next to other members, or none at all (which used to be refused as
/// RoslynError). Each test checks the file as written, the namespace the compilation puts the type
/// in, and that the files on disk compile.
/// </summary>
public sealed class MoveTypeToNamespaceDeclarationShapeTests
{
    private const string Target = "App.Parts";

    [Fact]
    public async Task FileScopedNamespace_IsRenamed()
    {
        const string models = "namespace App;\n\npublic sealed class Widget\n{\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));

        await MoveAsync(ws, Target);

        Assert.Equal("namespace App.Parts;\n\npublic sealed class Widget\n{\n}\n", ws.ReadFile("Models.cs"));
        await AssertTypeInAsync(ws, "App.Parts.Widget", "App.Widget");
    }

    [Fact]
    public async Task BlockNamespace_IsRenamed()
    {
        const string models = "namespace App\n{\n    public sealed class Widget\n    {\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));

        await MoveAsync(ws, Target);

        Assert.Equal("namespace App.Parts\n{\n    public sealed class Widget\n    {\n    }\n}\n", ws.ReadFile("Models.cs"));
        await AssertTypeInAsync(ws, "App.Parts.Widget", "App.Widget");
    }

    [Fact]
    public async Task NestedBlocksHoldingOnlyTheType_BecomeOneBlockOfTheTarget()
    {
        const string models =
            "namespace Outer\n{\n    using System.Text;\n\n    namespace Inner\n    {\n        /// <summary>Builds.</summary>\n" +
            "        public sealed class Widget\n        {\n            public StringBuilder Build() => new();\n        }\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));

        await MoveAsync(ws, Target);

        Assert.Equal(
            "namespace App.Parts\n{\n    using System.Text;\n\n    /// <summary>Builds.</summary>\n" +
            "    public sealed class Widget\n    {\n        public StringBuilder Build() => new();\n    }\n}\n",
            ws.ReadFile("Models.cs"));
        await AssertTypeInAsync(ws, "App.Parts.Widget", "Outer.Inner.Widget");
    }

    [Fact]
    public async Task NestedBlockNextToOtherMembers_LeavesItsParent_ForABlockOfTheTarget()
    {
        // `using Models;` binds to Outer.Models only inside Outer, so the moved copy is qualified.
        const string models =
            "namespace Outer\n{\n    using System.Text;\n\n    namespace Models\n    {\n        public sealed class Part\n        {\n        }\n    }\n\n" +
            "    // The widget.\n    namespace Inner\n    {\n        using Models;\n\n        public sealed class Widget\n        {\n" +
            "            public Part? Part { get; set; }\n\n            public StringBuilder Text { get; } = new();\n        }\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));

        await MoveAsync(ws, "Other.Parts");

        Assert.Equal(
            "namespace Outer\n{\n    using System.Text;\n\n    namespace Models\n    {\n        public sealed class Part\n        {\n        }\n    }\n}\n\n" +
            "// The widget.\nnamespace Other.Parts\n{\n    using System.Text;\n    using Outer.Models;\n\n    public sealed class Widget\n    {\n" +
            "        public Part? Part { get; set; }\n\n        public StringBuilder Text { get; } = new();\n    }\n}\n",
            ws.ReadFile("Models.cs"));
        await AssertTypeInAsync(ws, "Other.Parts.Widget", "Outer.Inner.Widget");
    }

    [Fact]
    public async Task BlockNamespace_UsingRelativeToTheOldNamespace_IsQualified()
    {
        const string models =
            "namespace App.Models\n{\n    public sealed class Part\n    {\n    }\n}\n";
        const string widget =
            "namespace App.Feature\n{\n    using Models;\n\n    public sealed class Widget\n    {\n        public Part? Part { get; set; }\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models), ("Widget.cs", widget));

        await MoveAsync(ws, "Other", "Widget.cs");

        Assert.Equal(widget.Replace("App.Feature", "Other", StringComparison.Ordinal).Replace("using Models;", "using App.Models;", StringComparison.Ordinal), ws.ReadFile("Widget.cs"));
        await AssertTypeInAsync(ws, "Other.Widget", "App.Feature.Widget");
    }

    [Fact]
    public async Task GlobalNamespace_TheFilesOnlyType_GetsAFileScopedNamespace()
    {
        const string models = "using System.Text;\n\n/// <summary>Builds.</summary>\npublic sealed class Widget\n{\n    public StringBuilder Build() => new();\n}\n";
        const string uses = "namespace Consumers;\n\ninternal static class Uses\n{\n    public static object Make() => new Widget();\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models), ("Uses.cs", uses));

        await MoveAsync(ws, Target);

        Assert.Equal(
            "using System.Text;\n\nnamespace App.Parts;\n\n/// <summary>Builds.</summary>\npublic sealed class Widget\n{\n    public StringBuilder Build() => new();\n}\n",
            ws.ReadFile("Models.cs"));
        Assert.StartsWith("using App.Parts;", ws.ReadFile("Uses.cs"), StringComparison.Ordinal);
        await AssertTypeInAsync(ws, "App.Parts.Widget", "Widget");
    }

    [Fact]
    public async Task GlobalNamespace_NextToOtherTypes_GetsABlockAroundIt()
    {
        const string models =
            "public sealed class Widget\n{\n    public int Size => 1;\n}\n\npublic sealed class Other\n{\n    public Widget Make() => new();\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));

        await MoveAsync(ws, Target);

        Assert.Equal(
            "using App.Parts;\n\nnamespace App.Parts\n{\n    public sealed class Widget\n    {\n        public int Size => 1;\n    }\n}\n\n" +
            "public sealed class Other\n{\n    public Widget Make() => new();\n}\n",
            ws.ReadFile("Models.cs"));
        await AssertTypeInAsync(ws, "App.Parts.Widget", "Widget");
    }

    [Fact]
    public async Task GlobalNamespace_BeforeCSharp10_GetsABlock()
    {
        const string models = "public sealed class Widget\n{\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", models));
        var projectId = ws.Context.Solution.ProjectIds.Single();
        await ws.Context.UpdateSolutionInWriterAsync(
            s => s.WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.CSharp9)),
            TestContext.Current.CancellationToken);

        await MoveAsync(ws, Target);

        Assert.Equal("namespace App.Parts\n{\n    public sealed class Widget\n    {\n    }\n}\n", ws.ReadFile("Models.cs"));
        await AssertTypeInAsync(ws, "App.Parts.Widget", "Widget");
    }

    private static async Task MoveAsync(TestWorkspace ws, string targetNamespace, string file = "Models.cs")
    {
        var result = await new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = ws.PathOf(file), SymbolName = "Widget", TargetNamespace = targetNamespace },
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
    }

    /// <summary>The workspace's compilation has the type under its new name only, and the files on disk compile.</summary>
    private static async Task AssertTypeInAsync(TestWorkspace ws, string newName, string oldName)
    {
        var compilation = await ws.Context.Solution.Projects.Single().GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(compilation!.GetTypeByMetadataName(newName));
        Assert.Null(compilation.GetTypeByMetadataName(oldName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
