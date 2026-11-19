using System.Runtime.Loader;
using System.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Tests.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// inline_method replaces calls with the method's body, keeping what the code does: the order and
/// number of evaluations of the arguments, the names they bind to, and the types of the results.
/// </summary>
public sealed class InlineMethodTests
{
    [Fact]
    public async Task ExpressionBody_ReplacesEveryCall_ParenthesizedWhereNeeded_AndDeletesTheMethod()
    {
        const string source = """
            namespace App;

            internal static class Calc
            {
                /// <summary>Adds.</summary>
                private static int Add(int a, int b) => a + b;

                public static int Run(int x, int y)
                {
                    var sum = Add(x, y);
                    return 2 * Add(x, y - 1) + sum;
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Calc.cs", Lf(source)));

        var result = await InlineAsync(ws, "Calc.cs", "Add");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Lf("""
            namespace App;

            internal static class Calc
            {
                public static int Run(int x, int y)
                {
                    var sum = x + y;
                    return 2 * (x + (y - 1)) + sum;
                }
            }
            """), ws.ReadFile("Calc.cs"));
        Assert.Equal(2, result.ReferencesUpdated);
        Assert.Null(result.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task SingleReturnBlock_InlinesItsExpression()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal static class C
            {
                private static bool IsEven(int n)
                {
                    return n % 2 == 0;
                }

                public static bool Run(int value) => !IsEven(value);
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "IsEven");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static bool Run(int value) => !(value % 2 == 0);", ws.ReadFile("C.cs"));
        Assert.DoesNotContain("IsEven", ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task ArgumentsWithSideEffects_KeepTheirOrderAndCount_CompiledAndRun()
    {
        const string source = """
            using System.Collections.Generic;

            public static class Program
            {
                private static readonly List<string> Log = new();
                private static int _counter;

                private static int Next(string tag)
                {
                    Log.Add(tag);
                    return ++_counter;
                }

                private static int Square(int x) => x * x;
                private static int Add(int a, int b) => a + b;
                private static int Ignore(int value) => 42;
                private static int Twice(int value) => value * 2;
                private static int Second(int a, int b) => b - a;

                public static string Run()
                {
                    Log.Clear();
                    _counter = 0;
                    var square = Square(Next("square"));
                    var add = Add(Next("a"), Next("b"));
                    var named = Add(b: Next("named-b"), a: Next("named-a"));
                    var ignored = Ignore(Next("ignored"));
                    var twice = Twice(Next("twice"));
                    var second = Second(Next("first"), _counter);
                    Square(Next("discarded"));
                    return string.Join(",", Log) + "|" + string.Join(",", square, add, named, ignored, twice, second, _counter);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws, "Program", "Run");

        foreach (var method in new[] { "Square", "Add", "Ignore", "Twice", "Second" })
        {
            var result = await InlineAsync(ws, "Program.cs", method);
            Assert.True(result.Success, result.Error?.Message);
            Assert.True(result.Warnings == null, method + ": " + string.Join("\n", result.Warnings ?? []) + "\n" + ws.ReadFile("Program.cs"));
        }

        var after = ws.ReadFile("Program.cs");
        Assert.Equal(before, Run(ws, "Program", "Run"));
        Assert.Contains("int x = Next(\"square\");", after);
        Assert.Contains("var square = x * x;", after);
        Assert.Contains("int value = Next(\"ignored\");", after);
        Assert.Contains("var twice = Next(\"twice\") * 2;", after);
        Assert.DoesNotContain("private static int Square", after);
    }

    [Fact]
    public async Task VoidBody_ReplacesTheStatement_RenamingClashingLocals_CompiledAndRun()
    {
        const string source = """
            using System.Text;

            public static class Program
            {
                private static readonly StringBuilder Output = new();

                private static void Write(string message, int times)
                {
                    var text = "[" + message + "]";
                    for (var i = 0; i < times; i++)
                        Output.Append(text);
                }

                public static string Run()
                {
                    Output.Clear();
                    var text = "outer";
                    // write twice
                    Write(text, 2);
                    if (text.Length > 0)
                        Write("embedded", 1);
                    for (var i = 0; i < 2; i++)
                    {
                        Write(text + i, 1);
                    }

                    return Output.ToString();
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws, "Program", "Run");

        var result = await InlineAsync(ws, "Program.cs", "Write");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.ReferencesUpdated);
        Assert.Equal(before, Run(ws, "Program", "Run"));
        Assert.Equal(Lf("""
            using System.Text;

            public static class Program
            {
                private static readonly StringBuilder Output = new();

                public static string Run()
                {
                    Output.Clear();
                    var text = "outer";
                    // write twice
                    var text1 = "[" + text + "]";
                    for (var i1 = 0; i1 < 2; i1++)
                        Output.Append(text1);
                    if (text.Length > 0)
                    {
                        var text2 = "[" + "embedded" + "]";
                        for (var i2 = 0; i2 < 1; i2++)
                            Output.Append(text2);
                    }
                    for (var i = 0; i < 2; i++)
                    {
                        var text3 = "[" + text + i + "]";
                        for (var i3 = 0; i3 < 1; i3++)
                            Output.Append(text3);
                    }

                    return Output.ToString();
                }
            }
            """), ws.ReadFile("Program.cs"));
    }

    [Fact]
    public async Task GenericLocalFunctionsOfTheBody_AreCopied_TheirNamesAndTypeParametersRenamedOnAClash()
    {
        const string source = """
            using System.Collections.Generic;

            public static class Program
            {
                private static readonly List<string> Log = new();

                private static void Record(int value)
                {
                    Log.Add(Describe(value) + Describe("x"));
                    string Describe<T>(T item) => typeof(T).Name + ":" + item + value;
                }

                private static void Twice<T>(T item)
                {
                    Record(1);
                    Log.Add(Describe(item));
                    static string Describe(T item) => "outer " + item;
                }

                public static string Run()
                {
                    Log.Clear();
                    Record(2);
                    Twice("y");
                    return string.Join(",", Log);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws, "Program", "Run");

        var result = await InlineAsync(ws, "Program.cs", "Record");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.ReferencesUpdated);
        Assert.Null(result.Warnings);
        Assert.Equal(before, Run(ws, "Program", "Run"));
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("""
                    Log.Add(Describe(2) + Describe("x"));
                    string Describe<T>(T item) => typeof(T).Name + ":" + item + 2;
            """.ReplaceLineEndings("\n"), text);
        Assert.Contains("""
                    Log.Add(Describe1(1) + Describe1("x"));
                    string Describe1<T1>(T1 item1) => typeof(T1).Name + ":" + item1 + 1;
                    Log.Add(Describe(item));
            """.ReplaceLineEndings("\n"), text);
        Assert.DoesNotContain("Record", text);
    }

    [Fact]
    public async Task NamedArgumentsDefaultsParamsAndCasts_AreSubstituted()
    {
        const string source = """
            internal enum Mode { Fast, Slow }

            internal static class C
            {
                private static long Scale(int value, int factor = 3, Mode mode = Mode.Slow) => value * factor + (int)mode;
                private static int Count(params int[] items) => items.Length;

                public static long Run(int x)
                {
                    var a = Scale(x);
                    var b = Scale(factor: 2, value: x);
                    var c = Count(1, 2, 3);
                    return a + b + c;
                }
            }
            """;
        using var ws = TestWorkspace.Create(("C.cs", Lf(source)));

        var scale = await InlineAsync(ws, "C.cs", "Scale");
        var count = await InlineAsync(ws, "C.cs", "Count");

        Assert.True(scale.Success, scale.Error?.Message);
        Assert.True(count.Success, count.Error?.Message);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("var a = (long)(x * 3 + (int)Mode.Slow);", text);
        Assert.Contains("var b = (long)(x * 2 + (int)Mode.Slow);", text);
        Assert.Contains("var c = (new int[] { 1, 2, 3 }).Length;", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtensionAndGenericMethods_TakeTheReceiverAndTheTypeArguments()
    {
        const string source = """
            using System.Collections.Generic;

            namespace App;

            internal static class Extensions
            {
                public static int Doubled(this int value) => value * 2;
                public static List<T> Wrap<T>(T item) => new List<T> { item };
            }

            internal static class Use
            {
                public static int Run(int n)
                {
                    var list = Extensions.Wrap(n.Doubled());
                    var names = Extensions.Wrap<string>("x");
                    return list.Count + names.Count;
                }
            }
            """;
        using var ws = TestWorkspace.Create(("App.cs", Lf(source)));

        var doubled = await InlineAsync(ws, "App.cs", "Doubled");
        var wrap = await InlineAsync(ws, "App.cs", "Wrap");

        Assert.True(doubled.Success, doubled.Error?.Message);
        Assert.True(wrap.Success, wrap.Error?.Message);
        var text = ws.ReadFile("App.cs");
        Assert.Contains("var list = new List<int> { n * 2 };", text);
        Assert.Contains("var names = new List<string> { \"x\" };", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task InstanceMethod_ThisBecomesTheReceiver_AStatefulReceiverIsEvaluatedOnce()
    {
        const string source = """
            using System.Collections.Generic;

            public sealed class Counter
            {
                public int Value { get; set; }
                public int Step;

                public int Plus(int amount) => Value + amount;
                public int Both() => Value + Step;
            }

            public static class Program
            {
                private static readonly List<string> Log = new();

                private static Counter Make(string tag)
                {
                    Log.Add(tag);
                    return new Counter { Value = 10, Step = 1 };
                }

                public static string Run()
                {
                    Log.Clear();
                    var counter = new Counter { Value = 1, Step = 2 };
                    var a = counter.Plus(5);
                    var b = Make("once").Plus(1);
                    var c = Make("both").Both();
                    return string.Join(",", Log) + "|" + a + "," + b + "," + c;
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws, "Program", "Run");

        var plus = await InlineAsync(ws, "Program.cs", "Plus");
        var both = await InlineAsync(ws, "Program.cs", "Both");

        Assert.True(plus.Success, plus.Error?.Message);
        Assert.True(both.Success, both.Error?.Message);
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("var a = counter.Value + 5;", text);
        Assert.Contains("var b = Make(\"once\").Value + 1;", text);
        Assert.Contains("Counter counter1 = Make(\"both\");", text);
        Assert.Contains("var c = counter1.Value + counter1.Step;", text);
        Assert.Equal(before, Run(ws, "Program", "Run"));
    }

    [Fact]
    public async Task StaticMembersAndTypes_StillBindInAnotherNamespaceWithOtherUsings()
    {
        using var ws = TestWorkspace.Create(
            ("Lib.cs", Lf("""
                using System.Text;
                using static System.Math;

                namespace Lib;

                public static class Helper
                {
                    public const int Offset = 1;

                    public static int Larger(int a, int b) => Max(a, b) + Offset + new StringBuilder("x").Length;

                    public static int Local(int a) => Larger(a, Offset);
                }
                """)),
            ("App.cs", Lf("""
                namespace App;

                public static class Client
                {
                    public static int Run() => Lib.Helper.Larger(1, 2);
                }
                """)));

        var result = await InlineAsync(ws, "Lib.cs", "Larger");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public static int Run() => System.Math.Max(1, 2) + Lib.Helper.Offset + new System.Text.StringBuilder(\"x\").Length;", ws.ReadFile("App.cs"));
        Assert.Contains("public static int Local(int a) => Max(a, Offset) + Offset + new StringBuilder(\"x\").Length;", ws.ReadFile("Lib.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task BodyUsingAPrivateMember_IsRefusedWhereItIsInaccessible_AndTheMethodKept()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal class Account
            {
                private int _balance = 5;

                public int Doubled() => _balance * 2;

                public int Inside() => Doubled();
            }

            internal static class Outside
            {
                public static int Run(Account account) => account.Doubled();
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "Doubled");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.ReferencesUpdated);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("public int Inside() => _balance * 2;", text);
        Assert.Contains("account.Doubled()", text);
        Assert.Contains("public int Doubled()", text);
        Assert.Contains(result.Warnings!, w => w.Contains("C.cs(12,", StringComparison.Ordinal) && w.Contains("'Account._balance', which is not accessible here", StringComparison.Ordinal));
        Assert.Contains(result.Warnings!, w => w.Contains("was not deleted: 1 reference(s) remain", StringComparison.Ordinal));
    }

    private static string Run(TestWorkspace ws, string type, string method)
    {
        var context = new AssemblyLoadContext(nameof(InlineMethodTests), isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(ws.EmitDiskAssembly()));
            return (string)assembly.GetType(type, throwOnError: true)!.GetMethod(method)!.Invoke(null, null)!;
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
        int? line = null,
        string? callFile = null,
        int? callLine = null,
        int? callColumn = null,
        bool? deleteMethod = null,
        bool preview = false) =>
        Inline(ws.Context, ws.PathOf(file), method, line, callFile == null ? null : ws.PathOf(callFile), callLine, callColumn, deleteMethod, preview);

    private static Task<RefactoringResult> Inline(
        WorkspaceContext context,
        string file,
        string method,
        int? line = null,
        string? callFile = null,
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
                CallFile = callFile,
                CallLine = callLine,
                CallColumn = callColumn,
                DeleteMethod = deleteMethod,
                Preview = preview
            },
            TestContext.Current.CancellationToken);
}
