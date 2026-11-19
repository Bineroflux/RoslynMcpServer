using System.Runtime.Loader;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// introduce_parameter updates every call: it used to look the method up in the already
/// changed solution, found no calls, and left them without the new argument (CS7036).
/// </summary>
public sealed class IntroduceParameterCallSiteTests
{
    private const string Report = """
        namespace Lib;

        public class Report
        {
            private readonly string _title;

            public Report(string title) => _title = title;

            public string Describe(int width)
            {
                var separator = new string('-', width);
                return _title + separator;
            }

            public string Twice() => Describe(2) + Describe(width: 3);
        }

        public class Fancy : Report
        {
            public Fancy() : base("fancy") { }

            public string Both() => base.Describe(1);
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CallsInEveryProject_PassTheInitializerWithTheirArguments()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Lib", [("Report.cs", Lf(Report))]),
            new TestWorkspace.ProjectSpec("App", [("Program.cs", Lf("""
                using Lib;

                public static class Program
                {
                    public static string Run()
                    {
                        var report = new Report("t");
                        var width = 4;
                        return report.Describe(width) + "|" + report.Describe(width: 2) + "|" + report.Twice() + "|" + new Fancy().Both();
                    }
                }
                """))], References: ["Lib"]));
        var before = Run(ws);

        var result = await IntroduceAsync(ws, ws.PathOf("Lib", "Report.cs"), "separator", "var separator");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        Assert.Equal(5, result.ReferencesUpdated);
        var report = File.ReadAllText(ws.PathOf("Lib", "Report.cs"));
        Assert.Contains("public string Describe(int width, string separator)", report);
        Assert.DoesNotContain("var separator", report);
        Assert.Contains("Describe(2, new string('-', 2)) + Describe(width: 3, new string('-', 3))", report);
        Assert.Contains("base.Describe(1, new string('-', 1))", report);
        var program = File.ReadAllText(ws.PathOf("App", "Program.cs"));
        Assert.Contains("report.Describe(width, new string('-', width))", program);
        Assert.Contains("report.Describe(width: 2, new string('-', 2))", program);
        Assert.Empty(ws.GetDiskCompilationErrors());
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task OptionalParameters_NewOneGoesBeforeThem_NamedCallsGetANamedArgument()
    {
        using var ws = TestWorkspace.Create(("Program.cs", Lf("""
            public static class Program
            {
                public static string Banner(string text, int pad = 1)
                {
                    var marker = "*";
                    return marker + new string(' ', pad) + text;
                }

                public static string Run() => Banner("x") + "|" + Banner(pad: 2, text: "yo") + "|" + Banner("z", 3);
            }
            """)));
        var before = Run(ws);

        var result = await IntroduceAsync(ws, ws.PathOf("Program.cs"), "marker", "var marker");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("public static string Banner(string text, string marker, int pad = 1)", text);
        Assert.Contains("Banner(\"x\", \"*\") + \"|\" + Banner(pad: 2, text: \"yo\", marker: \"*\") + \"|\" + Banner(\"z\", \"*\", 3)", text);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task InitializerUsingTheInstance_CallsUseTheirReceiver()
    {
        using var ws = TestWorkspace.Create(("Program.cs", Lf("""
            public class Box
            {
                public int Size = 3;

                public int Area()
                {
                    var side = Size;
                    return side * side;
                }

                public int Twice() => Area() * 2;
            }

            public static class Program
            {
                public static string Run()
                {
                    var box = new Box { Size = 4 };
                    return box.Area() + "|" + box.Twice();
                }
            }
            """)));
        var before = Run(ws);

        var result = await IntroduceAsync(ws, ws.PathOf("Program.cs"), "side", "var side");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Warnings);
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("public int Twice() => Area(Size) * 2;", text);
        Assert.Contains("box.Area(box.Size)", text);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task GenericMethodAndExtensionCall_TypeArgumentsAndReceiverFilledIn()
    {
        using var ws = TestWorkspace.Create(("Program.cs", Lf("""
            using System.Collections.Generic;

            public static class Util
            {
                public static int Count<T>(IEnumerable<T> items)
                {
                    var list = new List<T>(items);
                    return list.Count;
                }

                public static string Shout(this string s)
                {
                    var mark = s.Length > 2 ? "!!" : "!";
                    return s.ToUpperInvariant() + mark;
                }
            }

            public static class Program
            {
                public static string Run()
                {
                    var words = new[] { "a", "b" };
                    var name = "hi";
                    return Util.Count(words) + name.Shout();
                }
            }
            """)));
        var before = Run(ws);

        var count = await IntroduceAsync(ws, ws.PathOf("Program.cs"), "list", "var list");
        var shout = await IntroduceAsync(ws, ws.PathOf("Program.cs"), "mark", "var mark");

        Assert.True(count.Success, count.Error?.Message);
        Assert.True(shout.Success, shout.Error?.Message);
        Assert.Null(count.Warnings);
        Assert.Null(shout.Warnings);
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("public static int Count<T>(IEnumerable<T> items, List<T> list)", text);
        Assert.Contains("Util.Count(words, new List<string>(words))", text);
        Assert.Contains("public static string Shout(this string s, string mark)", text);
        Assert.Contains("name.Shout(name.Length > 2 ? \"!!\" : \"!\")", text);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task RecursiveCall_GetsTheArgumentToo()
    {
        using var ws = TestWorkspace.Create(("Program.cs", Lf("""
            public static class Program
            {
                public static int Depth(int n)
                {
                    var step = 2;
                    return n <= 0 ? 0 : step + Depth(n - 1);
                }

                public static string Run() => Depth(3).ToString();
            }
            """)));
        var before = Run(ws);

        var result = await IntroduceAsync(ws, ws.PathOf("Program.cs"), "step", "var step");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Program.cs");
        Assert.Contains("public static int Depth(int n, int step)", text);
        Assert.Contains("step + Depth(n - 1, 2);", text);
        Assert.Contains("Depth(3, 2).ToString()", text);
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task ArgumentThatWouldBeEvaluatedTwiceAndMethodGroup_ReportedInWarnings()
    {
        using var ws = TestWorkspace.Create(("Report.cs", Lf(Report)), ("Use.cs", Lf("""
            using System;
            using Lib;

            public static class Use
            {
                private static int Next() => 3;

                public static string Run(Report report)
                {
                    Func<int, string> describe = report.Describe;
                    return report.Describe(Next()) + describe(1);
                }
            }
            """)));

        var result = await IntroduceAsync(ws, ws.PathOf("Report.cs"), "separator", "var separator");

        Assert.True(result.Success, result.Error?.Message);
        var warnings = result.Warnings!;
        Assert.Contains(warnings, w => w.Contains("Use.cs(10,", StringComparison.Ordinal) && w.Contains("method group", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("TODO", StringComparison.Ordinal) && w.Contains("'separator'", StringComparison.Ordinal) && w.Contains("Next()", StringComparison.Ordinal));
        Assert.Contains("report.Describe(Next(), default /* TODO: separator */)", ws.ReadFile("Use.cs"));
    }

    [Fact]
    public async Task OverrideFamily_IsRefused()
    {
        using var ws = TestWorkspace.Create(("Shape.cs", Lf("""
            public class Shape
            {
                public virtual string Draw()
                {
                    var pen = "#";
                    return pen;
                }
            }

            public class Square : Shape
            {
                public override string Draw() => "[]";
            }
            """)));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => IntroduceAsync(ws, ws.PathOf("Shape.cs"), "pen", "var pen"));

        Assert.Equal(ErrorCodes.MemberInFamily, ex.ErrorCode);
        Assert.Contains("Square.Draw()", ex.Message);
    }

    [Theory]
    [InlineData("var item = i * 2;", "item", "loop")]
    [InlineData("var total = first + 1;", "total", "'first'")]
    public async Task LocalThatCannotBeAParameter_IsRefused(string declaration, string name, string reason)
    {
        using var ws = TestWorkspace.Create(("Program.cs", Lf($$"""
            public static class Program
            {
                public static int Sum()
                {
                    var first = 1;
                    var sum = 0;
                    for (var i = 0; i < 3; i++)
                    {
                        {{(name == "item" ? declaration : "")}}
                        sum += i;
                    }

                    {{(name == "total" ? declaration : "")}}
                    return sum + first;
                }
            }
            """)));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => IntroduceAsync(ws, ws.PathOf("Program.cs"), name, declaration));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains(reason, ex.Message);
    }

    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private static Task<RefactoringResult> IntroduceAsync(TestWorkspace ws, string path, string variable, string declaration)
    {
        var lines = File.ReadAllText(path).Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains(declaration, StringComparison.Ordinal)) + 1;
        Assert.True(line > 0, $"'{declaration}' not found");
        return new IntroduceParameterOperation(ws.Context).ExecuteAsync(
            new IntroduceParameterParams { SourceFile = path, VariableName = variable, Line = line },
            Ct);
    }

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(IntroduceParameterCallSiteTests), isCollectible: true);
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
}
