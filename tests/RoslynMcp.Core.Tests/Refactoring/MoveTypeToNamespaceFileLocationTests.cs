using System.Text;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Tests.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// move_type_to_namespace advertised <c>updateFileLocation</c> but never read it: it
/// reported success and left the file where it was. The file now moves into the folder
/// matching the new namespace when that is safe, and stays (with a PARTIAL_SUCCESS
/// explanation) when it is not.
/// </summary>
public sealed class MoveTypeToNamespaceFileLocationTests
{
    private const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net11.0</TargetFramework>
            <RootNamespace>App</RootNamespace>
          </PropertyGroup>
        </Project>
        """;

    private const string Widget = "namespace App;\n\npublic class Widget\n{\n}\n";

    private const string Consumer = "namespace App.Consumers;\n\npublic class Consumer\n{\n    public Widget Widget { get; } = new();\n}\n";

    [Fact]
    public async Task SafeMove_MovesTheFileAndKeepsTheCachedSolutionInStep()
    {
        using var ws = CreateWorkspace();
        var source = ws.PathOf("Widget.cs");
        var target = Path.Combine(ws.Directory, "Services", "Auth", "Widget.cs");

        var result = await MoveAsync(ws, "App.Services.Auth");

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.False(File.Exists(source));
        Assert.Contains("namespace App.Services.Auth;", File.ReadAllText(target));
        Assert.Contains(target, result.Changes!.FilesCreated);
        Assert.Contains(source, result.Changes.FilesDeleted);
        Assert.DoesNotContain(source, result.Changes.FilesModified);
        Assert.Equal(target, result.Symbol!.NewLocation!.File);

        // The cached solution matches disk: the document now lives at the target path, in
        // the target folders, and nothing points at the old path any more.
        var document = Assert.Single(ws.Context.Solution.Projects.Single().Documents, d => d.Name == "Widget.cs");
        Assert.True(PathResolver.PathsEqual(target, document.FilePath));
        Assert.Equal(new[] { "Services", "Auth" }, document.Folders);
        Assert.Null(ws.Context.GetDocumentByPath(source));

        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task SafeMove_KeepsTheFilesEncodingAndLineEndings()
    {
        // A BOM-less LF file must not come out of the move with a BOM (as a fresh write would
        // give it) or CRLF line breaks; the using added to the consumer uses LF as well.
        using var ws = CreateWorkspace();

        await MoveAsync(ws, "App.Services");

        var moved = File.ReadAllBytes(Path.Combine(ws.Directory, "Services", "Widget.cs"));
        Assert.False(moved.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(moved));
        Assert.StartsWith("using App.Services;\n\nnamespace App.Consumers;", ws.ReadFile("Consumer.cs"));
    }

    [Fact]
    public async Task WithoutUpdateFileLocation_TheFileStays()
    {
        using var ws = CreateWorkspace();

        var result = await MoveAsync(ws, "App.Services", updateFileLocation: false);

        Assert.Null(result.Error);
        Assert.Contains("namespace App.Services;", ws.ReadFile("Widget.cs"));
        Assert.False(Directory.Exists(Path.Combine(ws.Directory, "Services")));
    }

    [Fact]
    public async Task FileAlreadyInTheNamespaceFolder_StaysWithoutAWarning()
    {
        using var ws = CreateWorkspace(widgetPath: Path.Combine("Services", "Widget.cs"));

        var result = await MoveAsync(ws, "App.Services", sourceFile: Path.Combine("Services", "Widget.cs"));

        Assert.Null(result.Error);
        Assert.Empty(result.Changes!.FilesCreated);
        Assert.Contains("namespace App.Services;", ws.ReadFile(Path.Combine("Services", "Widget.cs")));
    }

    [Theory]
    [InlineData("disabled-compile-items", "EnableDefaultCompileItems")]
    [InlineData("disabled-items-in-directory-build-props", "EnableDefaultItems")]
    [InlineData("compile-remove", "<Compile Remove=\"Services/**\">")]
    [InlineData("explicit-include", "<Compile Include=\"Widget.cs\">")]
    [InlineData("compile-update", "<Compile Update=\"Widget.cs\">")]
    [InlineData("legacy-project", "not an SDK-style project")]
    [InlineData("outside-root-namespace", "not inside the project's root namespace 'App'")]
    [InlineData("target-exists", "already exists")]
    [InlineData("second-type", "declares more than the moved type")]
    [InlineData("no-project-file", "no project file on disk")]
    public async Task UnsafeMove_ChangesTheNamespaceButLeavesTheFile(string scenario, string reason)
    {
        var project = scenario switch
        {
            "disabled-compile-items" => SdkProject.Replace("</RootNamespace>", "</RootNamespace>\n    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>"),
            "compile-remove" => SdkProject.Replace("</Project>", "  <ItemGroup><Compile Remove=\"Services/**\" /></ItemGroup>\n</Project>"),
            "explicit-include" => SdkProject.Replace("</Project>", "  <ItemGroup><Compile Include=\"Widget.cs\" /></ItemGroup>\n</Project>"),
            "compile-update" => SdkProject.Replace("</Project>", "  <ItemGroup><Compile Update=\"Widget.cs\" DependentUpon=\"Consumer.cs\" /></ItemGroup>\n</Project>"),
            "legacy-project" => "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" /></Project>",
            "no-project-file" => null,
            _ => SdkProject
        };
        var widget = scenario == "second-type" ? Widget + "\npublic enum WidgetKind { Small }\n" : Widget;
        using var ws = CreateWorkspace(project, widget);
        if (scenario == "disabled-items-in-directory-build-props")
            File.WriteAllText(ws.PathOf("Directory.Build.props"), "<Project><PropertyGroup><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup></Project>");
        if (scenario == "target-exists")
        {
            Directory.CreateDirectory(Path.Combine(ws.Directory, "Services"));
            File.WriteAllText(Path.Combine(ws.Directory, "Services", "Widget.cs"), "// someone else's file\n");
        }

        var targetNamespace = scenario == "outside-root-namespace" ? "Other.Services" : "App.Services";
        var result = await MoveAsync(ws, targetNamespace);

        Assert.True(result.Success);
        Assert.Equal("PARTIAL_SUCCESS", result.Error?.Code);
        Assert.Contains(reason, result.Error!.Message);
        Assert.Contains($"namespace {targetNamespace};", ws.ReadFile("Widget.cs"));
        Assert.Empty(result.Changes!.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
        if (scenario == "target-exists")
            Assert.Equal("// someone else's file\n", ws.ReadFile(Path.Combine("Services", "Widget.cs")));
        else
            Assert.False(Directory.Exists(Path.Combine(ws.Directory, "Services")));
    }

    [Fact]
    public async Task Preview_ReportsTheMoveAndTouchesNothing()
    {
        using var ws = CreateWorkspace();

        var result = await MoveAsync(ws, "App.Services", preview: true);

        Assert.Null(result.Error);
        var create = Assert.Single(result.PendingChanges!, c => c.ChangeType == ChangeKind.Create);
        Assert.Equal(Path.Combine(ws.Directory, "Services", "Widget.cs"), create.File);
        Assert.Contains(result.PendingChanges!, c => c.ChangeType == ChangeKind.Delete && c.File == ws.PathOf("Widget.cs"));
        Assert.Equal(Widget, ws.ReadFile("Widget.cs"));
        Assert.False(Directory.Exists(Path.Combine(ws.Directory, "Services")));
    }

    [Fact]
    public async Task Preview_ReportsWhyTheFileWouldStay()
    {
        using var ws = CreateWorkspace();

        var result = await MoveAsync(ws, "Other", preview: true);

        Assert.Equal("PARTIAL_SUCCESS", result.Error?.Code);
        Assert.DoesNotContain(result.PendingChanges!, c => c.ChangeType == ChangeKind.Create);
        Assert.Equal(Widget, ws.ReadFile("Widget.cs"));
    }

    [Fact]
    public async Task MsBuildWorkspace_CachedSolutionMatchesMsBuildAfterTheMove()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        // The real thing: an MSBuild-loaded project (RootNamespace defaults to TestProject),
        // the workspace cache and its file watcher.
        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;
        using var provider = new MSBuildWorkspaceProvider();
        var source = Path.Combine(working.ProjectDir, "Services", "OrderService.cs");
        var target = Path.Combine(working.ProjectDir, "Billing", "OrderService.cs");

        RefactoringResult result;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            result = await new MoveTypeToNamespaceOperation(ctx).ExecuteAsync(
                new MoveTypeToNamespaceParams
                {
                    SourceFile = source,
                    SymbolName = "OrderService",
                    TargetNamespace = "TestProject.Billing",
                    UpdateFileLocation = true
                },
                ct);
        }

        Assert.Null(result.Error);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(source));
        Assert.Contains("using TestProject.Billing;", File.ReadAllText(Path.Combine(working.ProjectDir, "Program.cs")));

        WorkspaceContext cached;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            cached = ctx;
            Assert.NotNull(ctx.GetDocumentByPath(target));
            Assert.Null(ctx.GetDocumentByPath(source));
            var compilation = await ctx.Solution.Projects.Single().GetCompilationAsync(ct);
            Assert.Empty(compilation!.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        // MSBuild's own evaluation of the project finds nothing to add or remove (outside the
        // lease: re-evaluation waits for operations to finish).
        Assert.False(await cached.ReconcileProjectDocumentsAsync(
            Path.Combine(working.ProjectDir, "TestProject.csproj"), ct));
    }

    [Fact]
    public async Task MsBuildWorkspace_MoveIntoAFolderAnImportExcludes_IsUndone()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        // The project files the move checks up front do not show this: an imported props
        // file removes the target folder from compilation. Only MSBuild can tell.
        using var working = RenameTempSolution.Create();
        var ct = TestContext.Current.CancellationToken;
        var projectFile = Path.Combine(working.ProjectDir, "TestProject.csproj");
        File.WriteAllText(Path.Combine(working.ProjectDir, "Shared.props"), """
            <Project>
              <ItemGroup>
                <Compile Remove="Billing/**" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(projectFile, File.ReadAllText(projectFile)
            .Replace("</Project>", """  <Import Project="Shared.props" />""" + Environment.NewLine + "</Project>"));
        using var provider = new MSBuildWorkspaceProvider();
        var source = Path.Combine(working.ProjectDir, "Services", "OrderService.cs");
        var targetFolder = Path.Combine(working.ProjectDir, "Billing");

        RefactoringResult result;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            result = await new MoveTypeToNamespaceOperation(ctx).ExecuteAsync(
                new MoveTypeToNamespaceParams
                {
                    SourceFile = source,
                    SymbolName = "OrderService",
                    TargetNamespace = "TestProject.Billing",
                    UpdateFileLocation = true
                },
                ct);
        }

        Assert.True(result.Success);
        Assert.Equal("PARTIAL_SUCCESS", result.Error?.Code);
        Assert.Contains("MSBuild does not compile", result.Error!.Message);
        Assert.Contains("the move was undone", result.Error.Message);
        Assert.Empty(result.Changes!.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
        Assert.Null(result.Symbol!.NewLocation);

        // The namespace change stays, in the file at its old place; the folder the move
        // created is gone again.
        Assert.Contains("namespace TestProject.Billing;", File.ReadAllText(source));
        Assert.False(Directory.Exists(targetFolder));

        WorkspaceContext cached;
        using (var ctx = await provider.CreateContextAsync(working.SolutionPath, ct))
        {
            cached = ctx;
            Assert.NotNull(ctx.GetDocumentByPath(source));
            Assert.Null(ctx.GetDocumentByPath(Path.Combine(targetFolder, "OrderService.cs")));
            var compilation = await ctx.Solution.Projects.Single().GetCompilationAsync(ct);
            Assert.Empty(compilation!.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        Assert.False(await cached.ReconcileProjectDocumentsAsync(projectFile, ct));
    }

    [Theory]
    [InlineData("Services/**", "Services/Auth/Widget.cs", true)]
    [InlineData("Services/**", "Widget.cs", false)]
    [InlineData("**/*.cs", "Widget.cs", true)]
    [InlineData("**/*.cs", "Services/Widget.cs", true)]
    [InlineData("Services\\*.cs", "Services/Widget.cs", true)]
    [InlineData("Services/*.cs", "Services/Auth/Widget.cs", false)]
    [InlineData("Widget.cs", "Widget.cs", true)]
    [InlineData("./widget.cs", "Widget.cs", true)]
    [InlineData("Widget.cs", "Services/Widget.cs", false)]
    [InlineData("Wid?et.cs", "Widget.cs", true)]
    public void MatchesItemSpec_FollowsMsBuildGlobs(string spec, string path, bool expected) =>
        Assert.Equal(expected, NamespaceFolderMove.MatchesItemSpec(spec, path.Replace('/', Path.DirectorySeparatorChar)));

    private static TestWorkspace CreateWorkspace(string? project = SdkProject, string widget = Widget, string widgetPath = "Widget.cs")
    {
        var ws = TestWorkspace.Create(
            [(widgetPath, widget), ("Consumer.cs", Consumer)],
            [],
            defaultNamespace: "App");
        if (project != null)
            File.WriteAllText(ws.ProjectFilePath, project);
        return ws;
    }

    private static Task<RefactoringResult> MoveAsync(
        TestWorkspace ws,
        string targetNamespace,
        bool updateFileLocation = true,
        bool preview = false,
        string sourceFile = "Widget.cs") =>
        new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams
            {
                SourceFile = ws.PathOf(sourceFile),
                SymbolName = "Widget",
                TargetNamespace = targetNamespace,
                UpdateFileLocation = updateFileLocation,
                Preview = preview
            },
            TestContext.Current.CancellationToken);
}
