using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Tests;

/// <summary>
/// A single-project in-memory workspace whose documents are backed by real files in a
/// unique temp directory. Operations run end-to-end against it, commit included, without
/// the cost of an MSBuild load; <see cref="GetDiskCompilationErrors"/> then compiles
/// whatever ended up on disk.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> FrameworkReferences =
        new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal)
                || Path.GetFileName(p) is "System.dll" or "mscorlib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray());

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);

    private readonly ImmutableArray<MetadataReference> _references;

    private TestWorkspace(string directory, WorkspaceContext context, ImmutableArray<MetadataReference> references)
    {
        Directory = directory;
        Context = context;
        _references = references;
    }

    /// <summary>Temp directory holding the project's source files.</summary>
    public string Directory { get; }

    /// <summary>Workspace context the operations under test run against.</summary>
    public WorkspaceContext Context { get; }

    public static TestWorkspace Create(params (string Name, string Text)[] files) =>
        Create(files, []);

    /// <param name="files">Source files, written to disk exactly as given.</param>
    /// <param name="extraReferences">References besides the framework.</param>
    /// <param name="editorConfig">Content of an <c>.editorconfig</c> next to the files, if any.</param>
    public static TestWorkspace Create(
        IEnumerable<(string Name, string Text)> files,
        IEnumerable<MetadataReference> extraReferences,
        string? editorConfig = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RoslynMcpServer.TestWorkspace-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);

        var references = FrameworkReferences.Value.AddRange(extraReferences);
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "TestProject",
            assemblyName: "TestProject",
            language: LanguageNames.CSharp,
            filePath: Path.Combine(directory, "TestProject.csproj"),
            compilationOptions: CompilationOptions,
            parseOptions: ParseOptions,
            metadataReferences: references));

        if (editorConfig != null)
        {
            var path = Path.Combine(directory, ".editorconfig");
            File.WriteAllText(path, editorConfig);
            solution = solution.AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(projectId), ".editorconfig", SourceText.From(editorConfig), filePath: path);
        }

        foreach (var (name, text) in files)
        {
            var path = Path.Combine(directory, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId),
                name,
                SourceText.From(text),
                filePath: path);
        }

        return new TestWorkspace(directory, new WorkspaceContext(workspace, solution, directory), references);
    }

    /// <summary>Compiles <paramref name="source"/> into an in-memory assembly reference.</summary>
    public static MetadataReference CompileLibrary(string assemblyName, string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, ParseOptions)],
            FrameworkReferences.Value,
            CompilationOptions);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Library fixture does not compile: " + string.Join("; ", emit.Diagnostics));
        }

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    public string PathOf(string name) => Path.Combine(Directory, name);

    public string ReadFile(string name) => File.ReadAllText(PathOf(name));

    /// <summary>
    /// Compiles the <c>.cs</c> files currently on disk and returns the errors, each
    /// rendered with its location so a failing assertion shows what broke.
    /// </summary>
    public IReadOnlyList<string> GetDiskCompilationErrors()
    {
        var trees = System.IO.Directory.GetFiles(Directory, "*.cs", SearchOption.AllDirectories)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path));

        return CSharpCompilation.Create("DiskCheck", trees, _references, CompilationOptions)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }

    public void Dispose()
    {
        Context.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle must not fail the test.
        }
    }
}
