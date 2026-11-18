using System.Text;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// A file that a refactoring writes under a new name (rename_symbol's renameFile) or splits
/// off an existing one (move_type_to_file) was written as a brand-new file, which the
/// writer creates as UTF-8 with BOM: a BOM-less file came out with one. It now takes the
/// encoding of the file it comes from, and move_type_to_file also uses that file's line
/// ending instead of a hard-coded LF.
/// </summary>
public sealed class NewFileEncodingTests
{
    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\r\n")]
    public async Task RenameFile_KeepsTheEncodingAndLineEndings(bool bom, string eol)
    {
        var widget = "namespace App;\n\npublic class Widget\n{\n}\n".Replace("\n", eol);
        using var ws = TestWorkspace.Create(("Widget.cs", widget));
        Rewrite(ws.PathOf("Widget.cs"), widget, bom);

        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Widget", NewName = "Gizmo", Line = 3 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(File.Exists(ws.PathOf("Widget.cs")));
        AssertFormat(ws.PathOf("Gizmo.cs"), bom, eol);
    }

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\r\n")]
    public async Task MoveTypeToFile_NewFileTakesTheSourcesEncodingAndLineEndings(bool bom, string eol)
    {
        var models = "using System;\n\nnamespace App;\n\npublic class User\n{\n}\n\npublic class Address\n{\n}\n".Replace("\n", eol);
        using var ws = TestWorkspace.Create(("Models.cs", models));
        Rewrite(ws.PathOf("Models.cs"), models, bom);

        await MoveTypeToFileAsync(ws, "Address", "Models.cs", "Address.cs");

        AssertFormat(ws.PathOf("Address.cs"), bom, eol);
        AssertFormat(ws.PathOf("Models.cs"), bom, eol);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveTypeToFile_SourceDeletedInTheSameCommit_StillLendsItsEncoding(bool bom)
    {
        // Zed.cs sorts after Models.cs, so the commit deletes the source before it writes the
        // new file: the encoding has to be read up front.
        const string Models = "namespace App;\n\npublic class Zed\n{\n}\n";
        using var ws = TestWorkspace.Create(("Models.cs", Models));
        Rewrite(ws.PathOf("Models.cs"), Models, bom);

        await MoveTypeToFileAsync(ws, "Zed", "Models.cs", "Zed.cs");

        Assert.False(File.Exists(ws.PathOf("Models.cs")));
        AssertFormat(ws.PathOf("Zed.cs"), bom, "\n");
    }

    private static async Task MoveTypeToFileAsync(TestWorkspace ws, string typeName, string source, string target)
    {
        var result = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf(source), SymbolName = typeName, TargetFile = ws.PathOf(target) },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
    }

    private static void Rewrite(string path, string text, bool bom) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));

    private static void AssertFormat(string path, bool bom, string eol)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(bom, bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));

        var text = Encoding.UTF8.GetString(bytes);
        if (eol == "\n")
            Assert.DoesNotContain("\r", text);
        else
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }
}
