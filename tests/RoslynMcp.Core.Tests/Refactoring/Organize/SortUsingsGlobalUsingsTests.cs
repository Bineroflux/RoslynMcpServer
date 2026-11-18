using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// End-to-end coverage that sort_usings and add_missing_usings (which re-sorts the file's
/// usings) keep <c>global using</c> directives ahead of non-global ones. The sorter used
/// to ignore the <c>global</c> keyword and could move a global using behind a normal one,
/// which does not compile (CS8915).
/// </summary>
public sealed class SortUsingsGlobalUsingsTests
{
    private const string MixedUsings = """
        using System;
        global using App.Shared;

        namespace App.Shared
        {
            public static class Clock
            {
                public static DateTime Now => DateTime.Now;
            }
        }
        """;

    private const string MissingTextUsing = """
        global using System.Collections.Generic;
        using System;

        namespace App;

        public class Builder
        {
            public List<string> Parts { get; } = new();
            public string Build() => new StringBuilder().Append(Environment.NewLine).ToString();
        }
        """;

    [Fact]
    public async Task SortUsings_KeepsGlobalUsingsFirst()
    {
        using var ws = TestWorkspace.Create(("Mixed.cs", MixedUsings));
        var ct = TestContext.Current.CancellationToken;

        var result = await new SortUsingsOperation(ws.Context)
            .ExecuteAsync(new SortUsingsParams { SourceFile = ws.PathOf("Mixed.cs") }, ct);

        Assert.True(result.Success, result.Error?.Message);
        var text = ws.ReadFile("Mixed.cs");
        Assert.True(
            text.IndexOf("global using App.Shared;", StringComparison.Ordinal)
                < text.IndexOf("using System;", StringComparison.Ordinal),
            text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task AddMissingUsings_ResortKeepsGlobalUsingsFirst()
    {
        using var ws = TestWorkspace.Create(("Builder.cs", MissingTextUsing));
        var ct = TestContext.Current.CancellationToken;

        var result = await new AddMissingUsingsOperation(ws.Context)
            .ExecuteAsync(new AddMissingUsingsParams { SourceFile = ws.PathOf("Builder.cs") }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        var text = ws.ReadFile("Builder.cs");
        Assert.StartsWith("global using System.Collections.Generic;", text);
        Assert.Contains("using System.Text;", text);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
