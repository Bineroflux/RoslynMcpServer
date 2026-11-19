using System.Runtime.Loader;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Inline;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// What inline_method keeps of a call's evaluation: the receiver it doesn't use, the
/// NullReferenceException of a null receiver, exceptions the body may throw before it uses an
/// argument, and which reads count as side effects.
/// </summary>
public sealed class InlineMethodEvaluationTests
{
    [Fact]
    public async Task AReceiverTheBodyDoesNotUse_IsEvaluatedForItsEffects_AndOneThatMayBeNullIsReported()
    {
        const string source = """
            using System.Collections.Generic;

            public sealed class Box
            {
                public int Value { get; set; } = 1;
                public int Add(int a, int b) => a + b;
                public int Pick(int a) => a > 0 ? Value : 0;
            }

            public static class Program
            {
                public static readonly List<string> Log = new();
                private static Box? _cached;

                private static Box Make(string tag)
                {
                    Log.Add(tag);
                    return new Box();
                }

                private static Box? Find(string tag)
                {
                    Log.Add(tag);
                    return null;
                }

                public static int Run(Box? maybe, Box sure)
                {
                    var a = Make("a").Add(1, 2);
                    Make("b").Add(3, 4);
                    var c = maybe.Add(5, 6);
                    var d = sure.Add(7, 8);
                    var e = Find("e").Add(9, 10);
                    var f = maybe.Pick(1);
                    var g = _cached.Add(11, 12);
                    return a + c + d + e + f + g;
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));

        var add = await InlineAsync(ws, "Add");
        var pick = await InlineAsync(ws, "Pick");

        Assert.True(add.Success, add.Error?.Message);
        Assert.True(pick.Success, pick.Error?.Message);
        Assert.Contains(Lf("""
                public static int Run(Box? maybe, Box sure)
                {
                    Make("a");
                    var a = 1 + 2;
                    Make("b");
                    _ = 3 + 4;
                    var c = 5 + 6;
                    var d = 7 + 8;
                    Find("e");
                    var e = 9 + 10;
                    var f = 1 > 0 ? maybe.Value : 0;
                    var g = 11 + 12;
                    return a + c + d + e + f + g;
                }
            """), ws.ReadFile("Program.cs"));
        var path = ws.PathOf("Program.cs");
        Assert.Equal(
            [
                $"{path}(31,23): inlined, but a null 'maybe' no longer throws NullReferenceException: the inlined code doesn't use it",
                $"{path}(33,27): inlined, but a null 'Find(\"e\")' no longer throws NullReferenceException: the inlined code doesn't use it",
                $"{path}(35,25): inlined, but a null '_cached' no longer throws NullReferenceException: the inlined code doesn't use it"
            ],
            add.Warnings);
        Assert.Equal(
            [$"{path}(36,23): inlined, but a null 'maybe' no longer throws NullReferenceException before the method's code runs: " +
                "the inlined code uses it only later or conditionally"],
            pick.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ANullReceiver_ThrowsAfterTheArgumentsAreEvaluated_AsTheCallDid_CompiledAndRun()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            public sealed class Box
            {
                public int Step = 2;
                public int Plus(int a) => Step + a;
                public int Both(int a) => a + Step;
            }

            public static class Program
            {
                private static readonly List<string> Log = new();

                private static int Next(int value)
                {
                    Log.Add("n" + value);
                    return value;
                }

                public static string Run()
                {
                    Log.Clear();
                    Box? none = null;
                    try
                    {
                        var r = none.Plus(Next(1));
                        Log.Add("r" + r);
                    }
                    catch (NullReferenceException)
                    {
                        Log.Add("nre");
                    }

                    try
                    {
                        var r = none.Both(Next(2));
                        Log.Add("r" + r);
                    }
                    catch (NullReferenceException)
                    {
                        Log.Add("nre");
                    }

                    return string.Join(",", Log);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws);

        var plus = await InlineAsync(ws, "Plus");
        var both = await InlineAsync(ws, "Both");

        Assert.True(plus.Success, plus.Error?.Message);
        Assert.True(both.Success, both.Error?.Message);
        Assert.Null(plus.Warnings);
        Assert.Null(both.Warnings);
        Assert.Equal("n1,nre,n2,nre", before);
        Assert.Equal(before, Run(ws));
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("            int a = Next(1);\n            var r = none.Step + a;\n", text);
        Assert.Contains("            var r = Next(2) + none.Step;\n", text);
    }

    [Theory]
    [InlineData(true, "big + big + step")]
    [InlineData(false, "checked(big + big) + step")]
    public async Task CodeThatMayThrow_BeforeTheBodyUsesAnArgument_KeepsTheArgumentEvaluatedFirst_CompiledAndRun(bool checkOverflow, string grow)
    {
        var source = $$"""
            using System;
            using System.Collections.Generic;

            public static class Program
            {
                private static readonly List<string> Log = new();

                private static int Next(int value)
                {
                    Log.Add("n" + value);
                    return value;
                }

                private static int Grow(int big, int step) => {{grow}};

                private static int Half(int value, int divisor, int step) => value / divisor + step;

            #nullable disable
                private static int Length(string text, int step) => text.Length + step;
            #nullable restore

                public static string Run()
                {
                    Log.Clear();
                    var big = int.MaxValue;
                    var zero = 0;
                    try
                    {
                        var r = Grow(big, Next(1));
                        Log.Add("r" + r);
                    }
                    catch (OverflowException)
                    {
                        Log.Add("overflow");
                    }

                    try
                    {
                        var r = Half(1, zero, Next(2));
                        Log.Add("r" + r);
                    }
                    catch (DivideByZeroException)
                    {
                        Log.Add("zero");
                    }

                    try
                    {
                        var r = Length(null!, Next(3));
                        Log.Add("r" + r);
                    }
                    catch (NullReferenceException)
                    {
                        Log.Add("nre");
                    }

                    return string.Join(",", Log);
                }
            }
            """;
        using var ws = TestWorkspace.Create([("Program.cs", Lf(source))], [], checkOverflow: checkOverflow);
        var before = Run(ws);

        foreach (var method in new[] { "Grow", "Half", "Length" })
        {
            var result = await InlineAsync(ws, method);
            Assert.True(result.Success, result.Error?.Message);
        }

        Assert.Equal("n1,overflow,n2,zero,n3,nre", before);
        Assert.Equal(before, Run(ws));
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("            int step = Next(1);\n", text);
        Assert.Contains("            int step1 = Next(2);\n", text);
        Assert.Contains("            int step2 = Next(3);\n", text);
    }

    [Fact]
    public async Task AutoPropertiesAndReadonlyFields_AreReadsWithoutSideEffects_ComputedPropertiesAreNot_CompiledAndRun()
    {
        const string source = """
            using System.Collections.Generic;

            public sealed class Item
            {
                public int Count { get; set; } = 1;
                public int Id { get; } = 7;
                public readonly int Weight = 3;
                public int Computed => Count * 2;
                public int Plus(int a) => Count + Id + a;
            }

            public static class Program
            {
                private static readonly List<string> Log = new();

                private static int Next(int value)
                {
                    Log.Add("n" + value);
                    return value;
                }

                private static int Second(int a, int b) => b - a;

                public static string Run()
                {
                    Log.Clear();
                    var item = new Item();
                    var plus = item.Plus(Next(1));
                    var id = Second(item.Id, Next(2));
                    var weight = Second(item.Weight, Next(3));
                    var count = Second(item.Count, Next(4));
                    var computed = Second(item.Computed, Next(5));
                    return string.Join(",", Log) + "|" + string.Join(",", plus, id, weight, count, computed);
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws);

        var plus = await InlineAsync(ws, "Plus");
        var second = await InlineAsync(ws, "Second");

        Assert.True(plus.Success, plus.Error?.Message);
        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(before, Run(ws));
        Assert.Contains(Lf("""
                    var plus = item.Count + item.Id + Next(1);
                    var id = Next(2) - item.Id;
                    var weight = Next(3) - item.Weight;
                    int a = item.Count;
                    int b = Next(4);
                    var count = b - a;
                    int a1 = item.Computed;
                    int b1 = Next(5);
                    var computed = b1 - a1;
            """), ws.ReadFile("Program.cs"));
    }

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(InlineMethodEvaluationTests), isCollectible: true);
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

    private static Task<RefactoringResult> InlineAsync(TestWorkspace ws, string method) =>
        new InlineMethodOperation(ws.Context).ExecuteAsync(
            new InlineMethodParams { SourceFile = ws.PathOf("Program.cs"), MethodName = method },
            TestContext.Current.CancellationToken);
}
