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
    private readonly CSharpCompilationOptions _compilationOptions;

    private TestWorkspace(
        string directory,
        WorkspaceContext context,
        ImmutableArray<MetadataReference> references,
        CSharpCompilationOptions? compilationOptions = null)
    {
        Directory = directory;
        Context = context;
        _references = references;
        _compilationOptions = compilationOptions ?? CompilationOptions;
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
    /// <param name="defaultNamespace">The project's root namespace (<c>RootNamespace</c>), if any.</param>
    /// <param name="additionalFiles">
    /// Additional documents (e.g. Razor files, as the Razor SDK passes them to its generator),
    /// written to disk exactly as given.
    /// </param>
    /// <param name="checkOverflow">
    /// Whether arithmetic is checked by default, like a project with <c>CheckForOverflowUnderflow</c>
    /// (also for <see cref="GetDiskCompilationErrors"/> and <see cref="EmitDiskAssembly"/>).
    /// </param>
    /// <param name="languageVersion">The project's C# version (default: the latest).</param>
    public static TestWorkspace Create(
        IEnumerable<(string Name, string Text)> files,
        IEnumerable<MetadataReference> extraReferences,
        string? editorConfig = null,
        string? defaultNamespace = null,
        IEnumerable<(string Name, string Text)>? additionalFiles = null,
        bool checkOverflow = false,
        LanguageVersion languageVersion = LanguageVersion.Latest)
    {
        var compilationOptions = CompilationOptions.WithOverflowChecks(checkOverflow);
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
            compilationOptions: compilationOptions,
            parseOptions: ParseOptions.WithLanguageVersion(languageVersion),
            metadataReferences: references).WithDefaultNamespace(defaultNamespace));

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

        foreach (var (name, text) in additionalFiles ?? [])
        {
            var path = Path.Combine(directory, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            solution = solution.AddAdditionalDocument(
                DocumentId.CreateNewId(projectId),
                name,
                SourceText.From(text),
                filePath: path);
        }

        return new TestWorkspace(directory, new WorkspaceContext(workspace, solution, directory), references, compilationOptions);
    }

    /// <summary>A project of a <see cref="CreateSolution"/> workspace.</summary>
    /// <param name="Name">Project name. Its files live in a directory of that name, next to <c>Name.csproj</c>.</param>
    /// <param name="Files">Source files, relative to the project directory.</param>
    /// <param name="References">Names of the projects it references.</param>
    /// <param name="TargetFrameworks">
    /// When set, the project is loaded once per framework and named <c>Name(tfm)</c>, all sharing
    /// one project file and one set of source files, like a multi-targeted MSBuild project. Each
    /// instance defines the framework, upper-cased, as a preprocessor symbol.
    /// </param>
    public sealed record ProjectSpec(
        string Name,
        (string Name, string Text)[] Files,
        string[]? References = null,
        string[]? TargetFrameworks = null);

    /// <summary>
    /// A multi-project workspace. Unlike <see cref="Create(ValueTuple{string, string}[])"/>, the
    /// project files exist on disk (as empty SDK projects), so <c>projectPath</c> arguments resolve.
    /// </summary>
    public static TestWorkspace CreateSolution(params ProjectSpec[] projects) =>
        CreateSolution(projects, generatorLoadIssues: null);

    /// <inheritdoc cref="CreateSolution(ProjectSpec[])"/>
    /// <param name="projects">The projects.</param>
    /// <param name="generatorLoadIssues">What the workspace reports as generator load issues.</param>
    public static TestWorkspace CreateSolution(
        IReadOnlyList<ProjectSpec> projects, IReadOnlyList<string>? generatorLoadIssues)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"RoslynMcpServer.TestWorkspace-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);

        var references = FrameworkReferences.Value;
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var instances = new Dictionary<string, List<ProjectId>>(StringComparer.Ordinal);

        foreach (var spec in projects)
        {
            var projectDirectory = Path.Combine(directory, spec.Name);
            System.IO.Directory.CreateDirectory(projectDirectory);
            var projectFile = Path.Combine(projectDirectory, spec.Name + ".csproj");
            File.WriteAllText(projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            foreach (var (name, text) in spec.Files)
            {
                var path = Path.Combine(projectDirectory, name);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }

            var frameworks = spec.TargetFrameworks is { Length: > 0 } tfms ? tfms : new string?[] { null };

            var ids = new List<ProjectId>();
            foreach (var framework in frameworks)
            {
                var projectName = framework is null ? spec.Name : $"{spec.Name}({framework})";
                var parseOptions = framework is null
                    ? ParseOptions
                    : ParseOptions.WithPreprocessorSymbols(framework.ToUpperInvariant());

                var projectId = ProjectId.CreateNewId();
                ids.Add(projectId);
                solution = solution.AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Default,
                    name: projectName,
                    assemblyName: spec.Name,
                    language: LanguageNames.CSharp,
                    filePath: projectFile,
                    compilationOptions: CompilationOptions,
                    parseOptions: parseOptions,
                    metadataReferences: references));

                foreach (var (name, text) in spec.Files)
                {
                    solution = solution.AddDocument(
                        DocumentId.CreateNewId(projectId),
                        name,
                        SourceText.From(text),
                        filePath: Path.Combine(projectDirectory, name));
                }
            }

            instances[spec.Name] = ids;
        }

        foreach (var spec in projects)
        {
            foreach (var referenced in spec.References ?? [])
            {
                foreach (var projectId in instances[spec.Name])
                {
                    solution = solution.AddProjectReference(projectId, new ProjectReference(instances[referenced][0]));
                }
            }
        }

        var context = new WorkspaceContext(workspace, solution, directory, generatorLoadIssues: generatorLoadIssues);
        return new TestWorkspace(directory, context, references);
    }

    /// <summary>Path of a <see cref="CreateSolution"/> project's file.</summary>
    public string ProjectPathOf(string projectName) => Path.Combine(Directory, projectName, projectName + ".csproj");

    /// <summary>Path of a file of a <see cref="CreateSolution"/> project.</summary>
    public string PathOf(string projectName, string fileName) => Path.Combine(Directory, projectName, fileName);

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

    /// <summary>Path of the project file the workspace's project claims (not created on disk).</summary>
    public string ProjectFilePath => Path.Combine(Directory, "TestProject.csproj");

    public string PathOf(string name) => Path.Combine(Directory, name);

    public string ReadFile(string name) => File.ReadAllText(PathOf(name));

    /// <summary>
    /// Compiles the <c>.cs</c> files currently on disk and returns the errors, each
    /// rendered with its location so a failing assertion shows what broke.
    /// </summary>
    public IReadOnlyList<string> GetDiskCompilationErrors() =>
        GetDiskCompilationDiagnostics(DiagnosticSeverity.Error);

    /// <summary>
    /// Like <see cref="GetDiskCompilationErrors"/>, but warnings included.
    /// </summary>
    public IReadOnlyList<string> GetDiskCompilationWarningsAndErrors() =>
        GetDiskCompilationDiagnostics(DiagnosticSeverity.Warning);

    /// <summary>
    /// Compiles the <c>.cs</c> files currently on disk into an assembly image, so a test can
    /// load it and run the generated code.
    /// </summary>
    public byte[] EmitDiskAssembly()
    {
        using var stream = new MemoryStream();
        var emit = CreateDiskCompilation().Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Files on disk do not compile: " + string.Join("; ", emit.Diagnostics));
        }

        return stream.ToArray();
    }

    private IReadOnlyList<string> GetDiskCompilationDiagnostics(DiagnosticSeverity minimum) =>
        CreateDiskCompilation()
            .GetDiagnostics()
            .Where(d => d.Severity >= minimum)
            .Select(d => d.ToString())
            .ToList();

    private CSharpCompilation CreateDiskCompilation()
    {
        var trees = System.IO.Directory.GetFiles(Directory, "*.cs", SearchOption.AllDirectories)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path));

        return CSharpCompilation.Create("DiskCheck", trees, _references, _compilationOptions);
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
