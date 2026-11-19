using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// A loaded project does not carry its <c>&lt;CodePage&gt;</c>, but the texts MSBuild decoded
/// show it: every file without byte order mark is read in it, also one that is valid UTF-8.
/// </summary>
public sealed class ProjectFileConventionsTests
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    [Fact]
    public async Task InferDeclaredEncoding_AsciiFileReadInACodePage_IsThatCodePage()
    {
        // Without a declared code page an ASCII file reads as UTF-8.
        var project = CreateProject(
            SourceText.From("// Größe\nclass A { }\n", Windows1252),
            SourceText.From("class B { }\n", Windows1252));

        var declared = await ProjectFileConventions.InferDeclaredEncodingAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(1252, declared!.CodePage);
    }

    [Fact]
    public async Task InferDeclaredEncoding_FileReadAsUtf8WithoutBom_IsNone()
    {
        var project = CreateProject(
            SourceText.From("// Größe\nclass A { }\n", Windows1252),
            SourceText.From("class B { }\n", SourceFileEncoding.Utf8WithoutBom));

        Assert.Null(await ProjectFileConventions.InferDeclaredEncodingAsync(project, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InferDeclaredEncoding_OnlyFilesThatAreNotUtf8OrHaveAByteOrderMark_DontTell()
    {
        // windows-1252 umlauts are no valid UTF-8, so the system code page reads them as well;
        // a byte order mark decides by itself; a text without encoding says nothing.
        var project = CreateProject(
            SourceText.From("// Größe\nclass A { }\n", Windows1252),
            SourceText.From("class B { }\n", SourceFileEncoding.Utf8WithBom),
            SourceText.From("class C { }\n"));

        Assert.Null(await ProjectFileConventions.InferDeclaredEncodingAsync(project, TestContext.Current.CancellationToken));
    }

    private static Project CreateProject(params SourceText[] texts)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        for (var i = 0; i < texts.Length; i++)
            project = project.AddDocument($"F{i}.cs", texts[i], filePath: Path.Combine(Path.GetTempPath(), $"F{i}.cs")).Project;

        return project;
    }
}
