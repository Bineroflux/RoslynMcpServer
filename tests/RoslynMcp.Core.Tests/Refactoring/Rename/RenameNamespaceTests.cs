using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// rename_namespace renames a source namespace and its sub-namespaces solution-wide: declarations
/// of every shape and every kind of name referring to it, also when the depth changes.
/// </summary>
public sealed class RenameNamespaceTests
{
    [Fact]
    public async Task EveryKindOfReference_IsRewritten_AndRelativeNamesStay()
    {
        using var ws = TestWorkspace.Create(
            ("Model.cs", "namespace Old\n{\n    public class Foo { }\n\n    public static class Util { public static int One() => 1; }\n\n    public class Holder { public Sub.Item? Item; }\n}\n"),
            ("Sub.cs", "namespace Old.Sub;\n\npublic class Item { }\n"),
            ("Use.cs", """
                global using Old.Sub;
                using Old;
                using static Old.Util;
                using F = Old.Foo;
                using O = Old;

                namespace Client;

                /// <summary>Uses <see cref="Old.Foo"/>.</summary>
                public class Consumer
                {
                    public Foo A = new();
                    public Old.Foo B = new Old.Foo();
                    public global::Old.Foo C = new();
                    public F D = new();
                    public O.Foo E = new();
                    public Item G = new();
                    public int H = One() + Old.Util.One();
                    public string I = nameof(Old.Sub);
                }
                """.ReplaceLineEndings("\n") + "\n"));

        var result = await RenameAsync(ws, "Old", "New");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace New\n{\n    public class Foo { }\n\n    public static class Util { public static int One() => 1; }\n\n    public class Holder { public Sub.Item? Item; }\n}\n", ws.ReadFile("Model.cs"));
        Assert.Equal("namespace New.Sub;\n\npublic class Item { }\n", ws.ReadFile("Sub.cs"));
        Assert.Equal("""
            global using New.Sub;
            using New;
            using static New.Util;
            using F = New.Foo;
            using O = New;

            namespace Client;

            /// <summary>Uses <see cref="New.Foo"/>.</summary>
            public class Consumer
            {
                public Foo A = new();
                public New.Foo B = new New.Foo();
                public global::New.Foo C = new();
                public F D = new();
                public O.Foo E = new();
                public Item G = new();
                public int H = One() + New.Util.One();
                public string I = nameof(New.Sub);
            }
            """.ReplaceLineEndings("\n") + "\n", ws.ReadFile("Use.cs"));
        Assert.Equal(11, result.ReferencesUpdated);
        Assert.Equal("New", result.Symbol!.FullyQualifiedName);
        Assert.Equal("Old", result.Symbol.PreviousNamespace);
        Assert.Equal(Contracts.Enums.SymbolKind.Namespace, result.Symbol.Kind);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ADeeperName_RewritesDeclarationsAndPartiallyQualifiedNames()
    {
        using var ws = TestWorkspace.Create(
            ("Old/Widget.cs", "namespace App.Old;\n\npublic class Widget { }\n"),
            ("Old/Deep/Part.cs", "namespace App.Old.Deep;\n\npublic class Part { public Widget? Owner; }\n"),
            ("App.cs", "namespace App\n{\n    public class Shell\n    {\n        public Old.Widget? W;\n        public Old.Deep.Part? P;\n        public App.Old.Widget? Q;\n    }\n}\n"));

        var result = await RenameAsync(ws, "App.Old", "Company.Core.New");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace Company.Core.New;\n\npublic class Widget { }\n", ws.ReadFile("Old/Widget.cs"));
        Assert.Equal("namespace Company.Core.New.Deep;\n\npublic class Part { public Widget? Owner; }\n", ws.ReadFile("Old/Deep/Part.cs"));
        Assert.Equal(
            "namespace App\n{\n    public class Shell\n    {\n        public Company.Core.New.Widget? W;\n        public Company.Core.New.Deep.Part? P;\n        public Company.Core.New.Widget? Q;\n    }\n}\n",
            ws.ReadFile("App.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task PartialNamesUnderTheSameParent_AreSimplified()
    {
        using var ws = TestWorkspace.Create(
            ("Lib.cs", "namespace Company.Old\n{\n    public class Widget { }\n}\n"),
            ("App.cs", "namespace Company\n{\n    public class Shell\n    {\n        public Old.Widget? W;\n    }\n}\n"));

        var result = await RenameAsync(ws, "Company.Old", "Company.Renamed");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace Company.Renamed\n{\n    public class Widget { }\n}\n", ws.ReadFile("Lib.cs"));
        Assert.Equal("namespace Company\n{\n    public class Shell\n    {\n        public Renamed.Widget? W;\n    }\n}\n", ws.ReadFile("App.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("A.C", "namespace A\n{\n    namespace C\n    {\n        public class Foo { }\n    }\n}\n")]
    [InlineData("X.Y", "namespace X\n{\n    namespace Y\n    {\n        public class Foo { }\n    }\n}\n")]
    [InlineData("X.Y.Z", "namespace X.Y\n{\n    namespace Z\n    {\n        public class Foo { }\n    }\n}\n")]
    [InlineData("A.B.C", "namespace A\n{\n    namespace B.C\n    {\n        public class Foo { }\n    }\n}\n")]
    public async Task NestedBlocks_RenameTheirOwnPart_OrTheEnclosingBlockWhenItHoldsNothingElse(string newName, string expected)
    {
        using var ws = TestWorkspace.Create(
            ("Foo.cs", "namespace A\n{\n    namespace B\n    {\n        public class Foo { }\n    }\n}\n"),
            ("Use.cs", "public class Use { public A.B.Foo? F; }\n"));

        var result = await RenameAsync(ws, "A.B", newName);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, ws.ReadFile("Foo.cs"));
        Assert.Equal($"public class Use {{ public {newName}.Foo? F; }}\n", ws.ReadFile("Use.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task DottedDeclarations_AndSubNamespaces_AreRenamedByPrefix()
    {
        using var ws = TestWorkspace.Create(
            ("Inner.cs", "namespace A.B.C.D\n{\n    public class Inner { }\n}\n"),
            ("Use.cs", "using A.B.C.D;\n\npublic class Use { public Inner? I; public A.B.C.D.Inner? J; }\n"));

        var result = await RenameAsync(ws, "A.B", "X");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace X.C.D\n{\n    public class Inner { }\n}\n", ws.ReadFile("Inner.cs"));
        Assert.Equal("using X.C.D;\n\npublic class Use { public Inner? I; public X.C.D.Inner? J; }\n", ws.ReadFile("Use.cs"));
    }

    [Theory]
    [InlineData("X.Y")]
    [InlineData("A")]
    public async Task AnEnclosingBlockThatDeclaresMore_OrAMerge_IsRefused(string newName)
    {
        const string source = "namespace A\n{\n    public class Other { }\n\n    namespace B\n    {\n        public class Foo { }\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Foo.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => RenameAsync(ws, "A.B", newName));

        Assert.Equal(ErrorCodes.NamespaceBlockShared, ex.ErrorCode);
        Assert.Contains(newName == "A" ? "Other" : "'Other'", ex.Message);
        Assert.Equal(source, ws.ReadFile("Foo.cs"));
    }

    [Fact]
    public async Task MergingNestedBlocksThatHoldNothingElse_IsRefused()
    {
        using var ws = TestWorkspace.Create(("Foo.cs", "namespace A\n{\n    namespace B\n    {\n        public class Foo { }\n    }\n}\n"));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => RenameAsync(ws, "A.B", "Z"));

        Assert.Equal(ErrorCodes.NamespaceBlockShared, ex.ErrorCode);
        Assert.Contains("merge", ex.Message);
    }

    [Theory]
    [InlineData("New", "'Old.Foo' would collide with the existing 'New.Foo'")]
    [InlineData("Taken", "namespace 'Taken' would collide with the type 'Taken'")]
    [InlineData("Taken.Inner", "namespace 'Taken' would collide with the type 'Taken'")]
    public async Task Collisions_AreRefused(string newName, string message)
    {
        using var ws = TestWorkspace.Create(
            ("Old.cs", "namespace Old { public class Foo { } }\n"),
            ("New.cs", "namespace New { public class Foo { } }\n"),
            ("Taken.cs", "public class Taken { }\n"));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => RenameAsync(ws, "Old", newName));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains(message, ex.Message);
        Assert.Equal("namespace Old { public class Foo { } }\n", ws.ReadFile("Old.cs"));
    }

    [Fact]
    public async Task MergingIntoAnExistingNamespaceWithoutCollisions_IsAllowed()
    {
        using var ws = TestWorkspace.Create(
            ("Old.cs", "namespace Old { public class Foo { } }\n"),
            ("New.cs", "namespace New { public class Bar { public Old.Foo? F; } }\n"));

        var result = await RenameAsync(ws, "Old", "New");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace New { public class Bar { public New.Foo? F; } }\n", ws.ReadFile("New.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("Old", "1Bad", ErrorCodes.InvalidNamespace)]
    [InlineData("Old", "A..B", ErrorCodes.InvalidNamespace)]
    [InlineData("Old", "A.class", ErrorCodes.ReservedKeyword)]
    [InlineData("Old", "global::Old", ErrorCodes.SameLocation)]
    [InlineData("Missing", "New", ErrorCodes.SymbolNotFound)]
    [InlineData("Ol", "New", ErrorCodes.SymbolNotFound)]
    public async Task BadNames_AreRejected(string oldName, string newName, string code)
    {
        using var ws = TestWorkspace.Create(("Old.cs", "namespace Old { public class Foo { } }\n"));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => RenameAsync(ws, oldName, newName));

        Assert.Equal(code, ex.ErrorCode);
    }

    [Fact]
    public async Task GeneratedFilesAndProjectLevelUsings_AreLeftAlone_AndReported()
    {
        using var ws = TestWorkspace.Create(
            [
                ("Model.cs", "namespace App.Old;\n\npublic class Foo { }\n"),
                ("Use.cs", "namespace App;\n\npublic class Use { public Foo? F; }\n"),
                ("obj/Debug/TestProject.GlobalUsings.g.cs", "// <auto-generated/>\nglobal using global::App.Old;\n")
            ],
            [],
            defaultNamespace: "App");
        File.WriteAllText(ws.ProjectFilePath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RootNamespace>App.Old</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <Using Include="App.Old" />
              </ItemGroup>
            </Project>
            """);

        var result = await RenameAsync(ws, "App.Old", "App.New");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("// <auto-generated/>\nglobal using global::App.Old;\n", ws.ReadFile("obj/Debug/TestProject.GlobalUsings.g.cs"));
        var warnings = result.Warnings!;
        Assert.Contains(warnings, w => w.Contains("<Using Include=\"App.Old\" />", StringComparison.Ordinal) && w.Contains("'App.New'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("<RootNamespace>App.Old</RootNamespace>", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("TestProject.GlobalUsings.g.cs' is generated and was not changed", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("error CS0246", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, warnings);
    }

    [Fact]
    public async Task AcrossProjects_TheReferencingProjectIsUpdated()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Lib.cs", "namespace Lib.Old;\n\npublic class Api { }\n")]),
            new TestWorkspace.ProjectSpec("App", [("App.cs", "using Lib.Old;\n\nnamespace App;\n\npublic class Client { public Api? A; public Lib.Old.Api? B; }\n")], References: ["Lib"]));

        var result = await RenameAsync(ws, "Lib.Old", "Lib.Current");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace Lib.Current;\n\npublic class Api { }\n", File.ReadAllText(ws.PathOf("Lib", "Lib.cs")));
        Assert.Equal("using Lib.Current;\n\nnamespace App;\n\npublic class Client { public Api? A; public Lib.Current.Api? B; }\n", File.ReadAllText(ws.PathOf("App", "App.cs")));
    }

    [Fact]
    public async Task NamesThatReliedOnTheEnclosingNamespace_AreQualified_WhenTheDeclarationMovesElsewhere()
    {
        using var ws = TestWorkspace.Create(
            ("Company.cs", "namespace Company\n{\n    public class Helper { public static int Count = 1; }\n\n    public class TagAttribute : System.Attribute { }\n}\n\nnamespace Company.Sibling\n{\n    public class Part { }\n}\n"),
            ("Shadow.cs", "namespace Other\n{\n    public class Helper { }\n}\n"),
            ("Widget.cs", """
                namespace Company.Old
                {
                    using Sibling;

                    /// <summary>Has a <see cref="Sibling.Part"/> and a <see cref="Helper"/>.</summary>
                    [Tag]
                    public class Widget
                    {
                        public Sibling.Part? P;
                        public Part? Q;
                        public Helper Make() => new Helper();
                        public int N = Helper.Count;
                        public System.Collections.Generic.List<Helper> All = new();
                        public Inner.Gadget? G;
                    }
                }
                """.ReplaceLineEndings("\n") + "\n"),
            ("Gadget.cs", "namespace Company.Old.Inner;\n\npublic class Gadget { public Sibling.Part? P; public Widget? W; }\n"),
            ("Nested.cs", "namespace Company\n{\n    namespace Old.Deep\n    {\n        public class Nested { public Sibling.Part? P; public Helper? H; }\n    }\n}\n"));

        var result = await RenameAsync(ws, "Company.Old", "Other.New");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("""
            namespace Other.New
            {
                using Company.Sibling;

                /// <summary>Has a <see cref="Company.Sibling.Part"/> and a <see cref="Company.Helper"/>.</summary>
                [Company.Tag]
                public class Widget
                {
                    public Company.Sibling.Part? P;
                    public Part? Q;
                    public Company.Helper Make() => new Company.Helper();
                    public int N = Company.Helper.Count;
                    public System.Collections.Generic.List<Company.Helper> All = new();
                    public Inner.Gadget? G;
                }
            }
            """.ReplaceLineEndings("\n") + "\n", ws.ReadFile("Widget.cs"));
        Assert.Equal("namespace Other.New.Inner;\n\npublic class Gadget { public Company.Sibling.Part? P; public Widget? W; }\n", ws.ReadFile("Gadget.cs"));
        Assert.Equal("namespace Other\n{\n    namespace New.Deep\n    {\n        public class Nested { public Company.Sibling.Part? P; public Company.Helper? H; }\n    }\n}\n", ws.ReadFile("Nested.cs"));
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task NamesThatAnExistingTargetNamespaceWouldShadow_AreQualified()
    {
        using var ws = TestWorkspace.Create(
            ("Company.cs", "namespace Company\n{\n    public class Helper { }\n}\n\nnamespace Company.New\n{\n    public class Helper { }\n}\n"),
            ("Old.cs", "namespace Company.Old\n{\n    public class Widget { public Helper? H; }\n}\n"));

        var result = await RenameAsync(ws, "Company.Old", "Company.New");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace Company.New\n{\n    public class Widget { public Company.Helper? H; }\n}\n", ws.ReadFile("Old.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task AMultiTargetedFile_IsRewrittenInTheIfBranchesOfEveryFramework()
    {
        const string use = """
            #if NET9
            using Old;
            #endif

            namespace App;

            public static class Use
            {
            #if NET9
                public static Foo Make() => new Old.Foo();
            #else
                public static global::Old.Foo Make() => new Old.Foo();
            #endif

                public static string Name => nameof(Old.Foo);
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec(
                "Multi",
                [("Foo.cs", "#if NET8\nnamespace Old\n#else\nnamespace Old\n#endif\n{\n    public class Foo { }\n}\n"), ("Use.cs", use.ReplaceLineEndings("\n") + "\n")],
                TargetFrameworks: ["net8", "net9"]));

        var result = await RenameAsync(ws, "Old", "Brand.New");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("#if NET8\nnamespace Brand.New\n#else\nnamespace Brand.New\n#endif\n{\n    public class Foo { }\n}\n", File.ReadAllText(ws.PathOf("Multi", "Foo.cs")));
        Assert.Equal("""
            #if NET9
            using Brand.New;
            #endif

            namespace App;

            public static class Use
            {
            #if NET9
                public static Foo Make() => new Brand.New.Foo();
            #else
                public static global::Brand.New.Foo Make() => new Brand.New.Foo();
            #endif

                public static string Name => nameof(Brand.New.Foo);
            }
            """.ReplaceLineEndings("\n") + "\n", File.ReadAllText(ws.PathOf("Multi", "Use.cs")));

        // Each name is counted once, however many frameworks compile it; nothing is left behind.
        Assert.Equal(5, result.ReferencesUpdated);
        Assert.Equal([ws.PathOf("Multi", "Foo.cs"), ws.PathOf("Multi", "Use.cs")], result.Changes!.FilesModified.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Null(result.Warnings);
        foreach (var path in new[] { ws.PathOf("Multi", "Foo.cs"), ws.PathOf("Multi", "Use.cs") })
        {
            var onDisk = File.ReadAllText(path);
            foreach (var id in ws.Context.Solution.GetDocumentIdsWithFilePath(path))
                Assert.Equal(onDisk, (await ws.Context.Solution.GetDocument(id)!.GetTextAsync(TestContext.Current.CancellationToken)).ToString());
        }
    }

    [Fact]
    public async Task Preview_ListsTheChanges_AndWritesNothing()
    {
        using var ws = TestWorkspace.Create(
            ("Model.cs", "namespace Old;\n\npublic class Foo { }\n"),
            ("Use.cs", "using Old;\n\npublic class Use { public Foo? F; }\n"));

        var result = await RenameAsync(ws, "Old", "New", preview: true);

        Assert.True(result.Preview);
        Assert.Equal(2, result.PendingChanges!.Count);
        var use = result.PendingChanges.Single(c => c.File == ws.PathOf("Use.cs"));
        Assert.Equal("using Old;", use.BeforeSnippet);
        Assert.Equal("using New;", use.AfterSnippet);
        Assert.Equal(1, use.StartLine);
        Assert.Equal("namespace Old;\n\npublic class Foo { }\n", ws.ReadFile("Model.cs"));
    }

    [Fact]
    public async Task CrlfFileWithBom_KeepsBoth()
    {
        const string source = "namespace Old\r\n{\r\n    public class Foo { }\r\n}\r\n";
        using var ws = TestWorkspace.Create(("Model.cs", source));
        File.WriteAllText(ws.PathOf("Model.cs"), source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await RenameAsync(ws, "Old", "Brand.New");

        Assert.True(result.Success, result.Error?.Message);
        var bytes = File.ReadAllBytes(ws.PathOf("Model.cs"));
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("namespace Brand.New\r\n{\r\n    public class Foo { }\r\n}\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task UpdateFolders_MovesEachFileWhoseTypesAllMove_AndRemovesEmptiedFolders()
    {
        using var ws = CreateFolderWorkspace();

        var result = await RenameAsync(ws, "App.Old", "App.New", updateFolders: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);
        var widget = Path.Combine(ws.Directory, "New", "Widget.cs");
        var gadget = Path.Combine(ws.Directory, "New", "Sub", "Gadget.cs");
        Assert.Equal("namespace App.New;\n\npublic class Widget { }\n", File.ReadAllText(widget));
        Assert.Equal("namespace App.New.Sub;\n\npublic class Gadget { }\n", File.ReadAllText(gadget));
        Assert.False(Directory.Exists(Path.Combine(ws.Directory, "Old")));
        Assert.Equal([widget, gadget], result.Changes!.FilesCreated);
        Assert.Contains(ws.PathOf("Old/Widget.cs"), result.Changes.FilesDeleted);
        Assert.Contains(ws.PathOf("Mixed.cs"), result.Changes.FilesModified);
        Assert.DoesNotContain(result.Changes.FilesModified, f => f.Contains("Widget", StringComparison.Ordinal));

        // The mixed file stays, and says why.
        Assert.Contains(result.Warnings!, w => w.Contains("Mixed.cs' was not moved: 'Mixed.cs' also declares types outside 'App.Old'", StringComparison.Ordinal));

        // The cached solution matches disk.
        Assert.NotNull(ws.Context.GetDocumentByPath(widget));
        Assert.Null(ws.Context.GetDocumentByPath(ws.PathOf("Old/Widget.cs")));
        Assert.Equal(new[] { "New", "Sub" }, ws.Context.GetDocumentByPath(gadget)!.Folders);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task UpdateFolders_OnlyMovesFilesThatSitInTheFolderOfTheirNamespace()
    {
        using var ws = TestWorkspace.Create(
            [
                ("Old/Widget.cs", "namespace App.Old;\n\npublic class Widget { }\n"),
                ("Old/Nested.cs", "namespace App.Old.Inner;\n\npublic class Nested { }\n"),
                ("Elsewhere/Stray.cs", "namespace App.Old;\n\npublic class Stray { }\n"),
                ("Root.cs", "namespace App.Old;\n\npublic class AtTheRoot { }\n")
            ],
            [],
            defaultNamespace: "App");
        File.WriteAllText(ws.ProjectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await RenameAsync(ws, "App.Old", "App.New", updateFolders: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);
        Assert.Equal([Path.Combine(ws.Directory, "New", "Widget.cs")], result.Changes!.FilesCreated);
        Assert.Equal("namespace App.New.Inner;\n\npublic class Nested { }\n", ws.ReadFile("Old/Nested.cs"));
        Assert.Equal("namespace App.New;\n\npublic class Stray { }\n", ws.ReadFile("Elsewhere/Stray.cs"));
        Assert.True(File.Exists(ws.PathOf("Root.cs")));
        Assert.True(Directory.Exists(Path.Combine(ws.Directory, "Old")));
        Assert.Contains(result.Warnings!, w => w.Contains("Nested.cs' was not moved: it is not in 'Old" + Path.DirectorySeparatorChar + "Inner', the folder of its namespace 'App.Old.Inner'", StringComparison.Ordinal));
        Assert.Contains(result.Warnings!, w => w.Contains("Stray.cs' was not moved: it is not in 'Old', the folder of its namespace 'App.Old'", StringComparison.Ordinal));
        Assert.Contains(result.Warnings!, w => w.Contains("Root.cs' was not moved: it is not in 'Old'", StringComparison.Ordinal));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task UpdateFolders_WarningsNameTheMovedFilesWhereTheyAreNow()
    {
        using var ws = TestWorkspace.Create(
            [
                ("Old/Widget.cs", "namespace App.Old;\n\npublic class Widget { public Gadget? G; }\n"),
                ("Old/Sub/Gadget.cs", "namespace App.Old.Sub;\n\npublic class Gadget { }\n"),
                ("obj/Debug/TestProject.GlobalUsings.g.cs", "// <auto-generated/>\nglobal using global::App.Old.Sub;\n")
            ],
            [],
            defaultNamespace: "App");
        File.WriteAllText(ws.ProjectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await RenameAsync(ws, "App.Old", "App.New", updateFolders: true);

        Assert.True(result.Success, result.Error?.Message);
        var moved = Path.Combine(ws.Directory, "New", "Widget.cs");
        Assert.Contains(result.Warnings!, w => w.StartsWith(moved + "(3,", StringComparison.Ordinal) && w.Contains("error CS0246", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings!, w => w.StartsWith(ws.PathOf("Old/Widget.cs"), StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task UpdateFolders_Preview_ReportsTheMoves_AndMovesNothing()
    {
        using var ws = CreateFolderWorkspace();

        var result = await RenameAsync(ws, "App.Old", "App.New", updateFolders: true, preview: true);

        Assert.Contains(result.PendingChanges!, c => c.ChangeType == ChangeKind.Create && c.File == Path.Combine(ws.Directory, "New", "Widget.cs"));
        Assert.Contains(result.PendingChanges!, c => c.ChangeType == ChangeKind.Delete && c.File == ws.PathOf("Old/Widget.cs"));
        Assert.True(File.Exists(ws.PathOf("Old/Widget.cs")));
        Assert.False(Directory.Exists(Path.Combine(ws.Directory, "New")));
    }

    [Fact]
    public async Task UpdateFolders_OutsideTheRootNamespace_LeavesTheFilesWithAWarning()
    {
        using var ws = CreateFolderWorkspace();

        var result = await RenameAsync(ws, "App.Old", "Other.New", updateFolders: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(File.Exists(ws.PathOf("Old/Widget.cs")));
        Assert.Contains(result.Warnings!, w => w.Contains("'Other.New' is not inside the project's root namespace 'App'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MsBuildWorkspace_ProjectLevelUsingIsReported_AndTheFolderMoveIsConfirmed()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;
        var projectFile = Path.Combine(working.ProjectDir, "TestProject.csproj");
        File.WriteAllText(projectFile, File.ReadAllText(projectFile).Replace(
            "</Project>",
            "  <ItemGroup>" + Environment.NewLine + "    <Using Include=\"TestProject.Services\" />" + Environment.NewLine + "  </ItemGroup>" + Environment.NewLine + "</Project>"));
        var generatedUsings = Directory.Exists(Path.Combine(working.ProjectDir, "obj"))
            ? Directory.GetFiles(Path.Combine(working.ProjectDir, "obj"), "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
            : [];
        using var provider = new MSBuildWorkspaceProvider();

        RefactoringResult result;
        string[] generatedAfterLoad;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            generatedAfterLoad = ctx.Solution.Projects.SelectMany(p => p.Documents)
                .Select(d => d.FilePath!)
                .Where(p => p.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var before = generatedAfterLoad.ToDictionary(p => p, File.ReadAllText);

            result = await new RenameNamespaceOperation(ctx).ExecuteAsync(
                new RenameNamespaceParams { OldNamespace = "TestProject.Services", NewNamespace = "TestProject.Logic", UpdateFolders = true },
                ct);

            foreach (var (path, text) in before)
                Assert.Equal(text, File.ReadAllText(path));
        }

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);
        Assert.Contains(result.Warnings!, w => w.Contains("<Using Include=\"TestProject.Services\" />", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(working.ProjectDir, "Logic", "OrderService.cs")));
        Assert.False(Directory.Exists(Path.Combine(working.ProjectDir, "Services")));
        Assert.Contains("namespace TestProject.Logic;", File.ReadAllText(Path.Combine(working.ProjectDir, "Logic", "UserService.cs")));
        if (generatedAfterLoad.Length > 0)
        {
            Assert.Contains(result.Warnings!, w => w.Contains("is generated and was not changed", StringComparison.Ordinal));
            Assert.Contains(result.Warnings!, w => w.Contains(": error CS0", StringComparison.Ordinal));
        }

        WorkspaceContext cached;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            cached = ctx;
            Assert.NotNull(ctx.GetDocumentByPath(Path.Combine(working.ProjectDir, "Logic", "OrderService.cs")));
        }

        Assert.False(await cached.ReconcileProjectDocumentsAsync(projectFile, ct));
    }

    private static TestWorkspace CreateFolderWorkspace()
    {
        var ws = TestWorkspace.Create(
            [
                ("Old/Widget.cs", "namespace App.Old;\n\npublic class Widget { }\n"),
                ("Old/Sub/Gadget.cs", "namespace App.Old.Sub;\n\npublic class Gadget { }\n"),
                ("Mixed.cs", "namespace App.Old\n{\n    public class Mixed { }\n}\n\nnamespace App\n{\n    public class Plain { public Old.Widget? W; }\n}\n")
            ],
            [],
            defaultNamespace: "App");
        File.WriteAllText(ws.ProjectFilePath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <RootNamespace>App</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        return ws;
    }

    private static Task<RefactoringResult> RenameAsync(
        TestWorkspace ws,
        string oldName,
        string newName,
        bool updateFolders = false,
        bool preview = false) =>
        new RenameNamespaceOperation(ws.Context).ExecuteAsync(
            new RenameNamespaceParams { OldNamespace = oldName, NewNamespace = newName, UpdateFolders = updateFolders, Preview = preview },
            TestContext.Current.CancellationToken);
}
