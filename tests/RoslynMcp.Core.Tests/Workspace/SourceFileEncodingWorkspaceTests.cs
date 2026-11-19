using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Tests.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Files that are not UTF-8 — windows-1252 with umlauts, as legacy code often is — are read in
/// their code page and written back in it, so a refactoring changes only the bytes of the
/// text it edits. They used to be read as UTF-8 by the watcher sync, the project
/// re-evaluation and the standalone loader (umlauts became U+FFFD), and every commit wrote
/// a file without byte order mark as UTF-8.
/// </summary>
public sealed class SourceFileEncodingWorkspaceTests
{
    private static readonly Encoding Windows1252 = GetWindows1252();

    private const string Legacy = "// Größe der Maße\ninternal sealed class Legacy\n{\n    public string Name => \"Grüße\";\n}\n";

    [Fact]
    public async Task SyncDocumentFromDiskAsync_Windows1252File_KeepsTheUmlauts()
    {
        using var ws = await CreateWithWindows1252FileAsync();

        var text = await ws.Context.GetDocumentByPath(ws.PathOf("Legacy.cs"))!.GetTextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Legacy, text.ToString());
        Assert.Equal(1252, text.Encoding!.CodePage);
    }

    [Fact]
    public async Task Commit_Windows1252File_KeepsItsCodePage_AlsoForTextBuiltFromAString()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = await CreateWithWindows1252FileAsync();
        var path = ws.PathOf("Legacy.cs");
        var id = ws.Context.GetDocumentByPath(path)!.Id;

        // A text built from a string has no encoding; the file keeps the one it was read in.
        var first = Legacy.Replace("class Legacy", "class Altlast", StringComparison.Ordinal);
        var commit = await ws.Context.CommitInWriterAsync(s => s.WithDocumentText(id, SourceText.From(first)), ct);

        Assert.True(commit.Success, commit.Error);
        Assert.Empty(commit.Warnings);
        Assert.Equal(Windows1252.GetBytes(first), await File.ReadAllBytesAsync(path, ct));
        Assert.Equal(1252, (await ws.Context.GetDocumentByPath(path)!.GetTextAsync(ct)).Encoding!.CodePage);

        // And the next commit still knows it: the committed text was labelled with it.
        var second = first.Replace("Grüße", "Grüße!", StringComparison.Ordinal);
        commit = await ws.Context.CommitInWriterAsync(s => s.WithDocumentText(id, SourceText.From(second)), ct);

        Assert.True(commit.Success, commit.Error);
        Assert.Equal(Windows1252.GetBytes(second), await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task Commit_CharacterTheCodePageLacks_SavesAsUtf8WithBom_AndWarns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = await CreateWithWindows1252FileAsync();
        var path = ws.PathOf("Legacy.cs");
        var id = ws.Context.GetDocumentByPath(path)!.Id;

        // windows-1252 would write '→' as '?'.
        var changed = Legacy.Replace("der Maße", "der Maße → cm", StringComparison.Ordinal);
        var commit = await ws.Context.CommitInWriterAsync(s => s.WithDocumentText(id, SourceText.From(changed)), ct);

        Assert.True(commit.Success, commit.Error);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(changed)], await File.ReadAllBytesAsync(path, ct));
        var warning = Assert.Single(commit.Warnings);
        Assert.Contains("Legacy.cs", warning);
        Assert.Contains("windows-1252", warning);
        Assert.Contains("U+2192", warning);

        // From now on the file is UTF-8 with BOM.
        var next = changed.Replace("Grüße", "Hallo", StringComparison.Ordinal);
        commit = await ws.Context.CommitInWriterAsync(s => s.WithDocumentText(id, SourceText.From(next)), ct);
        Assert.Empty(commit.Warnings);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(next)], await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task Commit_FileWithBytesInvalidForItsEncoding_WritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Broken.cs", "internal sealed class Broken { }\n"), ("Other.cs", "internal sealed class Other { }\n"));
        var broken = ws.PathOf("Broken.cs");
        var other = ws.PathOf("Other.cs");

        // A UTF-8 byte order mark in front of windows-1252 text: the umlaut reads as U+FFFD.
        byte[] brokenBytes = [0xEF, 0xBB, 0xBF, .. Windows1252.GetBytes("// Größe\ninternal sealed class Broken { }\n")];
        await File.WriteAllBytesAsync(broken, brokenBytes, ct);
        Assert.Equal(DiskSyncOutcome.Updated, await ws.Context.SyncDocumentFromDiskAsync(broken, ct));
        var otherBytes = await File.ReadAllBytesAsync(other, ct);
        var before = ws.Context.Solution;
        var edits = new List<(DocumentId Id, SourceText Text)>();
        foreach (var path in new[] { broken, other })
        {
            var document = ws.Context.GetDocumentByPath(path)!;
            var text = await document.GetTextAsync(ct);
            edits.Add((document.Id, SourceText.From(text.ToString().Replace("class", "record", StringComparison.Ordinal))));
        }

        var commit = await ws.Context.CommitInWriterAsync(
            s => edits.Aggregate(s, (solution, edit) => solution.WithDocumentText(edit.Id, edit.Text)),
            ct);

        Assert.False(commit.Success);
        Assert.Contains(broken, commit.Error);
        Assert.Equal(brokenBytes, await File.ReadAllBytesAsync(broken, ct));
        Assert.Equal(otherBytes, await File.ReadAllBytesAsync(other, ct));
        Assert.Same(before, ws.Context.Solution);
        Assert.Equal(RoslynMcp.Contracts.Enums.WorkspaceState.Ready, ws.Context.State);
    }

    [Fact]
    public async Task SyncDocumentFromDiskAsync_FileConvertedToUtf8_TakesTheNewEncoding()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = await CreateWithWindows1252FileAsync();
        var path = ws.PathOf("Legacy.cs");

        // The same text, now UTF-8 without BOM (converted in an editor).
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(Legacy), ct);
        Assert.Equal(DiskSyncOutcome.Updated, await ws.Context.SyncDocumentFromDiskAsync(path, ct));

        var document = ws.Context.GetDocumentByPath(path)!;
        var changed = Legacy.Replace("Grüße", "Grüße!", StringComparison.Ordinal);
        var commit = await ws.Context.CommitInWriterAsync(s => s.WithDocumentText(document.Id, SourceText.From(changed)), ct);

        Assert.True(commit.Success, commit.Error);
        Assert.Equal(Encoding.UTF8.GetBytes(changed), await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task InlineMethod_BringingACharacterTheCodePageLacks_ReportsTheConversionInTheResult()
    {
        SkipUnlessFallbackIsWindows1252();
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(
            ("Arrows.cs", "internal static class Arrows\n{\n    public static string Right() => \"→\";\n}\n"),
            ("Legacy.cs", "internal sealed class Placeholder { }\n"));
        var legacy = ws.PathOf("Legacy.cs");
        const string caller = "// Größe der Maße\ninternal sealed class Legacy\n{\n    public string Name => Arrows.Right();\n}\n";
        await File.WriteAllBytesAsync(legacy, Windows1252.GetBytes(caller), ct);
        Assert.Equal(DiskSyncOutcome.Updated, await ws.Context.SyncDocumentFromDiskAsync(legacy, ct));

        // The method's body comes from a UTF-8 file; the windows-1252 caller cannot hold its '→'.
        var result = await new InlineMethodOperation(ws.Context).ExecuteAsync(
            new InlineMethodParams { SourceFile = ws.PathOf("Arrows.cs"), MethodName = "Right", Line = 3 },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        var warning = Assert.Single(result.Warnings!);
        Assert.Contains("Legacy.cs", warning);
        Assert.Contains("U+2192", warning);
        var bytes = await File.ReadAllBytesAsync(legacy, ct);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(
            caller.Replace("Arrows.Right()", "\"→\"", StringComparison.Ordinal),
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task MsBuildWorkspace_Windows1252Files_AreReadAndWrittenInTheirCodePage()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        SkipUnlessFallbackIsWindows1252();
        var ct = TestContext.Current.CancellationToken;
        using var working = RenameTempSolution.Create();
        var legacy = Path.Combine(working.ProjectDir, "Legacy.cs");
        await File.WriteAllBytesAsync(legacy, Windows1252.GetBytes("namespace TestProject;\n\n" + Legacy), ct);
        using var provider = new MSBuildWorkspaceProvider();

        // The rename rewrites the file under its new name, in windows-1252.
        WorkspaceContext cached;
        RefactoringResult result;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            cached = ctx;
            Assert.Contains("Grüße", (await ctx.GetDocumentByPath(legacy)!.GetTextAsync(ct)).ToString());
            result = await new RenameSymbolOperation(ctx).ExecuteAsync(
                new RenameSymbolParams { SourceFile = legacy, SymbolName = "Legacy", NewName = "Altlast", Line = 4 },
                ct);
        }

        Assert.True(result.Success, result.Error?.Message);
        var renamed = Path.Combine(working.ProjectDir, "Altlast.cs");
        Assert.Equal(
            Windows1252.GetBytes("namespace TestProject;\n\n" + Legacy.Replace("class Legacy", "class Altlast", StringComparison.Ordinal)),
            await File.ReadAllBytesAsync(renamed, ct));

        // A file MSBuild picks up after the load is read in its code page too.
        var added = Path.Combine(working.ProjectDir, "Neu.cs");
        await File.WriteAllBytesAsync(added, Windows1252.GetBytes("namespace TestProject;\n\n// Überprüfung\ninternal sealed class Neu { }\n"), ct);

        // The cache's watcher may get there first; either way the file goes through the
        // re-evaluation. It waits for leases, so the context's is released above.
        await cached.ReconcileProjectDocumentsAsync(Path.Combine(working.ProjectDir, "TestProject.csproj"), ct);
        using var probe = await provider.CreateContextAsync(working.SolutionPath, ct);
        Assert.Contains("// Überprüfung", (await probe.GetDocumentByPath(added)!.GetTextAsync(ct)).ToString());
    }

    [Fact]
    public async Task StandaloneFile_Windows1252_KeepsTheUmlauts()
    {
        SkipUnlessFallbackIsWindows1252();
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"RoslynMcpServer.Encoding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "app.cs");
            await File.WriteAllBytesAsync(path, Windows1252.GetBytes("Console.WriteLine(\"Grüße\");\n"), ct);

            using var ctx = await StandaloneFileWorkspace.CreateAsync(path, new AtomicFileWriter(), log: null, ct);

            var text = await ctx.GetDocumentByPath(path)!.GetTextAsync(ct);
            Assert.Equal("Console.WriteLine(\"Grüße\");\n", text.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A workspace whose <c>Legacy.cs</c> holds <see cref="Legacy"/> in windows-1252, read by
    /// the watcher sync the way an external edit is.
    /// </summary>
    private static async Task<TestWorkspace> CreateWithWindows1252FileAsync()
    {
        SkipUnlessFallbackIsWindows1252();
        var ws = TestWorkspace.Create(("Legacy.cs", "internal sealed class Placeholder { }\n"));
        var path = ws.PathOf("Legacy.cs");
        await File.WriteAllBytesAsync(path, Windows1252.GetBytes(Legacy), TestContext.Current.CancellationToken);
        Assert.Equal(DiskSyncOutcome.Updated, await ws.Context.SyncDocumentFromDiskAsync(path, TestContext.Current.CancellationToken));
        return ws;
    }

    // Without a code page to go by, a file that is not UTF-8 is read in the system's ANSI code
    // page, as the compiler does; these tests are written for windows-1252.
    private static void SkipUnlessFallbackIsWindows1252() =>
        Assert.SkipUnless(
            SourceFileEncoding.FallbackCodePage.CodePage == 1252,
            $"The fallback code page is {SourceFileEncoding.FallbackCodePage.WebName}, not windows-1252.");

    private static Encoding GetWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
