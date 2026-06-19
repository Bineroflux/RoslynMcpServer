using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// End-to-end test for the staleness→reload contract added with the shadow-copy work:
/// when a project-local analyzer/source-generator assembly the workspace references is
/// rewritten on disk (e.g. a <c>dotnet build</c> overwrote it), the next cache access
/// must reload from scratch — serving a fresh workspace and fresh shadow copies — rather
/// than returning the cached snapshot built over the now-stale generator output.
/// </summary>
public sealed class WorkspaceCacheStalenessReloadTests
{
    [Fact]
    public async Task GetOrCreateAsync_WhenTrackedAnalyzerDllChangesOnDisk_ReloadsWorkspace()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var fixture = LocalAnalyzerSolution.Create();
        var ct = TestContext.Current.CancellationToken;

        var misses = 0;
        var reloads = 0;
        var cache = new WorkspaceCache(
            idleTtl: TimeSpan.FromHours(1),
            sweepInterval: TimeSpan.FromHours(1))
        {
            LogCallback = msg =>
            {
                if (msg.StartsWith("Cache miss", StringComparison.Ordinal))
                    Interlocked.Increment(ref misses);
                if (msg.Contains("changed on disk", StringComparison.Ordinal))
                    Interlocked.Increment(ref reloads);
            }
        };
        using var provider = new MSBuildWorkspaceProvider(cache: cache);

        // First load: cache miss. Capture the context identity and confirm the local
        // analyzer is actually being tracked as a mutable reference (otherwise the rest
        // of the test would vacuously pass).
        WorkspaceContext first;
        using (var ctx = await provider.CreateContextAsync(fixture.SolutionPath, ct))
        {
            first = ctx;
            var tracksLocalAnalyzer = ctx.Solution.Projects
                .SelectMany(p => p.AnalyzerReferences)
                .OfType<AnalyzerFileReference>()
                .Any(r => string.Equals(
                    Path.GetFileName(r.FullPath), LocalAnalyzerSolution.AnalyzerFileName,
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(tracksLocalAnalyzer,
                "The injected project-local analyzer should surface as an AnalyzerFileReference.");
        }

        // Second load with the DLL unchanged: cache hit, same workspace instance.
        WorkspaceContext second;
        using (var ctx = await provider.CreateContextAsync(fixture.SolutionPath, ct))
            second = ctx;
        Assert.Same(first, second);
        Assert.Equal(1, misses);

        // Rewrite the tracked analyzer DLL so its (last-write, length) stamp changes —
        // the same on-disk event a rebuild of a source generator produces.
        fixture.RewriteAnalyzer();

        // Third load: the staleness check must fire, drop the stale entry, and reload.
        WorkspaceContext third;
        using (var ctx = await provider.CreateContextAsync(fixture.SolutionPath, ct))
            third = ctx;

        Assert.True(reloads >= 1, "Expected the changed analyzer DLL to trigger a reload.");
        Assert.Equal(2, misses);
        Assert.NotSame(first, third);

        // The reloaded entry is itself stable: a follow-up access is a plain cache hit.
        WorkspaceContext fourth;
        using (var ctx = await provider.CreateContextAsync(fixture.SolutionPath, ct))
            fourth = ctx;
        Assert.Same(third, fourth);
        Assert.Equal(2, misses);
    }

    /// <summary>
    /// Copies testdata/TestSolution to a temp dir and injects a real, project-local
    /// analyzer assembly plus an <c>&lt;Analyzer Include&gt;</c> item that references it,
    /// so the loaded workspace has a mutable (build-output-like) analyzer reference whose
    /// on-disk changes the staleness check is meant to detect.
    /// </summary>
    private sealed class LocalAnalyzerSolution : IDisposable
    {
        public const string AnalyzerFileName = "LocalStaleProbe.dll";

        public string RootDir { get; }
        public string SolutionPath { get; }
        public string AnalyzerPath { get; }

        private LocalAnalyzerSolution(string rootDir, string solutionPath, string analyzerPath)
        {
            RootDir = rootDir;
            SolutionPath = solutionPath;
            AnalyzerPath = analyzerPath;
        }

        public static LocalAnalyzerSolution Create()
        {
            var source = LocateTestSolution();
            var dest = Path.Combine(Path.GetTempPath(),
                $"RoslynMcpServer.StalenessReload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dest);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);

            var projectDir = Path.Combine(dest, "TestProject");
            var analyzerPath = Path.Combine(projectDir, AnalyzerFileName);
            EmitTrivialAssembly(analyzerPath, "LocalStaleProbe", "public sealed class Marker { }");

            // Reference the local analyzer from the project so MSBuild surfaces it as an
            // analyzer file reference (it need not contain analyzers for the test).
            var csproj = Path.Combine(projectDir, "TestProject.csproj");
            var text = File.ReadAllText(csproj);
            text = text.Replace(
                "</Project>",
                $"  <ItemGroup>\n    <Analyzer Include=\"{AnalyzerFileName}\" />\n  </ItemGroup>\n</Project>",
                StringComparison.Ordinal);
            File.WriteAllText(csproj, text);

            var slnPath = Path.Combine(dest, "TestSolution.sln");
            return new LocalAnalyzerSolution(dest, slnPath, analyzerPath);
        }

        /// <summary>Re-emits the analyzer with different content so its stamp changes.</summary>
        public void RewriteAnalyzer()
        {
            EmitTrivialAssembly(
                AnalyzerPath, "LocalStaleProbe",
                "public sealed class Marker { public int Added; public long AlsoAdded; }");
            // Belt and braces against coarse filesystem timestamp granularity.
            File.SetLastWriteTimeUtc(AnalyzerPath, File.GetLastWriteTimeUtc(AnalyzerPath).AddSeconds(5));
        }

        private static void EmitTrivialAssembly(string outputPath, string assemblyName, string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            if (!string.IsNullOrEmpty(tpa))
                foreach (var p in tpa.Split(Path.PathSeparator))
                    if (!string.IsNullOrEmpty(p)) paths.Add(p);

            var references = paths.Where(File.Exists)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var result = compilation.Emit(outputPath);
            if (!result.Success)
            {
                var errors = string.Join("\n",
                    result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                throw new InvalidOperationException($"Failed to emit '{assemblyName}':\n{errors}");
            }
        }

        private static string LocateTestSolution()
        {
            var probe = AppContext.BaseDirectory;
            for (var i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(probe, "testdata", "TestSolution");
                if (Directory.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(probe);
                if (parent == null) break;
                probe = parent.FullName;
            }
            throw new DirectoryNotFoundException("Could not locate testdata/TestSolution.");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(RootDir)) Directory.Delete(RootDir, recursive: true); }
            catch { /* watchers may still be unhooking; ignore */ }
        }
    }
}
