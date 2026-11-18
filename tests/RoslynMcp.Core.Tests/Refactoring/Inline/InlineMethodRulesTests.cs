using System.Runtime.Loader;
using System.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// What inline_method refuses, which calls it leaves alone, picking one call, preview, encodings
/// and calls from another project.
/// </summary>
public sealed class InlineMethodRulesTests
{
    [Theory]
    [InlineData("public int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);", "Fact", "recursive")]
    [InlineData("public virtual int Get() => 1;", "Get", "virtual")]
    [InlineData("public int Read(ref int value) => value;", "Read", "ref, out or in")]
    [InlineData("public int Parse(out int value) { value = 1; return 1; }", "Parse", "ref, out or in")]
    [InlineData("public async System.Threading.Tasks.Task<int> Load() => await System.Threading.Tasks.Task.FromResult(1);", "Load", "async")]
    [InlineData("public System.Collections.Generic.IEnumerable<int> Items() { yield return 1; }", "Items", "iterator")]
    [InlineData("public int Two() { var a = 1; return a + 1; }", "Two", "a single return statement")]
    [InlineData("public void Early(int a) { if (a > 0) return; System.Console.WriteLine(a); }", "Early", "returns early")]
    [InlineData("[System.Diagnostics.Conditional(\"DEBUG\")] public void Trace() => System.Console.WriteLine(1);", "Trace", "Conditional")]
    [InlineData("public unsafe int Size() => sizeof(int*);", "Size", "unsafe")]
    [InlineData("public int Thrown() => throw new System.Exception();", "Thrown", "throw expression")]
    public async Task MethodsThatCantBeInlined_AreRefused_AndNothingChanges(string member, string name, string reason)
    {
        var source = Lf($$"""
            public class C
            {
                {{member}}

                public object Use() => {{name}}{{(name is "Read" or "Parse" ? "(ref x)" : "()")}};
                private int x;
            }
            """).Replace("Use() => Parse(ref x)", "Use() => Parse(out x)").Replace("Use() => Fact()", "Use() => Fact(3)").Replace("Use() => Early()", "Use() { Early(1); return 1; }")
            .Replace("Use() => Trace()", "Use() { Trace(); return 1; }");
        using var ws = TestWorkspace.Create(("C.cs", source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => InlineAsync(ws, "C.cs", name));

        Assert.Equal(ErrorCodes.MethodNotInlinable, ex.ErrorCode);
        Assert.Contains(reason, ex.Message);
        Assert.Equal(source, ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task CallsThatCantTakeTheirLocals_AreLeftWithAWarning_TheOthersInlined()
    {
        const string source = """
            internal class C
            {
                private int _seed;
                private int Next() => ++_seed;
                private static int Square(int x) => x * x;

                public int Property => Square(Next());
                private readonly int _field = Square(4);

                public int Run(bool flag)
                {
                    var conditional = flag ? Square(Next()) : 0;
                    var before = Next() + Square(Next());
                    while (Square(Next()) < 10) { }
                    var fine = Square(Next());
                    return conditional + before + fine + _field;
                }
            }
            """;
        using var ws = TestWorkspace.Create(("C.cs", Lf(source)));

        var result = await InlineAsync(ws, "C.cs", "Square");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.ReferencesUpdated);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("private readonly int _field = 4 * 4;", text);
        Assert.Contains("        int x = Next();\n        var fine = x * x;\n", text);
        Assert.Contains("private static int Square(int x) => x * x;", text);
        var warnings = result.Warnings!;
        Assert.Contains(warnings, w => w.Contains("C.cs(7,", StringComparison.Ordinal) && w.Contains("expression-bodied member", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("C.cs(12,", StringComparison.Ordinal) && w.Contains("conditionally", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("C.cs(13,", StringComparison.Ordinal) && w.Contains("evaluates something before the call", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("C.cs(14,", StringComparison.Ordinal) && w.Contains("loop condition", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("was not deleted: 4 reference(s) remain", StringComparison.Ordinal));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MethodGroupsAndNameof_AreLeftAndKeepTheMethod()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System;

            internal static class C
            {
                private static int Twice(int x) => x * 2;

                public static Func<int, int> Group() => Twice;
                public static string Name() => nameof(Twice);
                public static int Call(int v) => Twice(v);
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "Twice");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("public static int Call(int v) => v * 2;", text);
        Assert.Contains("private static int Twice(int x) => x * 2;", text);
        Assert.Equal(["'C.Twice(int)' was not deleted: 2 reference(s) remain (calls not inlined, method groups, nameof or crefs)."], result.Warnings);
    }

    [Fact]
    public async Task OneCall_ByLineAndColumn_LeavesTheOthersAndTheMethod()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal static class C
            {
                private static int Inc(int x) => x + 1;

                public static int Run(int a) => Inc(a) * Inc(Inc(a));
            }
            """)));

        var ambiguous = await Assert.ThrowsAsync<RefactoringException>(() => InlineAsync(ws, "C.cs", "Inc", callLine: 5));
        var result = await InlineAsync(ws, "C.cs", "Inc", callLine: 5, callColumn: 50);

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ambiguous.ErrorCode);
        Assert.Contains("37, 46, 50", ambiguous.Message);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        Assert.Contains("public static int Run(int a) => Inc(a) * Inc(a + 1);", ws.ReadFile("C.cs"));
        Assert.Contains("private static int Inc(int x)", ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task TheLastCall_WithDeleteMethod_DeletesTheMethod()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal static class C
            {
                private static int Inc(int x) => x + 1;

                public static int Run(int a) => Inc(a);
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "Inc", callLine: 5, deleteMethod: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            internal static class C
            {
                public static int Run(int a) => a + 1;
            }
            """), ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task CallSiteThatIsNotACall_OrMissing_OrNoCallsAtAll_AreErrors()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System;

            internal static class C
            {
                private static int Inc(int x) => x + 1;
                private static int Unused(int x) => x;

                public static Func<int, int> Group() => Inc;
            }
            """)));

        var group = await Assert.ThrowsAsync<RefactoringException>(() => InlineAsync(ws, "C.cs", "Inc", callLine: 8));
        var missing = await Assert.ThrowsAsync<RefactoringException>(() => InlineAsync(ws, "C.cs", "Inc", callLine: 3));
        var none = await Assert.ThrowsAsync<RefactoringException>(() => InlineAsync(ws, "C.cs", "Unused"));

        Assert.Equal(ErrorCodes.CallSiteNotFound, group.ErrorCode);
        Assert.Contains("is not a call", group.Message);
        Assert.Equal(ErrorCodes.CallSiteNotFound, missing.ErrorCode);
        Assert.Equal(ErrorCodes.CallSiteNotFound, none.ErrorCode);
        Assert.Contains("has no calls", none.Message);
    }

    [Fact]
    public async Task Preview_DescribesEachCallAndTheDeletion_AndWritesNothing()
    {
        var source = Lf("""
            internal static class C
            {
                private static int Inc(int x) => x + 1;

                public static int Run(int a) => Inc(a) + Inc(2);
            }
            """);
        using var ws = TestWorkspace.Create(("C.cs", source));

        var result = await InlineAsync(ws, "C.cs", "Inc", preview: true);

        Assert.True(result.Preview);
        Assert.Equal(source, ws.ReadFile("C.cs"));
        Assert.Equal(3, result.PendingChanges!.Count);
        Assert.Equal(("Inc(a)", "a + 1"), (result.PendingChanges[0].BeforeSnippet, result.PendingChanges[0].AfterSnippet));
        Assert.Equal(("Inc(2)", "2 + 1"), (result.PendingChanges[1].BeforeSnippet, result.PendingChanges[1].AfterSnippet));
        Assert.Equal("Delete method 'Inc'", result.PendingChanges[2].Description);
        Assert.Equal((3, 4), (result.PendingChanges[2].StartLine, result.PendingChanges[2].EndLine));
        Assert.All(result.PendingChanges, c => Assert.Equal(ChangeKind.Modify, c.ChangeType));
    }

    [Fact]
    public async Task CrlfFileWithBom_AndLfFileWithout_KeepTheirEncodingAndLineEndings()
    {
        const string declaring = "internal static class Lib\r\n{\r\n    public static void Log(int value)\r\n    {\r\n        var text = value.ToString();\r\n        System.Console.WriteLine(text);\r\n    }\r\n\r\n    public static void Here() => System.Console.WriteLine(1);\r\n}\r\n";
        const string calling = "internal static class Use\n{\n    public static void Run()\n    {\n        Lib.Log(1);\n    }\n}\n";
        using var ws = TestWorkspace.Create(("Lib.cs", declaring), ("Use.cs", calling));
        File.WriteAllText(ws.PathOf("Lib.cs"), declaring, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await InlineAsync(ws, "Lib.cs", "Log");

        Assert.True(result.Success, result.Error?.Message);
        var lib = File.ReadAllBytes(ws.PathOf("Lib.cs"));
        Assert.True(lib.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("internal static class Lib\r\n{\r\n    public static void Here() => System.Console.WriteLine(1);\r\n}\r\n", Encoding.UTF8.GetString(lib[3..]));
        var use = File.ReadAllBytes(ws.PathOf("Use.cs"));
        Assert.False(use.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal("internal static class Use\n{\n    public static void Run()\n    {\n        var text = 1.ToString();\n        System.Console.WriteLine(text);\n    }\n}\n", Encoding.UTF8.GetString(use));
    }

    [Fact]
    public async Task AcrossProjects_APublicBodyInlines_AnInternalOneIsRefusedThere()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Lib.cs", Lf("""
                namespace Lib;

                public static class Api
                {
                    internal static int Secret = 7;

                    public static int Open(int x) => x + System.Math.Abs(x);
                    public static int Hidden(int x) => x + Secret;
                }
                """))]),
            new TestWorkspace.ProjectSpec("App", [("App.cs", Lf("""
                namespace App;

                public static class Client
                {
                    public static int Run(int v) => Lib.Api.Open(v) + Lib.Api.Hidden(v);
                }
                """))], References: ["Lib"]));

        var open = await Inline(ws.Context, ws.PathOf("Lib", "Lib.cs"), "Open");
        var hidden = await Assert.ThrowsAsync<RefactoringException>(() => Inline(ws.Context, ws.PathOf("Lib", "Lib.cs"), "Hidden"));

        Assert.True(open.Success, open.Error?.Message);
        Assert.Contains("public static int Run(int v) => v + System.Math.Abs(v) + Lib.Api.Hidden(v);", File.ReadAllText(ws.PathOf("App", "App.cs")));
        Assert.DoesNotContain("Open", File.ReadAllText(ws.PathOf("Lib", "Lib.cs")));
        Assert.Equal(ErrorCodes.CallNotInlinable, hidden.ErrorCode);
        Assert.Contains("'Lib.Api.Secret', which is not accessible here", hidden.Message);
    }

    [Fact]
    public async Task MultiTargetedProject_EditsTheFileOnce_AndKeepsItsDocumentsInStep()
    {
        using var ws = TestWorkspace.CreateSolution(new TestWorkspace.ProjectSpec(
            "Lib",
            [("C.cs", "internal static class C\n{\n    private static int Inc(int x) => x + 1;\n\n    public static int Run(int a) => Inc(a);\n}\n")],
            TargetFrameworks: ["net8", "net9"]));

        var result = await Inline(ws.Context, ws.PathOf("Lib", "C.cs"), "Inc");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal([ws.PathOf("Lib", "C.cs")], result.Changes!.FilesModified);
        Assert.Equal("internal static class C\n{\n    public static int Run(int a) => a + 1;\n}\n", File.ReadAllText(ws.PathOf("Lib", "C.cs")));
        var texts = ws.Context.Solution.GetDocumentIdsWithFilePath(ws.PathOf("Lib", "C.cs"))
            .Select(id => ws.Context.Solution.GetDocument(id)!.GetTextAsync(TestContext.Current.CancellationToken).Result.ToString())
            .Distinct();
        Assert.Single(texts);
    }

    [Fact]
    public async Task ImplicitInterfaceImplementation_IsInlinedButKept()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal interface IShape { int Area(); }

            internal sealed class Square : IShape
            {
                public int Side;
                public int Area() => Side * Side;
                public int Twice() => 2 * Area();
            }
            """)));

        var result = await Inline(ws.Context, ws.PathOf("C.cs"), "Area", line: 6);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Twice() => 2 * Side * Side;", ws.ReadFile("C.cs"));
        Assert.Contains("public int Area() => Side * Side;", ws.ReadFile("C.cs"));
        Assert.Equal(["'Square.Area()' was not deleted: it implements 'IShape.Area()'."], result.Warnings);
    }

    [Fact]
    public async Task ConstrainedGenericCall_BindsToTheTypeArgumentsMember_AndIsAccepted()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System;

            internal static class C
            {
                private static T Larger<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b;

                public static int Run(int x, int y) => Larger(x, y);
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "Larger");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Run(int x, int y) => x.CompareTo(y) >= 0 ? x : y;", ws.ReadFile("C.cs"));
        Assert.Null(result.Warnings);
    }

    [Fact]
    public async Task ANameTheCallSiteHides_IsRefusedThere()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal class Base
            {
                public string Name = "base";
                public string Describe() => "I am " + Name;
            }

            internal sealed class Derived : Base
            {
                private new string Name = "derived";
                public string Run() => Describe() + Name;
            }

            internal static class Other
            {
                public static string Run(Base b) => b.Describe();
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "Describe");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("public string Run() => Describe() + Name;", text);
        Assert.Contains("public static string Run(Base b) => \"I am \" + b.Name;", text);
        Assert.Contains(result.Warnings!, w => w.Contains("C.cs(10,", StringComparison.Ordinal) && w.Contains("would refer to something else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtensionMethodsTheBodyCalls_GetTheirNamespaceImported()
    {
        using var ws = TestWorkspace.Create(
            ("Lib.cs", Lf("""
                using System.Collections.Generic;
                using System.Linq;

                namespace Lib;

                public static class Numbers
                {
                    public static int Positives(IEnumerable<int> values) => values.Where(v => v > 0).Count();
                }
                """)),
            ("App.cs", Lf("""
                namespace App;

                public static class Client
                {
                    public static int Run(int[] data) => Lib.Numbers.Positives(data);
                }
                """)));

        var result = await InlineAsync(ws, "Lib.cs", "Positives");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            using System.Linq;

            namespace App;

            public static class Client
            {
                public static int Run(int[] data) => data.Where(v => v > 0).Count();
            }
            """), ws.ReadFile("App.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task OutVariablesOfAnExpressionBody_AreRenamedPerCall()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal static class C
            {
                private static bool IsPositive(string s) => int.TryParse(s, out var n) && n > 0;

                public static bool Run(string a, string b)
                {
                    var first = IsPositive(a);
                    var second = IsPositive(b);
                    return first && second;
                }
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "IsPositive");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("var first = int.TryParse(a, out var n) && n > 0;", text);
        Assert.Contains("var second = int.TryParse(b, out var n1) && n1 > 0;", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ConditionalAccessCheckedContextAndExpressionTrees_AreRefused()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System;
            using System.Linq.Expressions;

            internal sealed class Box
            {
                public int Value;
                public int Doubled() => Value * 2;
            }

            internal static class Use
            {
                public static int? Maybe(Box? box) => box?.Doubled();
                public static int Checked(Box box) => checked(box.Doubled());
                public static Expression<Func<Box, int>> Tree() => b => b.Doubled();
                public static int Plain(Box box) => box.Doubled();
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "Doubled");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Plain(Box box) => box.Value * 2;", ws.ReadFile("C.cs"));
        var warnings = string.Join("\n", result.Warnings!);
        Assert.Contains("conditional access", warnings);
        Assert.Contains("overflow-checking context", warnings);
        Assert.Contains("expression tree", warnings);
    }

    [Fact]
    public async Task VoidStatementsWithLocals_KeepArgumentOrder_CompiledAndRun()
    {
        const string source = """
            using System.Collections.Generic;

            public static class Program
            {
                private static readonly List<string> Log = new();

                private static string Next(string tag)
                {
                    Log.Add(tag);
                    return tag;
                }

                private static void Show(string first, string second)
                {
                    Log.Add("show " + second);
                    Log.Add("show " + first);
                }

                private static string Name(string value) => nameof(value) + "=" + value;

                public static string Run()
                {
                    Log.Clear();
                    Show(Next("a"), Next("b"));
                    Log.Add(Name("v"));
                    return string.Join(",", Log);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws);

        var show = await InlineAsync(ws, "Program.cs", "Show");
        var name = await InlineAsync(ws, "Program.cs", "Name");

        Assert.True(show.Success, show.Error?.Message);
        Assert.True(name.Success, name.Error?.Message);
        Assert.Equal(before, Run(ws));
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("        string first = Next(\"a\");\n        string second = Next(\"b\");\n        Log.Add(\"show \" + second);\n", text);
        Assert.Contains("Log.Add(\"value\" + \"=\" + \"v\");", text);
    }

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(InlineMethodRulesTests), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(ws.EmitDiskAssembly()));
            return (string)assembly.GetType("Program", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null)!;
        }
        finally
        {
            context.Unload();
        }
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n") + "\n";

    private static Task<RefactoringResult> InlineAsync(
        TestWorkspace ws,
        string file,
        string method,
        int? callLine = null,
        int? callColumn = null,
        bool? deleteMethod = null,
        bool preview = false) =>
        Inline(ws.Context, ws.PathOf(file), method, null, callLine, callColumn, deleteMethod, preview);

    private static Task<RefactoringResult> Inline(
        WorkspaceContext context,
        string file,
        string method,
        int? line = null,
        int? callLine = null,
        int? callColumn = null,
        bool? deleteMethod = null,
        bool preview = false) =>
        new InlineMethodOperation(context).ExecuteAsync(
            new InlineMethodParams
            {
                SourceFile = file,
                MethodName = method,
                Line = line,
                CallLine = callLine,
                CallColumn = callColumn,
                DeleteMethod = deleteMethod,
                Preview = preview
            },
            TestContext.Current.CancellationToken);
}
