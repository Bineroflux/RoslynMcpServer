using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// extract_constant's <c>replaceAll</c> matched literals by their token's value text, so
/// <c>1</c>, <c>1L</c>, <c>1u</c>, <c>1.0</c> and <c>"1"</c> all counted as the extracted
/// <c>1</c> and were replaced by a constant of another type (changing overloads, or not
/// compiling). It now matches the constant's type and value.
/// </summary>
public sealed class ExtractConstantValueTests
{
    private const string FileName = "Rates.cs";

    private const string Source = """
        namespace App;

        public class Rates
        {
            public int A() => 1;
            public long B() => 1L;
            public double C() => 1.0;
            public double D() => 1.00;
            public float E() => 1f;
            public uint F() => 1u;
            public string G() => "1";
            public char H() => '1';
            public int I(int x = 1) => x + 1;
        }
        """;

    [Fact]
    public async Task ReplaceAll_IntLiteral_ReplacesOnlyIntOnes()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await ExtractAsync(ws, "public int A() => 1;", "1", "One");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.ReferencesUpdated);
        Assert.Equal("""
            namespace App;

            public class Rates
            {
                private const int One = 1;

                public int A() => One;
                public long B() => 1L;
                public double C() => 1.0;
                public double D() => 1.00;
                public float E() => 1f;
                public uint F() => 1u;
                public string G() => "1";
                public char H() => '1';
                public int I(int x = One) => x + One;
            }
            """, ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("public double C() => 1.0;", "1.0", "private const double One = 1.0;", "public double D() => One;")]
    [InlineData("public long B() => 1L;", "1L", "private const long One = 1L;", "public int A() => 1;")]
    [InlineData("public string G() => \"1\";", "\"1\"", "private const string One = \"1\";", "public char H() => '1';")]
    public async Task ReplaceAll_MatchesTypeAndValue(string line, string literal, string declaration, string expected)
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await ExtractAsync(ws, line, literal, "One");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Contains(declaration, text);
        Assert.Contains(expected, text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static Task<RefactoringResult> ExtractAsync(TestWorkspace ws, string line, string literal, string name)
    {
        var lines = Source.ReplaceLineEndings("\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(line, StringComparison.Ordinal));
        var column = lines[index].IndexOf(literal, lines[index].IndexOf("=>", StringComparison.Ordinal), StringComparison.Ordinal) + 1;

        return new ExtractConstantOperation(ws.Context).ExecuteAsync(
            new ExtractConstantParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = index + 1,
                StartColumn = column,
                EndLine = index + 1,
                EndColumn = column + literal.Length,
                ConstantName = name,
                ReplaceAll = true
            },
            TestContext.Current.CancellationToken);
    }
}
