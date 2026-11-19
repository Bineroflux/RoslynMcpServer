using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// extract_variable and extract_constant extract what is selected and keep it as written. A
/// selected argument used to be found as its ArgumentSyntax, from which the search only went up,
/// so extract_variable took the whole enclosing call and extract_constant found no literal; and
/// the new declaration's initializer was laid out anew by NormalizeWhitespace.
/// </summary>
public sealed class ExtractExpressionSelectionLayoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ExtractVariable_SelectedArgument_KeepsItsLayout(string newline)
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class P
            {
                public static int M(int[] items)
                {
                    if (items.Length > 0)
                    {
                        return Sum(items.Where(x => x>1)
                            .Select(x => { if (x > 2) return x; return 0; }));
                    }

                    return 0;
                }

                private static int Sum(IEnumerable<int> values) => values.Sum();
            }
            """;
        using var ws = TestWorkspace.Create(("P.cs", With(newline, source)));

        var result = await ExtractVariableAsync(ws, "items.Where", "return 0; })", "picked");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(With(newline, """
            using System.Collections.Generic;
            using System.Linq;

            public static class P
            {
                public static int M(int[] items)
                {
                    if (items.Length > 0)
                    {
                        var picked = items.Where(x => x>1)
                            .Select(x => { if (x > 2) return x; return 0; });
                        return Sum(picked);
                    }

                    return 0;
                }

                private static int Sum(IEnumerable<int> values) => values.Sum();
            }
            """), ws.ReadFile("P.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractVariable_LiteralArgument_IsTheSelection()
    {
        using var ws = TestWorkspace.Create(("P.cs", With("\n", """
            public static class P
            {
                public static void M() => System.Console.WriteLine(F(42));

                public static void N()
                {
                    System.Console.WriteLine(F(42));
                }

                private static int F(int x) => x;
            }
            """)));

        var result = await ExtractVariableAsync(ws, "42));\n    }", "42", "answer");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("        var answer = 42;\n        System.Console.WriteLine(F(answer));\n", ws.ReadFile("P.cs"));
    }

    [Fact]
    public async Task ExtractConstant_LiteralArgument_KeptAsWritten()
    {
        using var ws = TestWorkspace.Create(("P.cs", With("\n", """
            public static class P
            {
                public static int M() => F(- 1);

                private static int F(int x) => x;
            }
            """)));

        var result = await new ExtractConstantOperation(ws.Context).ExecuteAsync(
            Selection<ExtractConstantParams>(ws, "- 1", "- 1", s => new ExtractConstantParams
            {
                SourceFile = s.File,
                StartLine = s.StartLine,
                StartColumn = s.StartColumn,
                EndLine = s.EndLine,
                EndColumn = s.EndColumn,
                ConstantName = "Offset"
            }),
            Ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("P.cs");
        Assert.Contains("    private const int Offset = - 1;\n", text);
        Assert.Contains("public static int M() => F(Offset);", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static string With(string newline, string text) => text.ReplaceLineEndings(newline) + newline;

    private sealed record Span(string File, int StartLine, int StartColumn, int EndLine, int EndColumn);

    private static TParams Selection<TParams>(TestWorkspace ws, string from, string to, Func<Span, TParams> create)
    {
        var text = ws.ReadFile("P.cs");
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found");
        var end = text.IndexOf(to, start, StringComparison.Ordinal) + to.Length;
        static (int, int) Position(string text, int offset)
        {
            var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
            return (text[..lineStart].Count(c => c == '\n') + 1, offset - lineStart + 1);
        }

        var (startLine, startColumn) = Position(text, start);
        var (endLine, endColumn) = Position(text, end);
        return create(new Span(ws.PathOf("P.cs"), startLine, startColumn, endLine, endColumn));
    }

    private static Task<RefactoringResult> ExtractVariableAsync(TestWorkspace ws, string from, string to, string name) =>
        new ExtractVariableOperation(ws.Context).ExecuteAsync(
            Selection<ExtractVariableParams>(ws, from, to, s => new ExtractVariableParams
            {
                SourceFile = s.File,
                StartLine = s.StartLine,
                StartColumn = s.StartColumn,
                EndLine = s.EndLine,
                EndColumn = s.EndColumn,
                VariableName = name
            }),
            Ct);
}
