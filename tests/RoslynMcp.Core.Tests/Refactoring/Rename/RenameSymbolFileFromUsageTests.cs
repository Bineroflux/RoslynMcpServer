using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// rename_symbol's <c>renameFile</c> looked at the file the symbol was found in. Started
/// from a usage in another file, the type was renamed but its own file kept the old name.
/// </summary>
public sealed class RenameSymbolFileFromUsageTests
{
    [Fact]
    public async Task RenameFromAUsage_RenamesTheDeclaringFile()
    {
        using var ws = TestWorkspace.Create(
            ("Widget.cs", "namespace App;\n\npublic class Widget\n{\n}\n"),
            ("User.cs", "namespace App;\n\npublic class User\n{\n    public Widget Item { get; } = new();\n}\n"));

        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("User.cs"), SymbolName = "Widget", NewName = "Gizmo", Line = 5 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);
        Assert.False(File.Exists(ws.PathOf("Widget.cs")));
        Assert.Contains("public class Gizmo", ws.ReadFile("Gizmo.cs"));
        Assert.Contains("public Gizmo Item", ws.ReadFile("User.cs"));
        Assert.Contains(result.Changes!.FilesCreated, f => PathResolver.PathsEqual(f, ws.PathOf("Gizmo.cs")));
        Assert.Contains(result.Changes.FilesDeleted, f => PathResolver.PathsEqual(f, ws.PathOf("Widget.cs")));
        Assert.Equal(ws.PathOf("Widget.cs"), result.Symbol?.PreviousLocation?.File);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }
}
