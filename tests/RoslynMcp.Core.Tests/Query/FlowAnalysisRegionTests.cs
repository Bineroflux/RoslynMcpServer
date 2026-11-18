using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// End-to-end coverage of the regions analyze_data_flow / analyze_control_flow accept.
/// Both used to take the first and last of <em>every</em> statement inside the line range;
/// whenever the range ended in a nested block (or covered a whole method, whose body block
/// is itself a statement) those two were in different statement lists, Roslyn threw, and
/// the tool failed with an internal 4003 error on ordinary method bodies.
/// </summary>
public sealed class FlowAnalysisRegionTests
{
    private const string FileName = "Orders.cs";

    // Line numbers below refer to this fixture.
    private const string Source = """
        using System;

        namespace App;

        public class Orders
        {
            private readonly Func<int, int> _twice = x => x * 2;

            public int Process(int count, bool flag)
            {
                var total = count * 2;
                if (flag)
                {
                    total += 1;
                    return total;
                }
                Log(total);
                if (count > 10)
                {
                    return -1;
                }
                return total;
            }

            public int Doubled(int value) => value * 2;

            private static void Log(int value) { }
        }
        """;

    private const int TwiceInitializerLine = 7;
    private const int ProcessSignatureLine = 9;
    private const int TotalDeclarationLine = 11;
    private const int FirstIfReturnLine = 15;
    private const int FirstIfCloseLine = 16;
    private const int SecondIfReturnLine = 20;
    private const int FinalReturnLine = 22;
    private const int ProcessCloseLine = 23;
    private const int DoubledLine = 25;

    [Fact]
    public async Task DataFlow_RegionEndingInNestedBlock_Succeeds()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await AnalyzeDataFlowAsync(ws, TotalDeclarationLine, FirstIfCloseLine);

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.Contains("count", data.ReadInside);
        Assert.Contains("flag", data.ReadInside);
        Assert.Contains("total", data.WrittenInside);
        Assert.Contains("total", data.DataFlowsOut);
        Assert.Equal(new[] { "count", "flag" }, data.DataFlowsIn.Order());
    }

    [Fact]
    public async Task DataFlow_WholeMethod_AnalyzesItsBody()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await AnalyzeDataFlowAsync(ws, ProcessSignatureLine, ProcessCloseLine);

        Assert.True(result.Success);
        Assert.Equal(new[] { "count", "flag" }, result.Data!.DataFlowsIn.Order());
        Assert.Contains("total", result.Data.WrittenInside);
    }

    [Fact]
    public async Task DataFlow_RegionAcrossDifferentBlocks_ReturnsInvalidRegion()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            AnalyzeDataFlowAsync(ws, FirstIfReturnLine, SecondIfReturnLine));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
        Assert.Contains("different blocks", ex.Message);
    }

    [Fact]
    public async Task DataFlow_ExpressionBodiedMember_AnalyzesTheExpression()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await AnalyzeDataFlowAsync(ws, DoubledLine, DoubledLine);

        Assert.True(result.Success);
        Assert.Equal(new[] { "value" }, result.Data!.ReadInside);
        Assert.Equal(new[] { "value" }, result.Data.DataFlowsIn);
    }

    [Fact]
    public async Task DataFlow_FieldInitializer_AnalyzesTheInitializer()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await AnalyzeDataFlowAsync(ws, TwiceInitializerLine, TwiceInitializerLine);

        Assert.True(result.Success);
        Assert.Contains("x", result.Data!.ReadInside);
    }

    [Fact]
    public async Task ControlFlow_RegionEndingInNestedBlock_Succeeds()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await AnalyzeControlFlowAsync(ws, TotalDeclarationLine, FirstIfCloseLine);

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.True(data.EndPointReachable);
        Assert.Equal(new[] { FirstIfReturnLine }, data.ReturnStatements.Select(r => r.Line));
    }

    [Fact]
    public async Task ControlFlow_WholeMethod_ReportsEveryReturn()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var result = await AnalyzeControlFlowAsync(ws, ProcessSignatureLine, ProcessCloseLine);

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.False(data.EndPointReachable);
        Assert.Equal(
            new[] { FirstIfReturnLine, SecondIfReturnLine, FinalReturnLine },
            data.ReturnStatements.Select(r => r.Line).Order());
    }

    [Fact]
    public async Task ControlFlow_RegionAcrossDifferentBlocks_ReturnsInvalidRegion()
    {
        using var ws = TestWorkspace.Create((FileName, Source));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            AnalyzeControlFlowAsync(ws, FirstIfReturnLine, SecondIfReturnLine));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
    }

    private static Task<QueryResult<AnalyzeDataFlowResult>> AnalyzeDataFlowAsync(
        TestWorkspace ws, int startLine, int endLine) =>
        new AnalyzeDataFlowOperation(ws.Context).ExecuteAsync(
            new AnalyzeDataFlowParams { SourceFile = ws.PathOf(FileName), StartLine = startLine, EndLine = endLine },
            TestContext.Current.CancellationToken);

    private static Task<QueryResult<AnalyzeControlFlowResult>> AnalyzeControlFlowAsync(
        TestWorkspace ws, int startLine, int endLine) =>
        new AnalyzeControlFlowOperation(ws.Context).ExecuteAsync(
            new AnalyzeControlFlowParams { SourceFile = ws.PathOf(FileName), StartLine = startLine, EndLine = endLine },
            TestContext.Current.CancellationToken);
}
