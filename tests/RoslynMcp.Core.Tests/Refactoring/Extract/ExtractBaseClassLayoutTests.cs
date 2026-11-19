using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Extract;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// extract_base_class moves members as they are written. They went through NormalizeWhitespace
/// with the new class, which split single-line statements, expanded single-line blocks and
/// re-spaced expressions; now only their indentation changes.
/// </summary>
public sealed class ExtractBaseClassLayoutTests
{
    private const string Shape = """
        namespace App
        {
            public class Shape
            {
                #region Fields
                private int a;
                #endregion

                /// <summary>
                /// Picks.
                /// </summary>
                public int Pick(int x)
                {
                    if (x > 2) return x;
                    { a++; a--; }

                    return a+1; // sum
                }

                public int Other() => 1;
            }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task SameFile_MembersKeepTheirLayout(string newline)
    {
        using var ws = TestWorkspace.Create(("Shape.cs", With(newline, Shape)));

        var result = await ExtractAsync(ws, targetFile: null);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(With(newline, """
            namespace App
            {
                public class ShapeBase
                {
                    protected int a;

                    /// <summary>
                    /// Picks.
                    /// </summary>
                    public int Pick(int x)
                    {
                        if (x > 2) return x;
                        { a++; a--; }

                        return a+1; // sum
                    }
                }

                public class Shape : ShapeBase
                {
                    #region Fields
                    #endregion

                    public int Other() => 1;
                }
            }
            """), ws.ReadFile("Shape.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task NewFile_MembersReindentedToItsNamespace()
    {
        using var ws = TestWorkspace.Create(("Shape.cs", With("\n", Shape)));

        var result = await ExtractAsync(ws, targetFile: ws.PathOf("ShapeBase.cs"));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("""
            namespace App;

            public class ShapeBase
            {
                protected int a;

                /// <summary>
                /// Picks.
                /// </summary>
                public int Pick(int x)
                {
                    if (x > 2) return x;
                    { a++; a--; }

                    return a+1; // sum
                }
            }

            """.ReplaceLineEndings("\n"), File.ReadAllText(ws.PathOf("ShapeBase.cs")).ReplaceLineEndings("\n"));
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    private static string With(string newline, string text) => text.ReplaceLineEndings(newline) + newline;

    private static Task<RefactoringResult> ExtractAsync(TestWorkspace ws, string? targetFile) =>
        new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams
            {
                SourceFile = ws.PathOf("Shape.cs"),
                TypeName = "Shape",
                BaseClassName = "ShapeBase",
                Members = ["a", "Pick"],
                TargetFile = targetFile
            },
            Ct);
}
