using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// <c>2147483648</c> in <c>-2147483648</c> (and <c>9223372036854775808L</c> in
/// <c>-9223372036854775808L</c>) is a <c>uint</c> (<c>ulong</c>) on its own; only the minus
/// makes it <c>int.MinValue</c> (<c>long.MinValue</c>). extract_variable and extract_constant
/// extracted the bare operand, so <c>-x</c> became a <c>long</c> (or did not compile at all).
/// They now extract the negation with it. extract_constant also takes a selected signed
/// number (<c>-5</c>), which it reported as "no literal".
/// </summary>
public sealed class MinValueLiteralTests
{
    private const string FileName = "Limits.cs";

    private const string Source = """
        namespace App;

        public class Limits
        {
            public int Min() => -2147483648;
            public long LongMin() => -9223372036854775808L;
            public int Next() => -2147483648 + 1;
            public uint Big() => 2147483648;
            public int Neg() => -5;
            public int Pos() => 5 - -5;

            public long Sum()
            {
                long total = -9223372036854775808L;
                return total + -2147483648;
            }
        }
        """;

    [Fact]
    public async Task ExtractConstant_OperandOfIntMinValue_TakesTheMinus()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await ExtractConstantAsync(ws, "public int Min()", "2147483648", "Lowest", replaceAll: true);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Contains("private const int Lowest = -2147483648;", text);
        Assert.Contains("public int Min() => Lowest;", text);
        Assert.Contains("public int Next() => Lowest + 1;", text);
        Assert.Contains("return total + Lowest;", text);
        Assert.Contains("public uint Big() => 2147483648;", text);
        Assert.Equal(3, result.ReferencesUpdated);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractConstant_OperandOfLongMinValue_TakesTheMinus()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await ExtractConstantAsync(ws, "public long LongMin()", "9223372036854775808L", "Lowest", replaceAll: false);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Contains("private const long Lowest = -9223372036854775808L;", text);
        Assert.Contains("public long LongMin() => Lowest;", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractConstant_UintLiteral_LeavesTheNegatedOnesAlone()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await ExtractConstantAsync(ws, "public uint Big()", "2147483648", "Big2", replaceAll: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.ReferencesUpdated);
        Assert.Contains("private const uint Big2 = 2147483648;", ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task ExtractConstant_SignedNumber_IsExtractedWithItsSign()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await ExtractConstantAsync(ws, "public int Neg()", "-5", "MinusFive", replaceAll: true);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Contains("private const int MinusFive = -5;", text);
        Assert.Contains("public int Neg() => MinusFive;", text);
        Assert.Contains("public int Pos() => 5 - MinusFive;", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData(true, "var lowest = -2147483648;")]
    [InlineData(false, "int lowest = -2147483648;")]
    public async Task ExtractVariable_OperandOfIntMinValue_TakesTheMinus(bool useVar, string declaration)
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, column) = Locate("return total", "2147483648");

        var result = await new ExtractVariableOperation(ws.Context).ExecuteAsync(
            new ExtractVariableParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = line,
                StartColumn = column,
                EndLine = line,
                EndColumn = column + "2147483648".Length,
                VariableName = "lowest",
                UseVar = useVar
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Contains(declaration, text);
        Assert.Contains("return total + lowest;", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static (int Line, int Column) Locate(string line, string literal)
    {
        var lines = Source.ReplaceLineEndings("\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(line, StringComparison.Ordinal));
        var start = lines[index].Contains("=>", StringComparison.Ordinal) ? lines[index].IndexOf("=>", StringComparison.Ordinal) : 0;
        return (index + 1, lines[index].IndexOf(literal, start, StringComparison.Ordinal) + 1);
    }

    private static Task<RefactoringResult> ExtractConstantAsync(
        TestWorkspace ws, string line, string literal, string name, bool replaceAll)
    {
        var (lineNumber, column) = Locate(line, literal);
        return new ExtractConstantOperation(ws.Context).ExecuteAsync(
            new ExtractConstantParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = lineNumber,
                StartColumn = column,
                EndLine = lineNumber,
                EndColumn = column + literal.Length,
                ConstantName = name,
                ReplaceAll = replaceAll
            },
            TestContext.Current.CancellationToken);
    }
}
