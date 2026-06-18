using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Tests for <see cref="ShadowCopyAnalyzerAssemblyLoader"/> and its integration into
/// the workspace provider. The core guarantee: analyzer / source-generator assemblies
/// are loaded from private temp copies, so the original build output is never locked
/// and a concurrent <c>dotnet build</c> can overwrite it (the Visual Studio behavior).
/// </summary>
public sealed class ShadowCopyAnalyzerTests
{
    [Fact]
    public void LoadFromPath_DoesNotLockOriginalFile()
    {
        // Use a real, dependency-light managed assembly as the stand-in "analyzer".
        var sourceDll = typeof(RoslynMcp.Contracts.Errors.ErrorCodes).Assembly.Location;
        Assert.True(File.Exists(sourceDll), $"Expected source assembly at {sourceDll}.");

        var tempDir = Path.Combine(Path.GetTempPath(), $"RoslynMcp.ShadowLockTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var original = Path.Combine(tempDir, Path.GetFileName(sourceDll));
        File.Copy(sourceDll, original);

        try
        {
            using var loader = new ShadowCopyAnalyzerAssemblyLoader();

            var assembly = loader.LoadFromPath(original);
            Assert.NotNull(assembly);

            // The proof: with Roslyn's default in-place loader this delete throws
            // IOException ("being used by another process") on Windows. Because the
            // loader memory-mapped a shadow copy instead, the original is free.
            var ex = Record.Exception(() => File.Delete(original));
            Assert.Null(ex);
            Assert.False(File.Exists(original));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LoadFromPath_ReturnsUsableAssemblyWithExpectedIdentity()
    {
        var sourceDll = typeof(RoslynMcp.Contracts.Errors.ErrorCodes).Assembly.Location;
        var expectedName = typeof(RoslynMcp.Contracts.Errors.ErrorCodes).Assembly.GetName().Name;

        var tempDir = Path.Combine(Path.GetTempPath(), $"RoslynMcp.ShadowIdentityTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var original = Path.Combine(tempDir, Path.GetFileName(sourceDll));
        File.Copy(sourceDll, original);

        try
        {
            using var loader = new ShadowCopyAnalyzerAssemblyLoader();

            var assembly = loader.LoadFromPath(original);
            Assert.Equal(expectedName, assembly.GetName().Name);
            // Loaded from the shadow copy, not the path we handed in.
            Assert.NotEqual(
                Path.GetFullPath(original),
                Path.GetFullPath(assembly.Location),
                StringComparer.OrdinalIgnoreCase);

            // Idempotent: a second request for the same path resolves to the same assembly.
            var again = loader.LoadFromPath(original);
            Assert.Same(assembly, again);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void GetGenerators_FromShadowLoadedAssembly_DiscoversGeneratorAndDoesNotLockOriginal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"RoslynMcp.ShadowGenTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var generatorDll = Path.Combine(tempDir, "Sample.Generator.dll");

        try
        {
            EmitSampleGenerator(generatorDll);

            using var loader = new ShadowCopyAnalyzerAssemblyLoader();
            var reference = new AnalyzerFileReference(generatorDll, loader);

            var loadErrors = new List<string>();
            reference.AnalyzerLoadFailed += (_, e) => loadErrors.Add($"{e.ErrorCode}: {e.Message}");

            // GetGenerators only finds the type if [Generator]/IIncrementalGenerator
            // resolve to the host's Microsoft.CodeAnalysis — i.e. unification works.
            var generators = reference.GetGenerators(LanguageNames.CSharp);

            Assert.True(generators.Length >= 1,
                $"Expected the shadow-loaded assembly to expose a source generator. " +
                $"Load errors: {string.Join("; ", loadErrors)}");

            // The original assembly must remain unlocked: a copy was loaded instead.
            var ex = Record.Exception(() => File.Delete(generatorDll));
            Assert.Null(ex);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ShadowCopies_AreIsolatedUnderPerProcessPidDirectory_WithLockFile()
    {
        var sourceDll = typeof(RoslynMcp.Contracts.Errors.ErrorCodes).Assembly.Location;
        var tempDir = Path.Combine(Path.GetTempPath(), $"RoslynMcp.ShadowIsoTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // Copy under a unique name so we can find exactly this copy in the shadow tree.
        var uniqueName = $"IsoProbe-{Guid.NewGuid():N}.dll";
        var original = Path.Combine(tempDir, uniqueName);
        File.Copy(sourceDll, original);

        try
        {
            using var loader = new ShadowCopyAnalyzerAssemblyLoader();
            Assert.NotNull(loader.LoadFromPath(original));

            // The copy lives under a directory named purely for THIS process, so
            // parallel servers (other PIDs) can never collide with it.
            var pidDir = Path.Combine(
                Path.GetTempPath(), "RoslynMcp", "AnalyzerShadowCopy", $"pid-{Environment.ProcessId}");
            Assert.True(Directory.Exists(pidDir), $"Expected process directory {pidDir}.");

            // The liveness lock is present while this process runs.
            Assert.True(File.Exists(Path.Combine(pidDir, "in_use.lock")),
                "in_use.lock should exist for the live process.");

            var copies = Directory.GetFiles(pidDir, uniqueName, SearchOption.AllDirectories);
            Assert.NotEmpty(copies);
            Assert.DoesNotContain(copies, c => string.Equals(
                Path.GetFullPath(c), Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Sweep_DeletesDirectoriesWithoutLock_AndKeepsLockedAndOwn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"RoslynMcp.SweepTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // Dead owner: no lock, only an empty sub-directory left behind (the residue
            // a crashed/terminated process leaves once delete-on-close removes its files).
            var dead = Path.Combine(root, "pid-100");
            Directory.CreateDirectory(Path.Combine(dead, "0", "ABCD1234"));

            // Live owner: holds the lock file.
            var live = Path.Combine(root, "pid-200");
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(live, "in_use.lock"), "");

            // Our own directory: skipped even though it has no lock yet.
            var own = Path.Combine(root, "pid-300");
            Directory.CreateDirectory(own);

            ShadowCopyAnalyzerAssemblyLoader.SweepDeadProcessDirectories(root, own);

            Assert.False(Directory.Exists(dead), "Lock-less (dead) directory should be swept.");
            Assert.True(Directory.Exists(live), "Locked (live) directory must be kept.");
            Assert.True(Directory.Exists(own), "Own directory must be kept.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void LockFile_DeleteOnClose_RemovesLockSoDirectoryBecomesSweepable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"RoslynMcp.LockLifecycle-{Guid.NewGuid():N}");
        var procDir = Path.Combine(root, "pid-100");
        Directory.CreateDirectory(procDir);
        var lockPath = Path.Combine(procDir, "in_use.lock");

        // Hold the lock the way a live process does (delete-on-close).
        var handle = File.OpenHandle(
            lockPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.DeleteOnClose);
        try
        {
            Assert.True(File.Exists(lockPath));
            ShadowCopyAnalyzerAssemblyLoader.SweepDeadProcessDirectories(root, ownDirectory: null);
            Assert.True(Directory.Exists(procDir), "A directory with a held lock must survive the sweep.");
        }
        finally
        {
            handle.Dispose(); // simulates the owning process exiting (clean or killed)
        }

        // The kernel removed the lock when the last handle closed...
        Assert.False(File.Exists(lockPath),
            "delete-on-close should remove the lock once the handle closes.");

        // ...so the now lock-less directory is collectible by the next sweep.
        ShadowCopyAnalyzerAssemblyLoader.SweepDeadProcessDirectories(root, ownDirectory: null);
        Assert.False(Directory.Exists(procDir), "A directory whose lock is gone should be swept.");

        TryDeleteDirectory(root);
    }

    [Fact]
    public async Task CreateContextAsync_RewrapsAnalyzerReferencesWithShadowLoader()
    {
        if (!ModuleInitializer.MsBuildAvailable)
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");

        using var working = TestSolutionCopy.Create();
        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(
            working.SolutionPath, TestContext.Current.CancellationToken);

        var fileReferences = ctx.Solution.Projects
            .SelectMany(p => p.AnalyzerReferences)
            .OfType<AnalyzerFileReference>()
            .ToList();

        if (fileReferences.Count == 0)
            Assert.Skip("No analyzer file references surfaced for the fixture in this environment.");

        // Every file-backed analyzer reference must route through our shadow-copy
        // loader so nothing is loaded in place from the build output.
        Assert.All(fileReferences, r =>
            Assert.IsType<ShadowCopyAnalyzerAssemblyLoader>(r.AssemblyLoader));

        // The rewrite swaps ONLY the loader: each reference is still an
        // AnalyzerFileReference whose FullPath is the ORIGINAL on-disk assembly, not
        // the internal shadow copy. The staleness check relies on this — it reads
        // FullPath via CaptureMutableAnalyzerStamps and stats that file.
        var shadowRoot = Path.Combine(Path.GetTempPath(), "RoslynMcp", "AnalyzerShadowCopy");
        Assert.All(fileReferences, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.FullPath));
            Assert.True(File.Exists(r.FullPath),
                $"FullPath should point at the real original assembly: {r.FullPath}");
            Assert.False(r.FullPath!.Contains(shadowRoot, StringComparison.OrdinalIgnoreCase),
                $"FullPath must be the original, not the shadow copy: {r.FullPath}");
        });
    }

    /// <summary>
    /// Compiles a minimal incremental source generator to <paramref name="outputPath"/>
    /// so the test has a real generator assembly to load through the shadow loader,
    /// independent of any SDK-provided generator.
    /// </summary>
    private static void EmitSampleGenerator(string outputPath)
    {
        const string source = /* language=regex */ """
            using Microsoft.CodeAnalysis;

            [Generator]
            public sealed class SampleGenerator : IIncrementalGenerator
            {
                public void Initialize(IncrementalGeneratorInitializationContext context)
                    => context.RegisterPostInitializationOutput(
                        c => c.AddSource("Sample.g.cs", "// generated by SampleGenerator"));
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Reference the running framework (TPA list) plus Microsoft.CodeAnalysis,
        // deduped by path so Emit doesn't see the same assembly twice.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var p in tpa.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
        }
        paths.Add(typeof(GeneratorAttribute).Assembly.Location);

        var references = paths
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "Sample.Generator",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(outputPath);
        if (!result.Success)
        {
            var errors = string.Join(
                "\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Failed to emit sample generator:\n{errors}");
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Copies testdata/TestSolution to a temp directory so the workspace load does not
    /// write obj/bin artifacts back into the repo working tree.
    /// </summary>
    private sealed class TestSolutionCopy : IDisposable
    {
        public string RootDir { get; }
        public string SolutionPath { get; }

        private TestSolutionCopy(string rootDir, string solutionPath)
        {
            RootDir = rootDir;
            SolutionPath = solutionPath;
        }

        public static TestSolutionCopy Create()
        {
            var source = LocateTestSolution();
            var dest = Path.Combine(Path.GetTempPath(),
                $"RoslynMcpServer.ShadowCopyTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dest);

            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);

            var slnPath = Path.Combine(dest, "TestSolution.slnx");
            if (!File.Exists(slnPath))
                slnPath = Path.Combine(dest, "TestSolution.sln");

            return new TestSolutionCopy(dest, slnPath);
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

        public void Dispose() => TryDeleteDirectory(RootDir);
    }
}
