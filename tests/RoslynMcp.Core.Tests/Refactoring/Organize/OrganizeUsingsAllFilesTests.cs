using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// End-to-end coverage for <c>allFiles: true</c> on add_missing_usings and
/// remove_unused_usings. Both used to push every per-file edit into the cached context
/// mid-walk and then commit that same snapshot, so the commit saw no difference: the
/// result reported usings added/removed while nothing was written, and the cached
/// solution kept edits that were not on disk.
/// </summary>
public sealed class OrganizeUsingsAllFilesTests
{
    private const string NeedsTextUsing = """
        namespace App;

        public class First
        {
            public StringBuilder Create() => new StringBuilder();
        }
        """;

    private const string AlsoNeedsTextUsing = """
        namespace App;

        public class Second
        {
            public string Build()
            {
                var sb = new StringBuilder();
                return sb.ToString();
            }
        }
        """;

    private const string HasUnusedUsing = """
        using System;
        using System.Text;

        namespace App;

        public class Third
        {
            public string Name => nameof(Third);
        }
        """;

    private const string AlsoHasUnusedUsing = """
        using System.Collections.Generic;
        using System.Text;

        namespace App;

        public class Fourth
        {
            public List<int> Items { get; } = new();
        }
        """;

    [Fact]
    public async Task AddMissingUsings_AllFiles_WritesEveryChangedFile()
    {
        using var ws = TestWorkspace.Create(("First.cs", NeedsTextUsing), ("Second.cs", AlsoNeedsTextUsing));
        var ct = TestContext.Current.CancellationToken;

        var result = await new AddMissingUsingsOperation(ws.Context)
            .ExecuteAsync(new AddMissingUsingsParams { AllFiles = true }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.UsingDirectivesAdded);
        Assert.Equal(
            new[] { ws.PathOf("First.cs"), ws.PathOf("Second.cs") },
            result.Changes!.FilesModified.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("using System.Text;", ws.ReadFile("First.cs"));
        Assert.Contains("using System.Text;", ws.ReadFile("Second.cs"));
        Assert.Empty(ws.GetDiskCompilationErrors());
        await AssertContextMatchesDiskAsync(ws, ct);
    }

    [Fact]
    public async Task RemoveUnusedUsings_AllFiles_WritesEveryChangedFile()
    {
        using var ws = TestWorkspace.Create(("Third.cs", HasUnusedUsing), ("Fourth.cs", AlsoHasUnusedUsing));
        var ct = TestContext.Current.CancellationToken;

        var result = await new RemoveUnusedUsingsOperation(ws.Context)
            .ExecuteAsync(new RemoveUnusedUsingsParams { AllFiles = true }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.UsingDirectivesRemoved);
        Assert.Equal(
            new[] { ws.PathOf("Fourth.cs"), ws.PathOf("Third.cs") },
            result.Changes!.FilesModified.Order(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("using", ws.ReadFile("Third.cs"));
        var fourth = ws.ReadFile("Fourth.cs");
        Assert.Contains("using System.Collections.Generic;", fourth);
        Assert.DoesNotContain("using System.Text;", fourth);
        Assert.Empty(ws.GetDiskCompilationErrors());
        await AssertContextMatchesDiskAsync(ws, ct);
    }

    /// <summary>
    /// The cached context outlives the operation, so its snapshot must describe exactly
    /// what is on disk — otherwise the next tool call works on edits that were never saved.
    /// </summary>
    private static async Task AssertContextMatchesDiskAsync(TestWorkspace ws, CancellationToken ct)
    {
        foreach (var document in ws.Context.Solution.Projects.SelectMany(p => p.Documents))
        {
            var text = await document.GetTextAsync(ct);
            Assert.Equal(await File.ReadAllTextAsync(document.FilePath!, ct), text.ToString());
        }
    }
}
