using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Runs a project's analyzers (the <see cref="DiagnosticAnalyzer"/>s and
/// <see cref="DiagnosticSuppressor"/>s of its <see cref="Project.AnalyzerReferences"/>) over its
/// compilation the way the build does: severities come from the compilation options and the
/// project's <c>.editorconfig</c> / global config files, <c>#pragma warning</c> and
/// <c>[SuppressMessage]</c> suppressions and the suppressors apply, and suppressed diagnostics
/// are left out.
/// </summary>
/// <remarks>
/// For an SDK project the references are what the build passes to the compiler: the .NET
/// analyzers (<c>Microsoft.CodeAnalysis.NetAnalyzers</c>, CA rules) unless
/// <c>EnableNETAnalyzers</c> is false, the code-style analyzers (IDE rules) only with
/// <c>EnforceCodeStyleInBuild</c>, and the analyzers of package references.
/// </remarks>
internal static class ProjectAnalyzers
{
    /// <summary>
    /// The analyzers of <paramref name="project"/> that can report (or, for a suppressor, suppress)
    /// a diagnostic the ID filters keep: with <paramref name="include"/>, one of those IDs; any ID
    /// not in <paramref name="exclude"/> otherwise. An analyzer whose IDs can't be read is kept, so
    /// the analyzer driver reports its exception. A reference whose analyzers can't be listed is
    /// skipped and described in <paramref name="failures"/>.
    /// </summary>
    public static ImmutableArray<DiagnosticAnalyzer> Select(
        Project project, IReadOnlySet<string>? include, IReadOnlySet<string> exclude, List<string> failures)
    {
        var selected = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var reference in project.AnalyzerReferences)
        {
            ImmutableArray<DiagnosticAnalyzer> analyzers;
            try
            {
                analyzers = reference.GetAnalyzers(project.Language);
            }
            catch (Exception ex)
            {
                failures.Add($"the analyzers of '{reference.Display}' could not be loaded: {ex.Message}");
                continue;
            }

            foreach (var analyzer in analyzers)
            {
                if (CanReportWanted(analyzer, include, exclude))
                    selected.Add(analyzer);
            }
        }

        return selected.ToImmutable();
    }

    /// <summary>
    /// The diagnostics of <paramref name="compilation"/> with the <paramref name="analyzers"/> run
    /// over it: the compiler's and the analyzers' together, with the suppressors applied to both.
    /// With a <paramref name="tree"/> the analyzers run on that file only, which is faster, except
    /// those reporting a diagnostic at the end of the compilation (tagged
    /// <see cref="WellKnownDiagnosticTags.CompilationEnd"/>, e.g. CA1812), which need the whole
    /// compilation; the compiler's diagnostics cover the whole compilation either way, so the
    /// caller filters by file. Exceptions thrown by an analyzer come back as Roslyn's
    /// <c>AD0001</c> warning.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
        Compilation compilation,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        AnalyzerOptions? options,
        SyntaxTree? tree,
        CancellationToken cancellationToken)
    {
        if (analyzers.IsEmpty)
            return compilation.GetDiagnostics(cancellationToken);

        // Roslyn adds the AD0001 of an analyzer that threw to a whole compilation's results but
        // not to a file's, so they are collected here as well; the caller lists identical
        // diagnostics once.
        var crashes = new ConcurrentQueue<Diagnostic>();
        var runOptions = new CompilationWithAnalyzersOptions(
            options ?? new AnalyzerOptions([]),
            onAnalyzerException: (_, _, diagnostic) => crashes.Enqueue(diagnostic),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false);

        var diagnostics = await RunAsync(compilation, analyzers, runOptions, tree, cancellationToken);
        return [.. diagnostics, .. crashes];
    }

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        CompilationWithAnalyzersOptions options,
        SyntaxTree? tree,
        CancellationToken cancellationToken)
    {
        if (tree is null)
            return await compilation.WithAnalyzers(analyzers, options).GetAllDiagnosticsAsync(cancellationToken);

        // The compiler's diagnostics only pass through a suppressor when the analyzer driver
        // computes them, which it does for the whole compilation; a compilation-end action
        // needs the whole compilation too. With no analyzers to run, that costs about as much
        // as compiling. The suppressors also run on the file, for the analyzers' diagnostics.
        var atCompilationEnd = analyzers.Where(ReportsAtCompilationEnd).ToHashSet();
        var suppressors = analyzers.OfType<DiagnosticSuppressor>();
        ImmutableArray<DiagnosticAnalyzer> wholeCompilation = [.. atCompilationEnd, .. suppressors];
        var compilerDiagnostics = wholeCompilation.IsEmpty
            ? compilation.GetDiagnostics(cancellationToken)
            : await compilation.WithAnalyzers(wholeCompilation, options).GetAllDiagnosticsAsync(cancellationToken);

        var onTheFile = analyzers.Where(a => !atCompilationEnd.Contains(a)).ToImmutableArray();
        if (onTheFile.All(a => a is DiagnosticSuppressor))
            return compilerDiagnostics;

        var withAnalyzers = compilation.WithAnalyzers(onTheFile, options);
        var model = withAnalyzers.Compilation.GetSemanticModel(tree);
        var syntax = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken);
        var semantic = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancellationToken);
        return [.. compilerDiagnostics, .. syntax, .. semantic];
    }

    private static bool ReportsAtCompilationEnd(DiagnosticAnalyzer analyzer)
    {
        try
        {
            return analyzer.SupportedDiagnostics.Any(d => d.CustomTags.Contains(WellKnownDiagnosticTags.CompilationEnd));
        }
        catch (Exception)
        {
            // The file's run reports the exception.
            return false;
        }
    }

    private static bool CanReportWanted(DiagnosticAnalyzer analyzer, IReadOnlySet<string>? include, IReadOnlySet<string> exclude)
    {
        if (include is null && exclude.Count == 0)
            return true;

        List<string> ids;
        try
        {
            ids = analyzer is DiagnosticSuppressor suppressor
                ? suppressor.SupportedSuppressions.Select(s => s.SuppressedDiagnosticId).ToList()
                : analyzer.SupportedDiagnostics.Select(d => d.Id).ToList();
        }
        catch (Exception)
        {
            return true;
        }

        return ids.Any(id => !exclude.Contains(id) && (include is null || include.Contains(id)));
    }
}
