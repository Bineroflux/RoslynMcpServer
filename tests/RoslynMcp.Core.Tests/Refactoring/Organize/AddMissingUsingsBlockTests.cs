using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Layout of the using block add_missing_usings writes. In a file without usings, the new
/// block used to be followed directly by the namespace declaration, with no blank line.
/// </summary>
public sealed class AddMissingUsingsBlockTests
{
    [Fact]
    public async Task FileWithoutUsings_NewBlockIsFollowedByBlankLine()
    {
        var text = await AddMissingUsingsAsync("""
            namespace App
            {
                public class Bag
                {
                    public List<int> Items { get; } = new List<int>();
                }
            }
            """);

        Assert.StartsWith("using System.Collections.Generic;\n\nnamespace App\n{\n", text);
    }

    [Fact]
    public async Task FileWithUsings_KeepsExistingSeparation()
    {
        var text = await AddMissingUsingsAsync("""
            using System;

            namespace App
            {
                public class Bag
                {
                    public List<int> Items { get; } = new List<int>();
                    public DateTime Created { get; } = DateTime.Now;
                }
            }
            """);

        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n\nnamespace App\n{\n", text);
    }

    private static async Task<string> AddMissingUsingsAsync(string source)
    {
        using var ws = TestWorkspace.Create(("Bag.cs", source.ReplaceLineEndings("\n")));

        var result = await new AddMissingUsingsOperation(ws.Context).ExecuteAsync(
            new AddMissingUsingsParams { SourceFile = ws.PathOf("Bag.cs") },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        Assert.Empty(ws.GetDiskCompilationErrors());
        return ws.ReadFile("Bag.cs");
    }
}
