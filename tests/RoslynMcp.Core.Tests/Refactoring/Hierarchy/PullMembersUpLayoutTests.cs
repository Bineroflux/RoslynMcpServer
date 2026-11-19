using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Hierarchy;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Hierarchy;

/// <summary>
/// pull_members_up moves a member as it is written: the formatter used to lay it out anew
/// (`a+1` became `a + 1`); now only its indentation changes, to its new type's.
/// </summary>
public sealed class PullMembersUpLayoutTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MovedMember_KeepsItsLayout(string newline)
    {
        using var ws = TestWorkspace.Create(
            ("Base.cs", With(newline, """
                namespace App;

                public class Shape
                {
                    public int a;
                }
                """)),
            ("Square.cs", With(newline, """
                namespace App
                {
                    public class Square : Shape
                    {
                        /// <summary>
                        /// Picks.
                        /// </summary>
                        public int Pick(int x)
                        {
                            if (x > 2) return x;

                            return a+1; // sum
                        }
                    }
                }
                """)));

        var result = await new PullMembersUpOperation(ws.Context).ExecuteAsync(
            new PullMembersUpParams { SourceFile = ws.PathOf("Square.cs"), TypeName = "Square", Members = ["Pick"] },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(With(newline, """
            namespace App;

            public class Shape
            {
                public int a;

                /// <summary>
                /// Picks.
                /// </summary>
                public int Pick(int x)
                {
                    if (x > 2) return x;

                    return a+1; // sum
                }
            }
            """), ws.ReadFile("Base.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static string With(string newline, string text) => text.ReplaceLineEndings(newline) + newline;
}
