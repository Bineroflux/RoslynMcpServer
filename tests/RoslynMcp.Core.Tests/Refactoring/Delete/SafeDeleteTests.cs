using System.Collections;
using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Tests.Refactoring.Rename;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Delete;

/// <summary>
/// safe_delete removes a type or member only when nothing outside it references it, together with
/// its comments, every partial part and, for a type alone in its file, the file.
/// </summary>
public sealed class SafeDeleteTests
{
    [Fact]
    public async Task UnusedMember_GoesWithItsDocComment_AndNoBlankLinesPileUp()
    {
        const string source = """
            namespace App;

            internal class Calculator
            {
                // Section comment, separated by a blank line: stays.

                /// <summary>Unused.</summary>
                // and a plain comment
                private int Helper(int x) => x;

                public int Add(int a, int b) => a + b;
            }
            """;
        using var ws = TestWorkspace.Create(("Calculator.cs", Lf(source)));

        var result = await DeleteAsync(ws, "Calculator.cs", "Helper");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace App;

            internal class Calculator
            {
                // Section comment, separated by a blank line: stays.

                public int Add(int a, int b) => a + b;
            }
            """), ws.ReadFile("Calculator.cs"));
        Assert.Equal([ws.PathOf("Calculator.cs")], result.Changes!.FilesModified);
        Assert.Equal("Helper", result.Symbol!.Name);
        Assert.Equal(9, result.Symbol.PreviousLocation!.Line);
        Assert.Null(result.Warnings);
    }

    [Theory]
    [InlineData("First", "    public void Second() { }\n\n    public void Third() { }\n")]
    [InlineData("Second", "    public void First() { }\n\n    public void Third() { }\n")]
    [InlineData("Third", "    public void First() { }\n\n    public void Second() { }\n")]
    public async Task FirstMiddleOrLastMember_LeavesOneBlankLineBetweenTheRest(string name, string expectedBody)
    {
        const string source = "namespace App;\n\ninternal class C\n{\n    public void First() { }\n\n    public void Second() { }\n\n    public void Third() { }\n}\n";
        using var ws = TestWorkspace.Create(("C.cs", source));

        var result = await DeleteAsync(ws, "C.cs", name);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("namespace App;\n\ninternal class C\n{\n" + expectedBody + "}\n", ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task RegionDirectivesAroundADeletedMember_Stay()
    {
        const string source = "internal class C\n{\n    #region Helpers\n    private void Helper() { }\n    #endregion\n\n    public void Run() { }\n}\n";
        using var ws = TestWorkspace.Create(("C.cs", source));

        var result = await DeleteAsync(ws, "C.cs", "Helper");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("internal class C\n{\n    #region Helpers\n    #endregion\n\n    public void Run() { }\n}\n", ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task RecursionAndSelfReferences_DoNotCount()
    {
        const string source = """
            internal class Node
            {
                private Node? _next;
                public Node? Next => _next;
                private int Depth(Node? node) => node == null ? 0 : 1 + Depth(node.Next);
            }

            internal class Keep { }
            """;
        using var ws = TestWorkspace.Create(("Node.cs", Lf(source)));

        var method = await DeleteAsync(ws, "Node.cs", "Depth");
        var type = await DeleteAsync(ws, "Node.cs", "Node");

        Assert.True(method.Success, method.Error?.Message);
        Assert.True(type.Success, type.Error?.Message);
        Assert.Equal("internal class Keep { }\n", ws.ReadFile("Node.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Referenced_FailsWithTheReferences_AndChangesNothing()
    {
        const string source = """
            using System;

            internal class Service
            {
                /// <summary>See <see cref="Run"/>.</summary>
                public void Run() { }

                public string Name() => nameof(Run);
            }

            internal static class Caller
            {
                public static void Go(Service s) { s.Run(); s.Run(); }
            }
            """;
        using var ws = TestWorkspace.Create(("Service.cs", Lf(source)));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Service.cs", "Run"));

        Assert.Equal(ErrorCodes.SymbolStillReferenced, ex.ErrorCode);
        Assert.Contains("still referenced in 3 places", ex.Message);
        Assert.Equal(3, ex.Details!["totalCount"]);
        Assert.Equal(0, ex.Details["crefCount"]);
        var references = ((IEnumerable)ex.Details["references"]).Cast<BlockingReference>().ToList();
        Assert.Equal(["nameof", "reference", "reference"], references.Select(r => r.Kind));
        Assert.Equal([8, 13, 13], references.Select(r => r.Line));
        Assert.Equal("public static void Go(Service s) { s.Run(); s.Run(); }", references[1].Snippet);
        Assert.Equal(Lf(source), ws.ReadFile("Service.cs"));
    }

    [Fact]
    public async Task ACrefElsewhere_BlocksTheDeletion_AndIsReportedAsSuch()
    {
        const string source = """
            internal class Old { }

            /// <summary>Replaces <see cref="Old"/>.</summary>
            internal class New { }
            """;
        using var ws = TestWorkspace.Create(("Types.cs", Lf(source)));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Types.cs", "Old"));

        Assert.Equal(ErrorCodes.SymbolStillReferenced, ex.ErrorCode);
        Assert.Contains("(1 in documentation-comment crefs)", ex.Message);
        Assert.Equal(1, ex.Details!["crefCount"]);
        Assert.Equal("cref", Assert.Single(((IEnumerable)ex.Details["references"]).Cast<BlockingReference>()).Kind);
    }

    [Fact]
    public async Task AType_IsUsedThroughItsMembers()
    {
        const string source = """
            internal static class Extensions
            {
                public static int Doubled(this int x) => x * 2;
            }

            internal static class Outer
            {
                internal static class Inner { public const int Max = 3; }
            }

            internal static class Use
            {
                public static int Run() => 2.Doubled() + Outer.Inner.Max;
            }
            """;
        using var ws = TestWorkspace.Create(("Use.cs", Lf(source)));

        var extensions = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Use.cs", "Extensions"));
        var inner = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Use.cs", "Inner"));

        Assert.Equal(ErrorCodes.SymbolStillReferenced, extensions.ErrorCode);
        Assert.Contains("(13,", extensions.Message);
        Assert.Equal(ErrorCodes.SymbolStillReferenced, inner.ErrorCode);
    }

    [Fact]
    public async Task PartialType_LosesEveryPart_AndFilesItWasAloneInAreDeleted()
    {
        using var ws = TestWorkspace.Create(
            ("Part1.cs", "namespace App;\n\ninternal partial class Widget\n{\n    private int _a;\n}\n"),
            ("Part2.cs", "using System;\n\nnamespace App\n{\n    internal partial class Widget\n    {\n        private int _b;\n    }\n\n    internal class Other { }\n}\n"));

        var result = await DeleteAsync(ws, "Part1.cs", "Widget");

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(File.Exists(ws.PathOf("Part1.cs")));
        Assert.Equal("using System;\n\nnamespace App\n{\n    internal class Other { }\n}\n", ws.ReadFile("Part2.cs"));
        Assert.Equal([ws.PathOf("Part1.cs")], result.Changes!.FilesDeleted);
        Assert.Equal([ws.PathOf("Part2.cs")], result.Changes.FilesModified);
        Assert.Null(ws.Context.GetDocumentByPath(ws.PathOf("Part1.cs")));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task AFileWithAGlobalUsingOrAnAssemblyAttribute_IsKept()
    {
        using var ws = TestWorkspace.Create(
            ("Global.cs", "global using System;\n\ninternal class Lonely { }\n"),
            ("Attr.cs", "[assembly: System.CLSCompliant(false)]\n\ninternal class Alone { }\n"));

        await DeleteAsync(ws, "Global.cs", "Lonely");
        await DeleteAsync(ws, "Attr.cs", "Alone");

        Assert.Equal("global using System;\n", ws.ReadFile("Global.cs"));
        Assert.Equal("[assembly: System.CLSCompliant(false)]\n", ws.ReadFile("Attr.cs"));
    }

    [Theory]
    [InlineData("a", "    private int b, c;\n")]
    [InlineData("b", "    private int a, c;\n")]
    [InlineData("c", "    private int a, b;\n")]
    public async Task OneVariableOfSeveral_IsRemovedWithItsComma(string name, string expectedField)
    {
        using var ws = TestWorkspace.Create(("F.cs", "internal class F\n{\n    private int a, b, c;\n}\n"));

        var result = await DeleteAsync(ws, "F.cs", name);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("internal class F\n{\n" + expectedField + "}\n", ws.ReadFile("F.cs"));
    }

    [Fact]
    public async Task EventFields_OneOfSeveralOrAlone()
    {
        const string source = "using System;\n\ninternal class E\n{\n    public event EventHandler? A, B;\n\n    public event EventHandler? C;\n}\n";
        using var ws = TestWorkspace.Create(("E.cs", source));

        var a = await DeleteAsync(ws, "E.cs", "A");
        var c = await DeleteAsync(ws, "E.cs", "C");

        Assert.True(a.Success, a.Error?.Message);
        Assert.True(c.Success, c.Error?.Message);
        Assert.Equal("using System;\n\ninternal class E\n{\n    public event EventHandler? B;\n}\n", ws.ReadFile("E.cs"));
    }

    [Theory]
    [InlineData("Area", "implements 'IShape.Area()'")]
    [InlineData("Draw", "overrides 'Base.Draw()'")]
    [InlineData("ToString", "overrides 'object.ToString()'")]
    public async Task OverridesAndImplementations_AreRefused(string name, string relation)
    {
        using var ws = CreateFamily();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Shapes.cs", name, line: 16));

        Assert.Equal(ErrorCodes.MemberInFamily, ex.ErrorCode);
        Assert.Contains(relation, ex.Message);
    }

    [Theory]
    [InlineData(3, "Area", "is implemented by 'Square.Area()'")]
    [InlineData(8, "Draw", "is overridden by 'Square.Draw()'")]
    public async Task MembersThatAreOverriddenOrImplemented_AreRefused(int line, string name, string relation)
    {
        using var ws = CreateFamily();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Shapes.cs", name, line: line));

        Assert.Equal(ErrorCodes.MemberInFamily, ex.ErrorCode);
        Assert.Contains(relation, ex.Message);
    }

    [Theory]
    [InlineData("Square", 14, "static constructor")]
    [InlineData("Square", 15, "finalizer")]
    [InlineData("op_Implicit", 18, "implicit conversion")]
    public async Task ImplicitlyUsedMembers_AreRefused(string name, int line, string reason)
    {
        using var ws = CreateFamily();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Shapes.cs", name, line: line));

        Assert.Equal(ErrorCodes.CannotDelete, ex.ErrorCode);
        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    [InlineData("B", "enum E\n{\n    A,\n    B,\n    C\n}\n", null)]
    [InlineData("C", "enum E\n{\n    A,\n    B,\n    C\n}\n", "enum E\n{\n    A,\n    B,\n}\n")]
    [InlineData("B", "enum E\n{\n    A = 1,\n    B = 2,\n    C = 3\n}\n", "enum E\n{\n    A = 1,\n    C = 3\n}\n")]
    [InlineData("A", "enum E\n{\n    None = 0,\n    A = 0,\n    B\n}\n", "enum E\n{\n    None = 0,\n    B\n}\n")]
    [InlineData("B", "enum E { A, B, C = 5 }\n", "enum E { A, C = 5 }\n")]
    public async Task EnumMembers_AreDeletedOnlyWhenNoOtherValueChanges(string name, string source, string? expected)
    {
        using var ws = TestWorkspace.Create(("E.cs", source));

        if (expected == null)
        {
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "E.cs", name));
            Assert.Equal(ErrorCodes.CannotDelete, ex.ErrorCode);
            Assert.Contains("C would get other values", ex.Message);
            Assert.Equal(source, ws.ReadFile("E.cs"));
        }
        else
        {
            var result = await DeleteAsync(ws, "E.cs", name);
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(expected, ws.ReadFile("E.cs"));
        }
    }

    [Fact]
    public async Task ABaseConstructorCalledImplicitly_IsReferenced_AnUnusedOneGoes_WithANoteForTheLast()
    {
        const string source = """
            internal class Base
            {
                public Base() { }
                public Base(int x) { }
            }

            internal class Derived : Base
            {
                public Derived(string s) { }
            }

            internal class Single
            {
                private Single(int x) { }
            }
            """;
        using var ws = TestWorkspace.Create(("Ctors.cs", Lf(source)));

        var implicitCall = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "Ctors.cs", "Base", line: 3));
        var last = await DeleteAsync(ws, "Ctors.cs", "Single", line: 14);
        var unused = await DeleteAsync(ws, "Ctors.cs", "Base", line: 4);

        Assert.Equal(ErrorCodes.SymbolStillReferenced, implicitCall.ErrorCode);
        Assert.Equal("implicit", Assert.Single(((IEnumerable)implicitCall.Details!["references"]).Cast<BlockingReference>()).Kind);
        Assert.True(unused.Success, unused.Error?.Message);
        Assert.DoesNotContain("Base(int x)", ws.ReadFile("Ctors.cs"));
        Assert.Equal(["'Single' has no constructor left, so the compiler gives it a public parameterless one."], last.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ErrorsLeftBehind_AreReturnedAsWarnings()
    {
        // Nothing names the parameterless constructor, but a new() constraint needs it.
        const string source = """
            internal class Widget
            {
                public Widget() { }
                public Widget(int size) { }
            }

            internal static class Factory
            {
                public static T Make<T>() where T : new() => new T();
                public static object Use() => Make<Widget>();
            }
            """;
        using var ws = TestWorkspace.Create(("Widget.cs", Lf(source)));

        var result = await DeleteAsync(ws, "Widget.cs", "Widget", line: 3);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.Warnings!, w => w.Contains("error CS0310", StringComparison.Ordinal));
        WarningAssert.AreTheCompilerErrors(ws, result.Warnings);
    }

    [Fact]
    public async Task Preview_ListsTheRemovals_AndWritesNothing()
    {
        using var ws = TestWorkspace.Create(
            ("Alone.cs", "namespace App;\n\ninternal class Alone { }\n"),
            ("Two.cs", "namespace App;\n\ninternal class Keep { }\n\ninternal class Drop\n{\n    private int _x;\n}\n"));

        var alone = await DeleteAsync(ws, "Alone.cs", "Alone", preview: true);
        var drop = await DeleteAsync(ws, "Two.cs", "Drop", preview: true);

        Assert.True(alone.Preview);
        Assert.Equal(ChangeKind.Delete, Assert.Single(alone.PendingChanges!).ChangeType);
        var change = Assert.Single(drop.PendingChanges!);
        Assert.Equal(ChangeKind.Modify, change.ChangeType);
        Assert.Equal("Delete class 'Drop'", change.Description);
        Assert.Equal("internal class Drop", change.BeforeSnippet);
        Assert.Equal(4, change.StartLine);
        Assert.Equal(8, change.EndLine);
        Assert.True(File.Exists(ws.PathOf("Alone.cs")));
        Assert.Contains("class Drop", ws.ReadFile("Two.cs"));
    }

    [Fact]
    public async Task UsingsOnlyTheDeletedCodeUsed_Go_AndThoseUnusedBeforeStay()
    {
        const string source = """
            using System.Text;
            using System.Collections.Concurrent;
            using System.Net;
            using SB = System.Text.StringBuilder;
            using static System.Math;

            namespace App
            {
                using System.Globalization;

                internal class Worker
                {
                    public int Run() => 1;

                    private string Build() => new StringBuilder().Append(CultureInfo.InvariantCulture).ToString() + new SB() + Abs(-1);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Worker.cs", Lf(source)));

        var preview = await DeleteAsync(ws, "Worker.cs", "Build", preview: true);
        var result = await DeleteAsync(ws, "Worker.cs", "Build");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using System.Collections.Concurrent;
            using System.Net;

            namespace App
            {
                internal class Worker
                {
                    public int Run() => 1;
                }
            }
            """), ws.ReadFile("Worker.cs"));
        Assert.Equal(4, result.UsingDirectivesRemoved);

        // The preview lists them, with the lines they are on.
        Assert.Equal(4, preview.UsingDirectivesRemoved);
        Assert.Equal(
            ["1: using System.Text;", "4: using SB = System.Text.StringBuilder;", "5: using static System.Math;", "9: using System.Globalization;"],
            preview.PendingChanges!.Where(c => c.Description!.StartsWith("Remove 'using", StringComparison.Ordinal)).Select(c => $"{c.StartLine}: {c.BeforeSnippet}"));
    }

    [Fact]
    public async Task AUsingStillUsedByAnotherTargetFramework_Stays()
    {
        const string source = """
            using System.Text;

            namespace App;

            internal class Worker
            {
                public int Run() => 1;

                private string Build() => new StringBuilder().ToString();

            #if NET9
                public string Other() => new StringBuilder().ToString();
            #endif
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Multi", [("Worker.cs", Lf(source))], TargetFrameworks: ["net8", "net9"]));

        var result = await new SafeDeleteOperation(ws.Context).ExecuteAsync(
            new SafeDeleteParams { SourceFile = ws.PathOf("Multi", "Worker.cs"), SymbolName = "Build" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith("using System.Text;\n", File.ReadAllText(ws.PathOf("Multi", "Worker.cs")), StringComparison.Ordinal);
        Assert.Equal(0, result.UsingDirectivesRemoved);
    }

    [Fact]
    public async Task CrlfFileWithBom_KeepsBoth()
    {
        const string source = "internal class C\r\n{\r\n    private void Unused() { }\r\n\r\n    public void Run() { }\r\n}\r\n";
        using var ws = TestWorkspace.Create(("C.cs", source));
        File.WriteAllText(ws.PathOf("C.cs"), source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await DeleteAsync(ws, "C.cs", "Unused");

        Assert.True(result.Success, result.Error?.Message);
        var bytes = File.ReadAllBytes(ws.PathOf("C.cs"));
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("internal class C\r\n{\r\n    public void Run() { }\r\n}\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task AcrossProjects_AReferenceInAnotherProjectBlocks()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Lib.cs", "namespace Lib;\n\npublic static class Api\n{\n    public static int Used() => 1;\n\n    public static int Unused() => 2;\n}\n")]),
            new TestWorkspace.ProjectSpec("App", [("App.cs", "namespace App;\n\npublic static class Client\n{\n    public static int Run() => Lib.Api.Used();\n}\n")], References: ["Lib"]));

        var used = await Assert.ThrowsAsync<RefactoringException>(() =>
            Delete(ws.Context, ws.PathOf("Lib", "Lib.cs"), "Used"));
        var unused = await Delete(ws.Context, ws.PathOf("Lib", "Lib.cs"), "Unused");

        Assert.Equal(ErrorCodes.SymbolStillReferenced, used.ErrorCode);
        Assert.Contains(ws.PathOf("App", "App.cs") + "(5,", used.Message);
        Assert.True(unused.Success, unused.Error?.Message);
        Assert.Equal("namespace Lib;\n\npublic static class Api\n{\n    public static int Used() => 1;\n}\n", File.ReadAllText(ws.PathOf("Lib", "Lib.cs")));
    }

    [Fact]
    public async Task NotATypeOrMember_IsRejected()
    {
        using var ws = TestWorkspace.Create(("C.cs", "internal class C\n{\n    public void M(int value) { var local = value; }\n}\n"));

        var parameter = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "C.cs", "value", line: 3, column: 23));
        var local = await Assert.ThrowsAsync<RefactoringException>(() => DeleteAsync(ws, "C.cs", "local", line: 3, column: 36));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, parameter.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidSymbolKind, local.ErrorCode);
    }

    [Fact]
    public async Task MsBuildWorkspace_DeletingATypeAloneInItsFile_DeletesTheFile_AndTheCacheMatchesMsBuild()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;
        var unusedFile = Path.Combine(working.ProjectDir, "Services", "Unused.cs");
        File.WriteAllText(unusedFile, "namespace TestProject.Services;\n\ninternal sealed class Unused\n{\n}\n");
        using var provider = new MSBuildWorkspaceProvider();

        RefactoringResult result;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            result = await Delete(ctx, unusedFile, "Unused");
        }

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(File.Exists(unusedFile));
        Assert.Equal([unusedFile], result.Changes!.FilesDeleted);

        WorkspaceContext cached;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            cached = ctx;
            Assert.Null(ctx.GetDocumentByPath(unusedFile));
            var compilation = await ctx.Solution.Projects.Single().GetCompilationAsync(ct);
            Assert.Empty(compilation!.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        Assert.False(await cached.ReconcileProjectDocumentsAsync(Path.Combine(working.ProjectDir, "TestProject.csproj"), ct));
    }

    private static TestWorkspace CreateFamily() => TestWorkspace.Create(("Shapes.cs", Lf("""
        internal interface IShape
        {
            double Area();
        }

        internal abstract class Base
        {
            public virtual void Draw() { }
        }

        internal sealed class Square : Base, IShape
        {
            public int Side { get; set; }
            static Square() { }
            ~Square() { }
            public double Area() => Side; public override void Draw() { } public override string ToString() => "";
            public void Use() { }
            public static implicit operator int(Square s) => s.Side;
        }
        """)));

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> DeleteAsync(
        TestWorkspace ws,
        string file,
        string name,
        int? line = null,
        int? column = null,
        bool preview = false) =>
        Delete(ws.Context, ws.PathOf(file), name, line, column, preview);

    private static Task<RefactoringResult> Delete(
        WorkspaceContext context,
        string file,
        string name,
        int? line = null,
        int? column = null,
        bool preview = false) =>
        new SafeDeleteOperation(context).ExecuteAsync(
            new SafeDeleteParams { SourceFile = file, SymbolName = name, Line = line, Column = column, Preview = preview },
            TestContext.Current.CancellationToken);
}
