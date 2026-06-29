using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Integration tests for loading a standalone <c>.cs</c> file — one not backed by any
/// <c>.csproj</c>/<c>.sln</c>, such as a .NET 10 file-based program. The file is turned into
/// a real project via the SDK (<c>dotnet run-api</c>) and loaded through MSBuildWorkspace, so
/// it gets the SDK's default analyzers/source generators and (when present) <c>#:project</c>
/// references as source. These tests therefore require a working .NET 10 SDK.
/// </summary>
public sealed class StandaloneFileWorkspaceTests
{
    // Line numbers are referenced by the find_references test, so keep this layout stable:
    //  1: var g = new Greeter("world");
    //  2: Console.WriteLine(g.Greet());
    //  3: Console.WriteLine(g.Greet());
    //  4:
    //  5: internal sealed class Greeter(string subject)
    //  6: {
    //  7:     public string Greet() => $"hello, {subject}!";
    //  8: }
    private const string GreeterSource =
        "var g = new Greeter(\"world\");\n" +
        "Console.WriteLine(g.Greet());\n" +
        "Console.WriteLine(g.Greet());\n" +
        "\n" +
        "internal sealed class Greeter(string subject)\n" +
        "{\n" +
        "    public string Greet() => $\"hello, {subject}!\";\n" +
        "}\n";

    [Fact]
    public async Task LoadsStandaloneFile_AsMsBuildBackedProject()
    {
        using var file = TempCsFile.Write(GreeterSource);
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(file.Path, ct);

        // The file is materialized into a real project and loaded via MSBuild (not the
        // framework-only ad-hoc fallback), so the SDK's default analyzers/generators apply.
        Assert.IsType<MSBuildWorkspace>(ctx.Workspace);
        // The workspace keeps the .cs as its identity, not the temp wrapper project.
        Assert.Equal(file.Path, ctx.LoadedPath);
        Assert.NotNull(ctx.GetDocumentByPath(file.Path));
    }

    [Fact]
    public async Task DirectiveFreeGeneratedRegex_RunsDefaultGenerator_NoCS8795()
    {
        // A directive-free file using [GeneratedRegex] only compiles cleanly if the SDK's
        // default Regex source generator runs — which it does because we load through MSBuild.
        const string source =
            "using System.Text.RegularExpressions;\n" +
            "Console.WriteLine(Patterns.Digits().IsMatch(\"a1\"));\n" +
            "internal partial class Patterns\n" +
            "{\n" +
            "    [GeneratedRegex(@\"\\d+\")]\n" +
            "    public static partial Regex Digits();\n" +
            "}\n";
        using var file = TempCsFile.Write(source);
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(file.Path, ct);

        var diagnostics = await RunDiagnosticsAsync(ctx, file.Path, ct);
        Assert.DoesNotContain(diagnostics, d => d.Id == "CS8795");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error.ToString());
    }

    [Fact]
    public async Task DirectiveBearingFile_LoadsWithoutDirectiveErrors()
    {
        // #:property only — exercises the directive path with no network/package restore.
        const string source =
            "#:property LangVersion=latest\n" +
            "Console.WriteLine(Greet());\n" +
            "static string Greet() => \"hi\";\n";
        using var file = TempCsFile.Write(source);
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(file.Path, ct);

        var diagnostics = await RunDiagnosticsAsync(ctx, file.Path, ct);
        Assert.DoesNotContain(diagnostics, d => d.Id == "CS9298"); // directives accepted
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error.ToString());
    }

    [Fact]
    public async Task FindReferences_ResolvesUsagesWithinTheStandaloneFile()
    {
        using var file = TempCsFile.Write(GreeterSource);
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(file.Path, ct);

        var op = new FindReferencesOperation(ctx);
        var result = await op.ExecuteAsync(
            new FindReferencesParams
            {
                SourceFile = file.Path,
                SymbolName = "Greet",
                Line = 7,   // definition: `public string Greet() => ...`
                Column = 19,
            },
            ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        // The definition (line 7) plus the two call sites (lines 2 and 3).
        Assert.Equal(3, result.Data!.TotalCount);
        Assert.Contains(result.Data.References, r => r.IsDefinition);
    }

    [Fact]
    public async Task GetDiagnostics_TypeError_IsReported()
    {
        using var file = TempCsFile.Write("int wrong = \"not an int\";\n"); // CS0029
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(file.Path, ct);

        var diagnostics = await RunDiagnosticsAsync(ctx, file.Path, ct);
        Assert.Contains(diagnostics, d => d.Id == "CS0029");
    }

    [Fact]
    public void ComputeDirectiveSignature_StableAcrossCodeEdits_ChangesWithDirectives()
    {
        var baseline = FileBasedProgramProject.ComputeDirectiveSignature(
            "#:package Newtonsoft.Json@13.0.3\nConsole.WriteLine(1);\n");
        var codeEdited = FileBasedProgramProject.ComputeDirectiveSignature(
            "#:package Newtonsoft.Json@13.0.3\nConsole.WriteLine(2);\n");
        var directiveEdited = FileBasedProgramProject.ComputeDirectiveSignature(
            "#:package Newtonsoft.Json@13.0.4\nConsole.WriteLine(1);\n");

        Assert.Contains("#:package Newtonsoft.Json@13.0.3", baseline);
        Assert.Equal(baseline, codeEdited);        // a code-only edit does not force a rebuild
        Assert.NotEqual(baseline, directiveEdited); // a directive change does
    }

    [Fact]
    public void ComputeDirectiveSignature_DirectiveFreeFile_IsEmpty()
    {
        Assert.Equal("", FileBasedProgramProject.ComputeDirectiveSignature(
            "using System;\nConsole.WriteLine(\"#:not a directive\");\n"));
    }

    [Theory]
    [InlineData(".cs", true)]
    [InlineData(".csproj", false)]
    [InlineData(".sln", false)]
    [InlineData(".txt", false)]
    public void IsStandaloneCSharpFile_RecognizesOnlyCsFiles(string extension, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Sample{extension}");
        Assert.Equal(expected, PathResolver.IsStandaloneCSharpFile(path));
    }

    [Fact]
    public void IsValidWorkspacePath_AcceptsStandaloneCsAndProjectsAndSolutions()
    {
        var dir = Path.GetTempPath();
        Assert.True(PathResolver.IsValidWorkspacePath(Path.Combine(dir, "a.cs")));
        Assert.True(PathResolver.IsValidWorkspacePath(Path.Combine(dir, "a.csproj")));
        Assert.True(PathResolver.IsValidWorkspacePath(Path.Combine(dir, "a.sln")));
        Assert.True(PathResolver.IsValidWorkspacePath(Path.Combine(dir, "a.slnx")));
        Assert.False(PathResolver.IsValidWorkspacePath(Path.Combine(dir, "a.vb")));
    }

    private static async Task<IReadOnlyList<DiagnosticInfo>> RunDiagnosticsAsync(
        WorkspaceContext ctx, string sourceFile, CancellationToken ct)
    {
        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams { SourceFile = sourceFile, SeverityFilter = "All" }, ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        return result.Data!.Diagnostics;
    }

    /// <summary>A temporary <c>.cs</c> file on disk, deleted on dispose.</summary>
    private sealed class TempCsFile : IDisposable
    {
        public string Path { get; private init; } = "";

        public static TempCsFile Write(string contents)
        {
            var path = PathResolver.NormalizePath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"RoslynMcp.Standalone-{Guid.NewGuid():N}.cs"));
            File.WriteAllText(path, contents);
            return new TempCsFile { Path = path };
        }

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
