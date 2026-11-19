using System.Runtime.Loader;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Inline;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// inline_method copies the method's code with the layout it is written with: the formatter used
/// to lay it out anew (`x+1` became `3 + 1`). Only its indentation changes; the arguments put in
/// for the parameters are generated and simplified as before.
/// </summary>
public sealed class InlineMethodLayoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task InlinedStatements_KeepTheirLayout_OnlyIndentationChanges(string newline)
    {
        using var ws = TestWorkspace.Create(("Program.cs", With(newline, """
            using System.Collections.Generic;

            public static class Program
            {
                private static void Log(List<string> l, int x)
                {
                    if (x > 0) l.Add("p");
                    l.Add((x+1).ToString()); // after
                    l.Add(string.Join(",",
                        x, x*2));
                }

                public static string Run()
                {
                    var list = new List<string>();
                    if (list != null)
                    {
                        Log(list, 3);
                    }

                    return string.Join("|", list);
                }
            }
            """)));
        var before = Run(ws);

        var result = await InlineAsync(ws, "Log");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(With(newline, """
                    if (list != null)
                    {
                        if (3 > 0) list.Add("p");
                        list.Add((3+1).ToString()); // after
                        list.Add(string.Join(",",
                            3, 3*2));
                    }
            """), ws.ReadFile("Program.cs"));
        Assert.Equal(before, Run(ws));
    }

    [Fact]
    public async Task InlinedExpressionBody_KeepsItsLayout()
    {
        using var ws = TestWorkspace.Create(("Program.cs", With("\n", """
            public static class Program
            {
                private static int Calc(int a) => a*2
                    + 1;

                public static string Run()
                {
                    var x = 4;
                    if (x > 0)
                    {
                        var y = Calc(x);
                        return y.ToString();
                    }

                    return "";
                }
            }
            """)));
        var before = Run(ws);

        var result = await InlineAsync(ws, "Calc");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("            var y = x*2\n                + 1;\n", ws.ReadFile("Program.cs"));
        Assert.Equal(before, Run(ws));
    }

    private static string With(string newline, string text) => text.ReplaceLineEndings(newline) + newline;

    private static Task<RefactoringResult> InlineAsync(TestWorkspace ws, string method) =>
        new InlineMethodOperation(ws.Context).ExecuteAsync(
            new InlineMethodParams { SourceFile = ws.PathOf("Program.cs"), MethodName = method },
            Ct);

    private static string Run(TestWorkspace ws)
    {
        var context = new AssemblyLoadContext(nameof(InlineMethodLayoutTests), isCollectible: true);
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
