using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Tests for detecting when a referenced source-generator / analyzer assembly changes
/// on disk, so the cached workspace can be reloaded instead of serving stale generator
/// output (and stale shadow copies).
/// </summary>
public sealed class WorkspaceContextStalenessTests
{
    [Fact]
    public void IsMutableAnalyzerPath_ExcludesNuGetAndSdk_IncludesBuildOutput()
    {
        // NuGet-restored analyzers are immutable; matched the way the implementation does.
        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(nugetRoot))
            nugetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var nugetAnalyzer = Path.Combine(nugetRoot, "some.pkg", "1.0.0", "analyzers", "dotnet", "cs", "Gen.dll");
        Assert.False(WorkspaceContext.IsMutableAnalyzerPath(nugetAnalyzer));

        var sdkAnalyzer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "sdk", "10.0.300", "Sdks", "X", "Razor.dll");
        Assert.False(WorkspaceContext.IsMutableAnalyzerPath(sdkAnalyzer));

        // A project build output (the kind a `dotnet build` rewrites) is mutable.
        var buildOutput = Path.Combine(
            Path.GetTempPath(), "repo", ".artifacts", "bin", "X.SourceGenerator", "debug", "X.SourceGenerator.dll");
        Assert.True(WorkspaceContext.IsMutableAnalyzerPath(buildOutput));
    }

    [Fact]
    public void CaptureAndDetect_TracksMutableReference_AndSeesContentAndTimestampChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"RoslynMcp.StaleTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var generatorDll = Path.Combine(tempDir, "Local.Generator.dll");
        File.WriteAllBytes(generatorDll, new byte[] { 1, 2, 3, 4 });

        try
        {
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var info = ProjectInfo
                .Create(projectId, VersionStamp.Default, "P", "P", LanguageNames.CSharp)
                .WithAnalyzerReferences(new[]
                {
                    new AnalyzerFileReference(generatorDll, new NoopLoader()),
                });
            var solution = workspace.CurrentSolution.AddProject(info);

            var stamps = WorkspaceContext.CaptureMutableAnalyzerStamps(solution);
            Assert.Single(stamps); // the project-local generator is tracked
            Assert.False(WorkspaceContext.StampsChanged(stamps));

            // Content (length) change is detected.
            File.WriteAllBytes(generatorDll, new byte[] { 1, 2, 3, 4, 5, 6 });
            Assert.True(WorkspaceContext.StampsChanged(stamps));

            // Re-capture, then a same-length, timestamp-only change is detected too.
            stamps = WorkspaceContext.CaptureMutableAnalyzerStamps(solution);
            Assert.False(WorkspaceContext.StampsChanged(stamps));
            File.SetLastWriteTimeUtc(generatorDll, File.GetLastWriteTimeUtc(generatorDll).AddHours(1));
            Assert.True(WorkspaceContext.StampsChanged(stamps));

            // Removal (e.g. clean) is detected.
            stamps = WorkspaceContext.CaptureMutableAnalyzerStamps(solution);
            File.Delete(generatorDll);
            Assert.True(WorkspaceContext.StampsChanged(stamps));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class NoopLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => throw new NotSupportedException();
    }
}
