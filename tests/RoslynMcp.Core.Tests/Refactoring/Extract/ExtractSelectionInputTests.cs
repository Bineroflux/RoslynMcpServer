using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// End-to-end input handling of extract_constant, extract_variable and extract_method.
/// extract_constant / extract_variable indexed the source lines unchecked, so an
/// out-of-range position surfaced as an internal error and a column past the end of a
/// line silently spilled into the next one; extract_variable accepted an empty selection
/// and dropped the extracted expression's trivia (fusing the new name with a following
/// <c>is</c>/<c>as</c>/<c>switch</c>); none of the three refused <c>_</c>, which would
/// capture every discard in scope.
/// </summary>
public sealed class ExtractSelectionInputTests
{
    private const string FileName = "Calculator.cs";

    private const string Source = """
        namespace App;

        public class Calculator
        {
            public int Total(int count)
            {
                var price = count * 42;
                return price;
            }

            public object GetValue() => "x";

            public int Length()
            {
                if (GetValue() is string s)
                    return s.Length;
                var text = GetValue() as string;
                return text?.Length ?? 0;
            }

            public string Describe(int count)
            {
                return Compute(count) switch { 0 => "none", _ => "some" };
            }

            private int Compute(int c) => c;
        }
        """;

    #region extract_constant

    [Fact]
    public async Task ExtractConstant_EndLinePastEndOfFile_ReturnsInvalidLineNumber()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, _) = Locate("42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractConstantAsync(ws, line, start, LineCount + 5, 1, "Rate"));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public async Task ExtractConstant_EndColumnPastEndOfLine_ReturnsInvalidColumnNumber()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, _) = Locate("42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractConstantAsync(ws, line, start, line, LineLength(line) + 10, "Rate"));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public async Task ExtractConstant_EndBeforeStart_ReturnsInvalidSelectionRange()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractConstantAsync(ws, line, end, line, start, "Rate"));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public async Task ExtractConstant_DiscardName_ReturnsInvalidSymbolName()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractConstantAsync(ws, line, start, line, end, "_"));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
        Assert.Equal(Source, ws.ReadFile(FileName));
    }

    [Fact]
    public async Task ExtractConstant_ValidSelection_StillWorks()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("42");

        var result = await ExtractConstantAsync(ws, line, start, line, end, "Rate");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("count * Rate", ws.ReadFile(FileName));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    #endregion

    #region extract_variable

    [Fact]
    public async Task ExtractVariable_EndLinePastEndOfFile_ReturnsInvalidLineNumber()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, _) = Locate("count * 42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractVariableAsync(ws, line, start, LineCount + 5, 1, "product"));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public async Task ExtractVariable_EndColumnPastEndOfLine_ReturnsInvalidColumnNumber()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, _) = Locate("count * 42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractVariableAsync(ws, line, start, line, LineLength(line) + 10, "product"));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public async Task ExtractVariable_EmptySelection_ReturnsInvalidSelectionRange()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, _) = Locate("count * 42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractVariableAsync(ws, line, start, line, start, "product"));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
        Assert.Equal(Source, ws.ReadFile(FileName));
    }

    [Fact]
    public async Task ExtractVariable_DiscardName_ReturnsInvalidSymbolName()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("count * 42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            ExtractVariableAsync(ws, line, start, line, end, "_"));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
        Assert.Equal(Source, ws.ReadFile(FileName));
    }

    [Theory]
    [InlineData("GetValue() is string s", "GetValue()", "value is string s")]
    [InlineData("GetValue() as string", "GetValue()", "value as string")]
    [InlineData("Compute(count) switch", "Compute(count)", "value switch")]
    public async Task ExtractVariable_ExpressionFollowedByKeyword_KeepsSeparatingSpace(
        string context, string selected, string expected)
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, contextStart, _) = Locate(context);
        var start = contextStart;
        var end = start + selected.Length;

        var result = await ExtractVariableAsync(ws, line, start, line, end, "value");

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile(FileName);
        Assert.Contains(expected, text);
        Assert.Contains($"var value = {selected};", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    #endregion

    #region extract_method

    [Fact]
    public async Task ExtractMethod_DiscardName_ReturnsInvalidNewName()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, end) = Locate("var price = count * 42;");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new ExtractMethodOperation(ws.Context).ExecuteAsync(
                new ExtractMethodParams
                {
                    SourceFile = ws.PathOf(FileName),
                    StartLine = line,
                    StartColumn = start,
                    EndLine = line,
                    EndColumn = end,
                    MethodName = "_"
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.InvalidNewName, ex.ErrorCode);
        Assert.Equal(Source, ws.ReadFile(FileName));
    }

    [Fact]
    public async Task ExtractMethod_EndColumnPastEndOfLine_ReturnsInvalidColumnNumber()
    {
        using var ws = TestWorkspace.Create((FileName, Source));
        var (line, start, _) = Locate("var price = count * 42;");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new ExtractMethodOperation(ws.Context).ExecuteAsync(
                new ExtractMethodParams
                {
                    SourceFile = ws.PathOf(FileName),
                    StartLine = line,
                    StartColumn = start,
                    EndLine = line,
                    EndColumn = LineLength(line) + 10,
                    MethodName = "ComputePrice"
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    #endregion

    private static string[] Lines => Source.ReplaceLineEndings("\n").Split('\n');

    private static int LineCount => Lines.Length;

    private static int LineLength(int line) => Lines[line - 1].Length;

    /// <summary>
    /// Finds the unique line containing <paramref name="token"/> and returns its 1-based
    /// line, start column, and exclusive end column.
    /// </summary>
    private static (int Line, int Start, int End) Locate(string token)
    {
        var matches = Lines
            .Select((text, index) => (Text: text, Line: index + 1))
            .Where(l => l.Text.Contains(token, StringComparison.Ordinal))
            .ToList();
        var match = Assert.Single(matches);
        var column = match.Text.IndexOf(token, StringComparison.Ordinal) + 1;
        return (match.Line, column, column + token.Length);
    }

    private static Task<RefactoringResult> ExtractConstantAsync(
        TestWorkspace ws, int startLine, int startColumn, int endLine, int endColumn, string name) =>
        new ExtractConstantOperation(ws.Context).ExecuteAsync(
            new ExtractConstantParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = startLine,
                StartColumn = startColumn,
                EndLine = endLine,
                EndColumn = endColumn,
                ConstantName = name
            },
            TestContext.Current.CancellationToken);

    private static Task<RefactoringResult> ExtractVariableAsync(
        TestWorkspace ws, int startLine, int startColumn, int endLine, int endColumn, string name) =>
        new ExtractVariableOperation(ws.Context).ExecuteAsync(
            new ExtractVariableParams
            {
                SourceFile = ws.PathOf(FileName),
                StartLine = startLine,
                StartColumn = startColumn,
                EndLine = endLine,
                EndColumn = endColumn,
                VariableName = name
            },
            TestContext.Current.CancellationToken);
}
