using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Convert;

/// <summary>
/// convert_expression_body changes only the body. It used to run NormalizeWhitespace over the
/// whole member and never put it back in place: the member landed at column 0 with the type's
/// closing brace glued to it, and wrapped expressions were joined.
/// </summary>
public sealed class ConvertExpressionBodyLayoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ToExpressionBody_KeepsMemberAndExpressionLayout(string newline)
    {
        using var ws = Create(newline, """
            using System.Linq;

            namespace App
            {
                public class C
                {
                    /// <summary>Counts.</summary>
                    [System.Obsolete]
                    public int Count(int[] items)
                    {
                        return items.Where(x => x>1)
                            .Count(); // big ones
                    }

                    public int Other() => 1;
                }
            }
            """);

        var result = await ConvertAsync(ws, "Count", "ToExpressionBody");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(With(newline, """
            using System.Linq;

            namespace App
            {
                public class C
                {
                    /// <summary>Counts.</summary>
                    [System.Obsolete]
                    public int Count(int[] items) => items.Where(x => x>1)
                        .Count(); // big ones

                    public int Other() => 1;
                }
            }
            """), ws.ReadFile("C.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ToBlockBody_LaysOutTheBlock_KeepsTheExpression(string newline)
    {
        using var ws = Create(newline, """
            using System.Linq;

            namespace App
            {
                public class C
                {
                    public int Count(int[] items) => items.Where(x => x>1)
                        .Count();

                    public int Other() => 1;
                }
            }
            """);

        var result = await ConvertAsync(ws, "Count", "ToBlockBody");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(With(newline, """
            using System.Linq;

            namespace App
            {
                public class C
                {
                    public int Count(int[] items)
                    {
                        return items.Where(x => x>1)
                            .Count();
                    }

                    public int Other() => 1;
                }
            }
            """), ws.ReadFile("C.cs"));
    }

    [Fact]
    public async Task Property_BothWays()
    {
        using var ws = Create("\n", """
            public class C
            {
                private int _a, _b;

                public int Size { get { return _a+_b; } }
            }
            """);

        var toExpression = await ConvertAsync(ws, "Size", "ToExpressionBody");
        var afterExpression = ws.ReadFile("C.cs");
        var toBlock = await ConvertAsync(ws, "Size", "ToBlockBody");

        Assert.True(toExpression.Success && toBlock.Success);
        Assert.Contains("    public int Size => _a+_b;\n}", afterExpression);
        Assert.Contains("""
                public int Size
                {
                    get
                    {
                        return _a+_b;
                    }
                }
            }
            """.ReplaceLineEndings("\n"), ws.ReadFile("C.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData("public void Log() => System.Console.WriteLine(\"x\");", "System.Console.WriteLine(\"x\");")]
    [InlineData("public async Task RunAsync() => await Task.Delay(1);", "await Task.Delay(1);")]
    [InlineData("public int Fail() => throw new System.Exception();", "throw new System.Exception();")]
    [InlineData("public async Task<int> GetAsync() => await Task.FromResult(1);", "return await Task.FromResult(1);")]
    public async Task ToBlockBody_StatementFitsTheMember(string member, string statement)
    {
        using var ws = Create("\n", $$"""
            using System.Threading.Tasks;

            public class C
            {
                {{member}}
            }
            """);

        var name = member.Split('(')[0].Split(' ')[^1];
        var result = await ConvertAsync(ws, name, "ToBlockBody");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains($"    {{\n        {statement}\n    }}\n}}", ws.ReadFile("C.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task CommentThatWouldBeLost_IsRefused()
    {
        using var ws = Create("\n", """
            public class C
            {
                public int One()
                {
                    // the answer
                    return 1;
                }
            }
            """);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => ConvertAsync(ws, "One", "ToExpressionBody"));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("comments", ex.Message);
    }

    private static string With(string newline, string text) => text.ReplaceLineEndings(newline) + newline;

    private static TestWorkspace Create(string newline, string source) => TestWorkspace.Create(("C.cs", With(newline, source)));

    private static Task<RefactoringResult> ConvertAsync(TestWorkspace ws, string member, string direction) =>
        new ConvertExpressionBodyOperation(ws.Context).ExecuteAsync(
            new ConvertExpressionBodyParams { SourceFile = ws.PathOf("C.cs"), MemberName = member, Direction = direction },
            Ct);
}
