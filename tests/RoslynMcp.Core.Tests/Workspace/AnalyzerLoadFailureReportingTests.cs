using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Core.Workspace;
using Xunit;

using FailureErrorCode = Microsoft.CodeAnalysis.Diagnostics.AnalyzerLoadFailureEventArgs.FailureErrorCode;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Tests for the analyzer/source-generator load-failure self-report that feeds
/// <c>get_diagnostics</c>' <c>RMCP0001</c> info diagnostic.
///
/// The motivating failure is silent: when an SDK's Razor generator binds against a newer
/// Roslyn than the host (SDK 10.0.400 wants 5.9.0.0), assembly binding refuses to roll
/// backward and <c>GetGenerators</c> returns an <em>empty array</em> — no exception, no
/// unresolved reference. Every RZ diagnostic and every CS diagnostic from the generated
/// <c>.g.cs</c> vanishes while the server reports a confident zero. Roslyn does raise
/// <see cref="AnalyzerFileReference.AnalyzerLoadFailed"/> with
/// <see cref="FailureErrorCode.ReferencesNewerCompiler"/>, which is what these tests cover.
/// </summary>
public sealed class AnalyzerLoadFailureReportingTests
{
    [Fact]
    public void ReferencesNewerCompiler_MessageNamesHostVersionAndTheFix()
    {
        var message = MSBuildWorkspaceProvider.BuildAnalyzerLoadFailureMessage(
            "RazorProject",
            "Microsoft.CodeAnalysis.Razor.Compiler",
            FailureErrorCode.ReferencesNewerCompiler,
            detail: null,
            hostRoslynVersion: new Version(5, 6, 0, 0));

        Assert.Contains("Microsoft.CodeAnalysis.Razor.Compiler", message);
        Assert.Contains("RazorProject", message);
        Assert.Contains("5.6.0.0", message);
        Assert.Contains("newer Roslyn", message);
        // The actionable half: the user has to move the server, not the SDK.
        Assert.Contains("Upgrade roslyn-mcp", message);
    }

    [Fact]
    public void OtherFailure_MessageCarriesErrorCodeAndDetail()
    {
        var message = MSBuildWorkspaceProvider.BuildAnalyzerLoadFailureMessage(
            "SomeProject",
            "Broken.Analyzer",
            FailureErrorCode.UnableToLoadAnalyzer,
            detail: "Could not load file or assembly.",
            hostRoslynVersion: new Version(5, 9, 0, 0));

        Assert.Contains("Broken.Analyzer", message);
        Assert.Contains("SomeProject", message);
        Assert.Contains("UnableToLoadAnalyzer", message);
        Assert.Contains("Could not load file or assembly.", message);
    }

    [Fact]
    public void OtherFailure_WithoutDetail_FallsBackToTheErrorCode()
    {
        var message = MSBuildWorkspaceProvider.BuildAnalyzerLoadFailureMessage(
            "SomeProject",
            "Broken.Analyzer",
            FailureErrorCode.UnableToCreateAnalyzer,
            detail: null,
            hostRoslynVersion: null);

        Assert.Contains("UnableToCreateAnalyzer", message);
        Assert.DoesNotContain("null", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnloadableAnalyzer_IsRecordedAsAGeneratorLoadIssue()
    {
        // A file that is not a managed assembly reproduces the load-failure path end to
        // end: Roslyn swallows the error, returns no generators, and raises the event.
        var bogus = Path.Combine(
            Path.GetTempPath(), $"RoslynMcp.NotAnAssembly-{Guid.NewGuid():N}.dll");
        File.WriteAllText(bogus, "this is not a PE file");

        try
        {
            using var loader = new ShadowCopyAnalyzerAssemblyLoader();
            var reference = new AnalyzerFileReference(bogus, loader);

            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(
                ProjectInfo.Create(
                    ProjectId.CreateNewId(),
                    VersionStamp.Default,
                    name: "ProbeProject",
                    assemblyName: "ProbeProject",
                    language: LanguageNames.CSharp)
                .WithAnalyzerReferences([reference]));

            var issues = new List<string>();
            using var provider = new MSBuildWorkspaceProvider();
            provider.SubscribeToAnalyzerLoadFailures(project.Solution, issues);

            // Enumerating the extensions is what triggers the load attempt.
            var generators = reference.GetGenerators(LanguageNames.CSharp);

            Assert.Empty(generators);
            var issue = Assert.Single(issues);
            Assert.Contains("ProbeProject", issue);
            Assert.Contains(Path.GetFileNameWithoutExtension(bogus), issue);
        }
        finally
        {
            try { File.Delete(bogus); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RepeatedEnumeration_ReportsTheFailureOnlyOnce()
    {
        var bogus = Path.Combine(
            Path.GetTempPath(), $"RoslynMcp.NotAnAssembly-{Guid.NewGuid():N}.dll");
        File.WriteAllText(bogus, "this is not a PE file");

        try
        {
            using var loader = new ShadowCopyAnalyzerAssemblyLoader();
            var reference = new AnalyzerFileReference(bogus, loader);

            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(
                ProjectInfo.Create(
                    ProjectId.CreateNewId(),
                    VersionStamp.Default,
                    name: "ProbeProject",
                    assemblyName: "ProbeProject",
                    language: LanguageNames.CSharp)
                .WithAnalyzerReferences([reference]));

            var issues = new List<string>();
            using var provider = new MSBuildWorkspaceProvider();
            provider.SubscribeToAnalyzerLoadFailures(project.Solution, issues);

            // Analyzer and generator enumeration each raise the event for the same
            // reference; the caller must not see the same problem twice.
            reference.GetGenerators(LanguageNames.CSharp);
            reference.GetAnalyzers(LanguageNames.CSharp);

            Assert.Single(issues);
        }
        finally
        {
            try { File.Delete(bogus); } catch { /* best effort */ }
        }
    }
}
