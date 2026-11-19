using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Rename;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Files created from scratch (extract_interface / extract_base_class into a new file) were
/// always UTF-8 with BOM, and split-off files (move_type_to_file) took their source's encoding,
/// whatever the <c>.editorconfig</c> said. A new file now takes the <c>charset</c> of the
/// <c>.editorconfig</c> that applies to its path, else the encoding of the file its content
/// comes from, else the encoding most of the project's files are in, else UTF-8 with BOM.
/// Its line ending follows <c>end_of_line</c>, else the project's files (it was the platform's).
/// </summary>
public sealed class NewFileConventionsTests
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    private const string Dog = "namespace App;\n\npublic class Dog\n{\n    public void Bark() { }\n\n    public int Größe() => 1;\n}\n";

    private const string Cat = "namespace App;\n\n// Kätzchen\npublic class Cat\n{\n}\n";

    [Fact]
    public async Task NewFile_TakesTheEditorConfigCharsetOfItsFolder()
    {
        using var ws = TestWorkspace.Create(("Dog.cs", Dog), ("Cat.cs", Cat));
        await ReadEncodingsFromDiskAsync(ws); // BOM-less, as File.WriteAllText writes them
        await AddEditorConfigAsync(ws, "Plain/.editorconfig", "[*.cs]\ncharset = utf-8\n");
        await AddEditorConfigAsync(ws, "Marked/.editorconfig", "[*.cs]\ncharset = utf-8-bom\n");

        await ExtractInterfaceAsync(ws, "IDog", "Plain/IDog.cs");
        await ExtractInterfaceAsync(ws, "IBarker", "Marked/IBarker.cs");

        AssertFormat(ws.PathOf("Plain/IDog.cs"), bom: false, "\n");
        AssertFormat(ws.PathOf("Marked/IBarker.cs"), bom: true, "\n");
        Assert.Contains("int Größe();", File.ReadAllText(ws.PathOf("Marked/IBarker.cs")));
    }

    [Fact]
    public async Task NewFile_Latin1Charset_IsLatin1_OrUtf8WithBomAndAWarningForACharacterLatin1Lacks()
    {
        const string Shape = "namespace App;\n\npublic class Shape\n{\n    public int Größe() => 1;\n\n    public int Λ() => 2;\n}\n";
        using var ws = TestWorkspace.Create(
            [("Shape.cs", Shape)], [], editorConfig: "root = true\n\n[*.cs]\ncharset = latin1\n");

        var latin = await ExtractInterfaceAsync(ws, "ISized", "ISized.cs", members: ["Größe"]);
        var greek = await ExtractInterfaceAsync(ws, "ILettered", "ILettered.cs", members: ["Λ"]);

        Assert.Null(latin.Warnings);
        var bytes = File.ReadAllBytes(ws.PathOf("ISized.cs"));
        var text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("int Größe();", text);
        Assert.Equal(Encoding.Latin1.GetBytes(text), bytes);

        var warning = Assert.Single(greek.Warnings!);
        Assert.Contains("ILettered.cs", warning);
        Assert.Contains("iso-8859-1", warning);
        Assert.Contains("U+039B", warning);
        AssertFormat(ws.PathOf("ILettered.cs"), bom: true, "\n");
        Assert.Contains("int Λ();", File.ReadAllText(ws.PathOf("ILettered.cs")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveTypeToFile_EditorConfigCharset_WinsOverTheSourcesEncoding(bool sourceBom)
    {
        const string Models = "namespace App;\n\npublic class User\n{\n}\n\npublic class Address\n{\n}\n";
        using var ws = TestWorkspace.Create(
            [("Models.cs", Models)], [], editorConfig: $"root = true\n\n[*.cs]\ncharset = {(sourceBom ? "utf-8" : "utf-8-bom")}\n");
        Rewrite(ws.PathOf("Models.cs"), Models, sourceBom);

        var result = await new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf("Models.cs"), SymbolName = "Address", TargetFile = ws.PathOf("Address.cs") },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        AssertFormat(ws.PathOf("Address.cs"), !sourceBom, "\n");
        AssertFormat(ws.PathOf("Models.cs"), sourceBom, "\n"); // an existing file keeps its own
    }

    [Fact]
    public async Task RenamedFile_KeepsItsEncoding_WhateverTheEditorConfigSays()
    {
        // A renamed file is the same file, not a new one: like every file a refactoring edits,
        // it keeps its encoding (an IDE renaming a file does not convert it either).
        const string Widget = "namespace App;\n\npublic class Widget\n{\n}\n";
        using var ws = TestWorkspace.Create(
            [("Widget.cs", Widget)], [], editorConfig: "root = true\n\n[*.cs]\ncharset = utf-8-bom\n");

        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Widget", NewName = "Gizmo", Line = 3 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        AssertFormat(ws.PathOf("Gizmo.cs"), bom: false, "\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewFile_WithoutEditorConfig_TakesTheEncodingMostOfTheProjectsFilesAreIn(bool bom)
    {
        // Two files one way, one the other; the files MSBuild generates into obj/ don't count.
        const string Bird = "namespace App;\n\npublic class Bird\n{\n}\n";
        using var ws = TestWorkspace.Create(
            ("Dog.cs", Dog),
            ("Cat.cs", Cat),
            ("Bird.cs", Bird),
            ("obj/Debug/App.AssemblyInfo.cs", "// generated\n"),
            ("obj/Debug/App.GlobalUsings.g.cs", "// generated\n"));
        Rewrite(ws.PathOf("Dog.cs"), Dog, bom);
        Rewrite(ws.PathOf("Cat.cs"), Cat, bom);
        Rewrite(ws.PathOf("Bird.cs"), Bird, !bom);
        Rewrite(ws.PathOf("obj/Debug/App.AssemblyInfo.cs"), "// generated\n", !bom);
        Rewrite(ws.PathOf("obj/Debug/App.GlobalUsings.g.cs"), "// generated\n", !bom);
        await ReadEncodingsFromDiskAsync(ws);

        await ExtractInterfaceAsync(ws, "IDog", "IDog.cs");

        AssertFormat(ws.PathOf("IDog.cs"), bom, "\n");
    }

    [Fact]
    public async Task NewFile_InAProjectOfWindows1252Files_IsWindows1252()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Dog.cs", Dog), ("Cat.cs", Cat));
        await File.WriteAllBytesAsync(ws.PathOf("Dog.cs"), Windows1252.GetBytes(Dog), ct);
        await File.WriteAllBytesAsync(ws.PathOf("Cat.cs"), Windows1252.GetBytes(Cat), ct);
        await ReadEncodingsFromDiskAsync(ws, projectEncoding: Windows1252);

        await ExtractInterfaceAsync(ws, "IDog", "IDog.cs");

        var bytes = File.ReadAllBytes(ws.PathOf("IDog.cs"));
        var text = Windows1252.GetString(bytes);
        Assert.Contains("int Größe();", text);
        Assert.Equal(Windows1252.GetBytes(text), bytes);
        var document = ws.Context.GetDocumentByPath(ws.PathOf("IDog.cs"))!;
        Assert.Equal(1252, (await document.GetTextAsync(ct)).Encoding!.CodePage);
    }

    [Fact]
    public async Task NewFile_NoEncodingPrevails_IsUtf8WithBom()
    {
        using var ws = TestWorkspace.Create(("Dog.cs", Dog), ("Cat.cs", Cat));
        Rewrite(ws.PathOf("Dog.cs"), Dog, bom: false);
        Rewrite(ws.PathOf("Cat.cs"), Cat, bom: true);
        await ReadEncodingsFromDiskAsync(ws);

        await ExtractInterfaceAsync(ws, "IDog", "IDog.cs");

        AssertFormat(ws.PathOf("IDog.cs"), bom: true, "\n");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task NewFile_WithoutEditorConfig_TakesTheLineEndingOfTheProjectsFiles(string eol)
    {
        // It was Environment.NewLine, whatever the project's files used.
        using var ws = TestWorkspace.Create(("Dog.cs", Dog.Replace("\n", eol)), ("Cat.cs", Cat.Replace("\n", eol)));

        await ExtractInterfaceAsync(ws, "IDog", "IDog.cs");

        AssertFormat(ws.PathOf("IDog.cs"), bom: true, eol);
    }

    [Fact]
    public async Task NewFile_SpelledUnlikeTheEditorConfigsPath_StillFollowsIt()
    {
        // Roslyn matches .editorconfig directories ordinally; a caller's target path may differ
        // in letter case, separators or 8.3 short names (e.g. PROGRA~1 for Program Files).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Spelling a path in other letter case needs a case-insensitive file system.");
        using var ws = TestWorkspace.Create(
            [("Dog.cs", Dog)], [], editorConfig: "root = true\n\n[*.cs]\ncharset = utf-8\nend_of_line = lf\n");
        var target = Path.Combine(ws.Directory.ToUpperInvariant(), "IDog.cs").Replace('\\', '/');

        await ExtractInterfaceAsync(ws, "IDog", target);

        AssertFormat(ws.PathOf("IDog.cs"), bom: false, "\n");
    }

    [Fact]
    public async Task ExtractBaseClass_NewFile_FollowsTheEditorConfig()
    {
        using var ws = TestWorkspace.Create(
            [("Dog.cs", Dog), ("Cat.cs", Cat.Replace("\n", "\r\n")), ("Bird.cs", "class Bird\r\n{\r\n}\r\n")],
            [],
            editorConfig: "root = true\n\n[*.cs]\ncharset = utf-8\nend_of_line = lf\n");

        var result = await new ExtractBaseClassOperation(ws.Context).ExecuteAsync(
            new ExtractBaseClassParams
            {
                SourceFile = ws.PathOf("Dog.cs"),
                TypeName = "Dog",
                BaseClassName = "Animal",
                Members = ["Bark"],
                TargetFile = ws.PathOf("Animal.cs"),
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        AssertFormat(ws.PathOf("Animal.cs"), bom: false, "\n");
    }

    private static async Task<RefactoringResult> ExtractInterfaceAsync(
        TestWorkspace ws, string interfaceName, string targetFile, string[]? members = null)
    {
        var source = ws.Context.Solution.Projects.Single().Documents.Single(d => d.Name is "Dog.cs" or "Shape.cs");
        var result = await new ExtractInterfaceOperation(ws.Context).ExecuteAsync(
            new ExtractInterfaceParams
            {
                SourceFile = source.FilePath!,
                TypeName = Path.GetFileNameWithoutExtension(source.FilePath!),
                InterfaceName = interfaceName,
                TargetFile = ws.PathOf(targetFile),
                Members = members,
                AddInterfaceToType = false,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        return result;
    }

    /// <summary>
    /// Gives the documents the texts their files decode to, encoding recorded, as MSBuild's
    /// load does (the texts of a <see cref="TestWorkspace"/> record none).
    /// </summary>
    private static async Task ReadEncodingsFromDiskAsync(TestWorkspace ws, Encoding? projectEncoding = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var texts = new List<(DocumentId Id, SourceText Text)>();
        foreach (var document in ws.Context.Solution.Projects.SelectMany(p => p.Documents))
            texts.Add((document.Id, SourceFileEncoding.Decode(await File.ReadAllBytesAsync(document.FilePath!, ct), projectEncoding)));

        await ws.Context.UpdateSolutionInWriterAsync(
            s => texts.Aggregate(s, (solution, t) => solution.WithDocumentText(t.Id, t.Text)), ct);
    }

    private static async Task AddEditorConfigAsync(TestWorkspace ws, string relativePath, string content)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = ws.PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
        var projectId = ws.Context.Solution.ProjectIds.Single();
        await ws.Context.UpdateSolutionInWriterAsync(
            s => s.AddAnalyzerConfigDocument(DocumentId.CreateNewId(projectId), ".editorconfig", SourceText.From(content), filePath: path),
            ct);
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
            Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
    }
}
