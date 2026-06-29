using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Builds a <see cref="WorkspaceContext"/> for a single standalone C# file — one that is
/// not backed by any <c>.csproj</c>/<c>.sln</c>, such as a .NET 10 "file-based program"
/// you can launch with <c>dotnet run file.cs</c>.
/// </summary>
/// <remarks>
/// This is the <em>fallback</em> path. The primary path materializes the file into a real
/// project via the SDK (<see cref="FileBasedProgramProject"/>) and loads it through
/// MSBuildWorkspace, which gives default analyzers/source generators and source-level
/// <c>#:project</c> references. This ad-hoc path is used only when that materialization
/// can't run (e.g. <c>dotnet run-api</c> unavailable or a build failure): the file is loaded
/// into an in-memory <see cref="AdhocWorkspace"/> with references to the host's framework
/// assemblies (this server runs on net10.0, so its trusted-platform-assembly set is the
/// net10.0 reference surface) and the implicit global usings <c>Microsoft.NET.Sdk</c>
/// injects. In this degraded mode <c>#:package</c>/<c>#:project</c> symbols and source
/// generators do not resolve, but the file's own symbols and diagnostics still work.
/// </remarks>
internal static class StandaloneFileWorkspace
{
    /// <summary>
    /// The implicit global usings <c>Microsoft.NET.Sdk</c> generates for a non-web project
    /// when <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c>. File-based programs
    /// enable them by default, so mirroring the set keeps diagnostics in line with
    /// <c>dotnet run</c> (e.g. a bare <c>Console.WriteLine</c> resolves without <c>using System;</c>).
    /// </summary>
    private const string ImplicitUsingsSource =
        "global using global::System;\n" +
        "global using global::System.Collections.Generic;\n" +
        "global using global::System.IO;\n" +
        "global using global::System.Linq;\n" +
        "global using global::System.Net.Http;\n" +
        "global using global::System.Threading;\n" +
        "global using global::System.Threading.Tasks;\n";

    /// <summary>
    /// Framework references resolved once per process from the running host's
    /// trusted-platform-assembly list. They are immutable for the process lifetime, so a
    /// single resolution is shared across every directive-free standalone-file workspace.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<MetadataReference>> FrameworkReferences =
        new(ResolveFrameworkReferences);

    public static async Task<WorkspaceContext> CreateAsync(
        string filePath,
        IFileWriter fileWriter,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var normalizedPath = PathResolver.NormalizePath(filePath);
        log?.Invoke($"Loading standalone C# file into an ad-hoc workspace: {normalizedPath}");

        var sourceText = await ReadSourceTextAsync(normalizedPath, cancellationToken);

        return BuildAdhocContext(
            normalizedPath,
            sourceText,
            FrameworkReferences.Value,
            new CSharpParseOptions(LanguageVersion.Latest),
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable),
            additionalInMemoryDocuments: new[] { ("RoslynMcp.ImplicitGlobalUsings.g.cs", SourceText.From(ImplicitUsingsSource)) },
            fileWriter);
    }

    /// <summary>
    /// Constructs an <see cref="AdhocWorkspace"/>-backed <see cref="WorkspaceContext"/> for a
    /// single source file plus any synthetic/generated companion documents.
    /// </summary>
    /// <param name="normalizedFilePath">Absolute, normalized path to the user's <c>.cs</c> file.</param>
    /// <param name="mainText">Current text of that file.</param>
    /// <param name="references">Metadata references for the project.</param>
    /// <param name="parseOptions">Parse options (language version, preprocessor symbols).</param>
    /// <param name="compilationOptions">Compilation options (output kind, nullable, unsafe).</param>
    /// <param name="additionalInMemoryDocuments">
    /// Extra documents to include in the compilation but not surface on disk — implicit
    /// global usings, generated assembly-info, etc. Added with a null file path so they are
    /// never watched, matched by path, or written back.
    /// </param>
    /// <param name="fileWriter">Writer used when a refactor commits changes to the file.</param>
    /// <param name="analyzerReferences">
    /// Analyzer/source-generator references for the project. Supplying the file-based
    /// program's generators here lets Roslyn run them (e.g. <c>[GeneratedRegex]</c>), so the
    /// in-memory compilation has the same generated members as <c>dotnet build</c>.
    /// </param>
    /// <param name="analyzerAssemblyLoader">
    /// Disposable owner of the analyzer assemblies (typically the shadow-copy loader), torn
    /// down with the workspace. Null when there are no analyzer references.
    /// </param>
    internal static WorkspaceContext BuildAdhocContext(
        string normalizedFilePath,
        SourceText mainText,
        IReadOnlyList<MetadataReference> references,
        CSharpParseOptions parseOptions,
        CSharpCompilationOptions compilationOptions,
        IReadOnlyList<(string Name, SourceText Text)> additionalInMemoryDocuments,
        IFileWriter fileWriter,
        IReadOnlyList<AnalyzerReference>? analyzerReferences = null,
        IDisposable? analyzerAssemblyLoader = null)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(normalizedFilePath);
        if (string.IsNullOrEmpty(assemblyName))
            assemblyName = "Standalone";

        var projectId = ProjectId.CreateNewId(debugName: assemblyName);
        var fileName = Path.GetFileName(normalizedFilePath);

        var documents = new List<DocumentInfo>(1 + additionalInMemoryDocuments.Count)
        {
            // The standalone file itself, keyed by its real path so edits and commits round-trip.
            DocumentInfo.Create(
                DocumentId.CreateNewId(projectId, debugName: fileName),
                name: fileName,
                loader: TextLoader.From(TextAndVersion.Create(mainText, VersionStamp.Create(), normalizedFilePath)),
                filePath: normalizedFilePath,
                sourceCodeKind: SourceCodeKind.Regular),
        };

        foreach (var (name, text) in additionalInMemoryDocuments)
        {
            documents.Add(DocumentInfo.Create(
                DocumentId.CreateNewId(projectId, debugName: name),
                name: name,
                loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create())),
                filePath: null,
                sourceCodeKind: SourceCodeKind.Regular));
        }

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name: assemblyName,
            assemblyName: assemblyName,
            language: LanguageNames.CSharp,
            filePath: null,
            outputFilePath: null,
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            documents: documents,
            metadataReferences: references,
            analyzerReferences: analyzerReferences ?? Array.Empty<AnalyzerReference>());

        var workspace = new AdhocWorkspace();
        workspace.AddProject(projectInfo);

        return new WorkspaceContext(
            workspace, workspace.CurrentSolution, normalizedFilePath, fileWriter,
            analyzerAssemblyLoader: analyzerAssemblyLoader);
    }

    internal static async Task<SourceText> ReadSourceTextAsync(string filePath, CancellationToken ct)
    {
        // Read through a shared stream so an editor holding the file open doesn't block us.
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SourceText.From(stream);
    }

    private static IReadOnlyList<MetadataReference> ResolveFrameworkReferences()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(path)) continue;
                try { references.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* skip an unreadable/locked assembly rather than fail the whole load */ }
            }
        }

        if (references.Count == 0)
        {
            // Last-resort fallback so a workspace can still be built (e.g. if the TPA list is
            // unavailable): at least reference the assembly that defines System.Object.
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        return references;
    }
}
