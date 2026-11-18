using System.Runtime.Loader;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Where inline_method puts the locals an argument needs (before which statement, in which
/// statement list), nested calls, receivers that are structs, and names in lambdas.
/// </summary>
public sealed class InlineMethodPositionTests
{
    [Fact]
    public async Task LocalsGoBeforeTheStatement_InIfConditionsSwitchSectionsAndNestedCalls_CompiledAndRun()
    {
        const string source = """
            using System.Collections.Generic;

            public static class Program
            {
                private static readonly List<string> Log = new();
                private static int _n;

                private static int Next(string tag)
                {
                    Log.Add(tag);
                    return ++_n;
                }

                private static int Add(int a, int b) => a + b;

                public static string Run()
                {
                    Log.Clear();
                    _n = 0;
                    var total = 0;
                    if (Add(Next("if-a"), Next("if-b")) > 2)
                        total += 1;
                    switch (total)
                    {
                        case 1:
                            total += Add(Next("case-a"), Next("case-b"));
                            break;
                    }

                    foreach (var item in new[] { Add(Add(Next("inner-a"), Next("inner-b")), Next("outer")) })
                        total += item;
                    Add(Next("discard-a"), 1);
                    return string.Join(",", Log) + "|" + total;
                }
            }
            """;
        using var ws = TestWorkspace.Create(("Program.cs", Lf(source)));
        var before = Run(ws);

        var result = await InlineAsync(ws, "Program.cs", "Add");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        Assert.Equal(5, result.ReferencesUpdated);
        Assert.Equal(before, Run(ws));
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("""
                    int a = Next("if-a");
                    int b = Next("if-b");
                    if (a + b > 2)
                        total += 1;
            """.ReplaceLineEndings("\n"), text);
        Assert.Contains("""
                        case 1:
                            int a1 = Next("case-a");
                            int b1 = Next("case-b");
                            total += a1 + b1;
                            break;
            """.ReplaceLineEndings("\n"), text);
        Assert.Contains("        _ = Next(\"discard-a\") + 1;\n", text);
    }

    [Fact]
    public async Task VoidCallInAnEmbeddedStatement_BecomesItsSingleStatementOrABlock()
    {
        const string source = """
            using System;

            internal static class C
            {
                private static void Say(string text) => Console.WriteLine(text);

                private static void SayTwice(string text)
                {
                    Console.WriteLine(text);
                    Console.WriteLine(text);
                }

                public static void Run(bool flag)
                {
                    if (flag)
                        Say("one");
                    else
                        SayTwice("two");
                }
            }
            """;
        using var ws = TestWorkspace.Create(("C.cs", Lf(source)));

        await InlineAsync(ws, "C.cs", "Say");
        var result = await InlineAsync(ws, "C.cs", "SayTwice");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("""
                    if (flag)
                        Console.WriteLine("one");
                    else
                    {
                        Console.WriteLine("two");
                        Console.WriteLine("two");
                    }
            """.ReplaceLineEndings("\n"), ws.ReadFile("C.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task MutableStructReceiverNeedingACopy_IsRefused_AReadonlyOneIsNot()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal struct Mutable
            {
                public int N;
                public int Twice() => N + N;
            }

            internal readonly struct Frozen
            {
                public readonly int N;
                public Frozen(int n) => N = n;
                public int Twice() => N + N;
            }

            internal static class Use
            {
                private static Mutable MakeMutable() => new Mutable { N = 1 };
                private static Frozen MakeFrozen() => new Frozen(1);

                public static int Run()
                {
                    var a = MakeMutable().Twice();
                    var b = MakeFrozen().Twice();
                    return a + b;
                }
            }
            """)));

        var mutable = await Assert.ThrowsAsync<RefactoringException>(() => InlineAsync(ws, "C.cs", "Twice", line: 4));
        var frozen = await InlineAsync(ws, "C.cs", "Twice", line: 11);

        Assert.Equal(ErrorCodes.CallNotInlinable, mutable.ErrorCode);
        Assert.Contains("struct", mutable.Message);
        Assert.True(frozen.Success, frozen.Error?.Message);
        Assert.Contains("Frozen frozen = MakeFrozen();\n        var b = frozen.N + frozen.N;", ws.ReadFile("C.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task BaseInTheBody_IsRefusedOutsideTheDeclaringType()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            internal class Animal
            {
                public virtual string Sound() => "...";
            }

            internal class Dog : Animal
            {
                public override string Sound() => "woof";
                public string Both() => base.Sound() + Sound();
                public string Here() => Both();
            }

            internal static class Use
            {
                public static string Run(Dog dog) => dog.Both();
            }
            """)));

        var result = await InlineAsync(ws, "C.cs", "Both");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public string Here() => base.Sound() + Sound();", ws.ReadFile("C.cs"));
        Assert.Contains(result.Warnings!, w => w.Contains("C.cs(15,", StringComparison.Ordinal) && w.Contains("'base'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LambdaParametersAndTypeParameters_AreRenamedAndSubstituted()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System.Collections.Generic;
            using System.Linq;

            internal static class C
            {
                private static List<int> Scaled(IEnumerable<int> items, int factor) => items.Select(x => x * factor).ToList();
                private static string Describe<T>(T value) => typeof(T).Name + ":" + value;

                public static int Run(int[] data, int x)
                {
                    var scaled = Scaled(data, x);
                    var count = scaled.Count;
                    return count + Describe(count).Length + Describe<string>("s").Length;
                }
            }
            """)));

        var scaled = await InlineAsync(ws, "C.cs", "Scaled");
        var describe = await InlineAsync(ws, "C.cs", "Describe");

        Assert.True(scaled.Success, scaled.Error?.Message);
        Assert.True(describe.Success, describe.Error?.Message);
        var text = ws.ReadFile("C.cs");
        Assert.Contains("var scaled = data.Select(x1 => x1 * x).ToList();", text);
        Assert.Contains("return count + (typeof(int).Name + \":\" + count).Length + (typeof(string).Name + \":\" + \"s\").Length;", text);
        Assert.Null(describe.Warnings);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task VoidExpressionBodyInALambda_ReplacesTheCall_ABlockBodyIsRefusedThere()
    {
        using var ws = TestWorkspace.Create(("C.cs", Lf("""
            using System;

            internal static class C
            {
                private static void Say(string text) => Console.WriteLine(text);

                private static void SayTwice(string text)
                {
                    Console.WriteLine(text);
                    Console.WriteLine(text);
                }

                public static Action One() => () => Say("one");
                public static Action Two() => () => SayTwice("two");
            }
            """)));

        var say = await InlineAsync(ws, "C.cs", "Say");
        var twice = await Assert.ThrowsAsync<RefactoringException>(() => InlineAsync(ws, "C.cs", "SayTwice"));

        Assert.True(say.Success, say.Error?.Message);
        Assert.Contains("public static Action One() => () => Console.WriteLine(\"one\");", ws.ReadFile("C.cs"));
        Assert.Equal(ErrorCodes.CallNotInlinable, twice.ErrorCode);
        Assert.Contains("statement of its own", twice.Message);
    }

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(InlineMethodPositionTests), isCollectible: true);
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

    private static Task<RefactoringResult> InlineAsync(TestWorkspace ws, string file, string method, int? line = null) =>
        new InlineMethodOperation(ws.Context).ExecuteAsync(
            new InlineMethodParams { SourceFile = ws.PathOf(file), MethodName = method, Line = line },
            TestContext.Current.CancellationToken);
}
