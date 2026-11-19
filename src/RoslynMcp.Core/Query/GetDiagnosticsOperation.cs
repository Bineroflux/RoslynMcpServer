using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Retrieves compiler diagnostics for the solution or a specific file.
/// Delegates to Roslyn's Compilation.GetDiagnostics().
/// </summary>
public sealed class GetDiagnosticsOperation : QueryOperationBase<GetDiagnosticsParams, GetDiagnosticsResult>
{
    /// <summary>The ID of the diagnostic Roslyn reports for an analyzer that threw.</summary>
    private const string AnalyzerExceptionId = "AD0001";

    /// <inheritdoc />
    public GetDiagnosticsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GetDiagnosticsParams @params)
    {
        if (!string.IsNullOrWhiteSpace(@params.SourceFile))
        {
            if (!PathResolver.IsAbsolutePath(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

            if (!PathResolver.IsValidDiagnosticsSourcePath(@params.SourceFile))
                throw new RefactoringException(
                    ErrorCodes.InvalidSourcePath,
                    "sourceFile must be a .cs, .razor, or .cshtml file.");

            if (!File.Exists(@params.SourceFile))
                throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
        }

        if (!string.IsNullOrWhiteSpace(@params.SeverityFilter) &&
            !Enum.TryParse<DiagnosticSeverityFilter>(@params.SeverityFilter, ignoreCase: true, out _))
        {
            var valid = string.Join(", ", Enum.GetNames<DiagnosticSeverityFilter>());
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"Invalid severityFilter. Valid values: {valid}");
        }

        QueryScope.ValidateProjectPath(@params.ProjectPath);

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetDiagnosticsResult>> ExecuteCoreAsync(
        Guid operationId,
        GetDiagnosticsParams @params,
        CancellationToken cancellationToken)
    {
        var severityFilter = ParseSeverityFilter(@params.SeverityFilter);
        var projectScope = QueryScope.ResolveProject(Context.Solution, @params.ProjectPath);
        var contentFilter = new DiagnosticContentFilter(@params);
        var diagnostics = new DiagnosticCollector(Context.Solution);

        // The caller's sourceFile is matched against each diagnostic's span path
        // below. Normalize it once up front: the workspace normalizes document (and
        // therefore span) paths at load time via PathResolver.NormalizePath, so the
        // raw caller-supplied path has to be normalized the same way or an
        // equivalent-but-not-byte-identical path — an 8.3 short-name component
        // (e.g. PROGRA~1 vs Program Files), mixed '/' vs '\', or '..' segments —
        // would match nothing and silently drop every diagnostic for the file.
        var normalizedSourceFile = string.IsNullOrWhiteSpace(@params.SourceFile)
            ? null
            : PathResolver.NormalizePath(@params.SourceFile);

        // The projects whose analyzers this call wants (includeAnalyzers).
        var analyzedProjects = new List<Project>();

        // A projectPath compiles only its own project(s), which is what makes it cheaper.
        var projects = projectScope?.Projects ?? Context.Solution.Projects;
        foreach (var project in projects)
        {
            // Roslyn's compilation tracker eagerly enumerates generators across
            // every AnalyzerReference while building the final compilation
            // state, so a buggy generator (throwing during Initialize, in a
            // ctor, or via an AnalyzerReference override) propagates out of
            // GetCompilationAsync. Catching it here lets us surface RMCP0002
            // for the offending project and still return diagnostics for the
            // rest of the solution, instead of failing the whole call.
            Compilation? compilation = null;
            string? generatorFailure = null;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                generatorFailure = ex.Message;
            }

            // compilation.GetDiagnostics() includes everything Roslyn computes itself —
            // including CS errors inside generator-emitted .g.cs trees — but NOT the
            // diagnostics that source generators report via ReportDiagnostic. RZ
            // diagnostics from the Razor source generator fall into that gap, so we
            // re-run the generators and union their reported diagnostics in.
            var generatorDiags = ImmutableArray<Diagnostic>.Empty;
            if (compilation is not null)
            {
                var helperResult = await GetGeneratorReportedDiagnosticsAsync(
                    project, compilation, cancellationToken);
                generatorDiags = helperResult.Diagnostics;
                generatorFailure ??= helperResult.Failure;
            }

            // Surface a generator runtime failure as a synthetic info-level
            // diagnostic (RMCP0002) so callers don't get silently-empty results
            // when a generator crashes during construction or execution. Same
            // severity-filter gate as RMCP0001.
            if (generatorFailure is not null && severityFilter != DiagnosticSeverityFilter.Error)
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    Id = "RMCP0002",
                    Message = $"Source generator failed while processing project '{project.Name}': {generatorFailure}. " +
                              "Generator-reported diagnostics (e.g. RZ codes for .razor) will be missing.",
                    Severity = DiagnosticSeverity.Info.ToString(),
                    Category = "RoslynMcp.Workspace",
                    File = null,
                    Line = 0,
                    Column = 0
                }, project, span: default);
            }

            var analyzersWanted = @params.IncludeAnalyzers == true && WantsAnalyzers(project, normalizedSourceFile);
            if (analyzersWanted)
            {
                analyzedProjects.Add(project);

                // The load removed the analyzer references Roslyn couldn't resolve; whether they
                // hold an analyzer for a wanted ID can't be known.
                foreach (var unresolved in Context.UnresolvedAnalyzerReferencesOf(project.Id))
                {
                    AddAnalyzerFailureMessage(
                        diagnostics,
                        project,
                        $"Analyzer reference '{unresolved.Path}' of project '{project.Name}' could not be resolved " +
                        $"({unresolved.Reason}), so its analyzers did not run; the result may be incomplete.");
                }
            }

            if (compilation is null)
            {
                if (analyzersWanted)
                    AddAnalyzerFailure(diagnostics, project, $"the project could not be compiled: {generatorFailure}");
                continue;
            }

            var reported = analyzersWanted
                ? await GetDiagnosticsWithAnalyzersAsync(project, compilation, contentFilter, normalizedSourceFile, diagnostics, cancellationToken)
                : compilation.GetDiagnostics(cancellationToken);

            foreach (var diag in reported.Concat(generatorDiags))
            {
                // Only an analyzer this call ran can have thrown: its AD0001 means the result is
                // incomplete, so no filter drops it.
                var analyzerFailure = diag.Id == AnalyzerExceptionId;
                if (!analyzerFailure && !PassesSeverityFilter(diag.Severity, severityFilter))
                    continue;

                // Razor diagnostics arrive in two flavours: (a) CS errors from the
                // generator-emitted *_razor.g.cs trees, whose Location.IsInSource is
                // true and whose GetMappedLineSpan() honours #line pragmas back to
                // the .razor; and (b) RZ-prefixed parser errors the Razor generator
                // reports with a synthetic ExternalFile location pointing at the
                // .razor directly — IsInSource is false there, but GetLineSpan()
                // still returns a usable path/line. We try the mapped span first,
                // then fall back to the unmapped span. The whole expression is
                // skipped only when the diagnostic has no location at all.
                var hasLocation = diag.Location.Kind != LocationKind.None;
                var unmappedSpan = hasLocation ? diag.Location.GetLineSpan() : default;
                var mappedSpan = hasLocation ? diag.Location.GetMappedLineSpan() : default;
                var preferredSpan = mappedSpan.IsValid && mappedSpan.HasMappedPath ? mappedSpan : unmappedSpan;

                // Filter by file if specified.
                if (normalizedSourceFile is not null && !analyzerFailure)
                {
                    if (!hasLocation)
                        continue;

                    var matches =
                        SpanPathMatchesSourceFile(preferredSpan.Path, normalizedSourceFile) ||
                        SpanPathMatchesSourceFile(unmappedSpan.Path, normalizedSourceFile);
                    if (!matches)
                        continue;
                }

                string? file = null;
                int line = 0;
                int column = 0;

                if (hasLocation)
                {
                    file = preferredSpan.Path;
                    line = preferredSpan.StartLinePosition.Line + 1;
                    column = preferredSpan.StartLinePosition.Character + 1;
                }

                var info = new DiagnosticInfo
                {
                    Id = diag.Id,
                    Message = diag.GetMessage(),
                    Severity = diag.Severity.ToString(),
                    Category = diag.Descriptor.Category,
                    File = file,
                    Line = line,
                    Column = column
                };

                diagnostics.Add(info, project, hasLocation ? preferredSpan : default, analyzerFailure);
            }
        }

        // Surface workspace-level load issues (e.g. the Razor source generator
        // failed to load) as synthetic info-level diagnostics. Emitted whenever
        // the caller's filter would include informational results — i.e. for
        // anything except an explicit Errors-only request, because the message
        // explains why their .razor query came back empty. A projectPath keeps
        // only the issues of its project. Collected after the projects ran: an
        // analyzer that fails to load is only noticed when its analyzers are listed.
        List<string> issues;
        lock (Context.GeneratorLoadIssues)
        {
            // The list grows when an analyzer fails to load later; it is locked while it does.
            issues = Context.GeneratorLoadIssues.ToList();
        }

        foreach (var issue in issues)
        {
            // A reference that could not be loaded, in a project whose analyzers this call
            // wants, may hold analyzers reporting a wanted ID (which IDs can't be known): like
            // an analyzer failure, it is reported whatever the filters.
            var analyzerFailure = MSBuildWorkspaceProvider.IsAnalyzerLoadFailure(issue) &&
                analyzedProjects.Any(p => WorkspaceContext.IssueConcernsProject(issue, p.Name));
            if (!analyzerFailure)
            {
                if (severityFilter == DiagnosticSeverityFilter.Error)
                    continue;

                if (projectScope is not null &&
                    !projectScope.Projects.Any(p => WorkspaceContext.IssueConcernsProject(issue, p.Name)))
                {
                    continue;
                }
            }

            diagnostics.Add(new DiagnosticInfo
            {
                Id = "RMCP0001",
                Message = issue,
                Severity = DiagnosticSeverity.Info.ToString(),
                Category = "RoslynMcp.Workspace",
                File = null,
                Line = 0,
                Column = 0
            }, project: null, span: default, analyzerFailure);
        }

        // Sort, since compilations bind method bodies concurrently and projects are
        // independent: diagnostics without a file (RMCP0001-RMCP0003, project-level) first,
        // then by file, line, column, ID and message. The failures of analyzers this call
        // wanted go before everything else and past the ID and message filters: they mean
        // the result is incomplete, and maxResults must not cut them off. Filter before
        // truncating, so totalCount counts every match.
        var entries = diagnostics.Entries.ToList();
        var matching = Sort(entries.Where(e => e.AnalyzerFailure).Select(e => e.Info))
            .Concat(Sort(entries.Where(e => !e.AnalyzerFailure).Select(e => e.Info).Where(contentFilter.Matches)))
            .ToList();
        var maxResults = @params.MaxResults ?? int.MaxValue;
        var returned = matching.Count > maxResults ? matching.Take(maxResults).ToList() : matching;

        var result = new GetDiagnosticsResult
        {
            Diagnostics = returned,
            TotalCount = matching.Count,
            Truncated = matching.Count > returned.Count
        };

        return QueryResult<GetDiagnosticsResult>.Succeeded(operationId, result);

        static IEnumerable<DiagnosticInfo> Sort(IEnumerable<DiagnosticInfo> diagnostics) =>
            ResultOrder.ByLocation(
                diagnostics,
                d => d.File,
                d => d.Line ?? 0,
                d => d.Column ?? 0,
                d => d.Id + "\0" + d.Message);
    }

    /// <summary>
    /// Whether includeAnalyzers runs the analyzers of <paramref name="project"/>: unless a
    /// <c>.cs</c> <paramref name="normalizedSourceFile"/> is not one of its documents.
    /// </summary>
    private static bool WantsAnalyzers(Project project, string? normalizedSourceFile) =>
        normalizedSourceFile is null ||
        !PathResolver.IsValidCSharpFilePath(normalizedSourceFile) ||
        project.Documents.Any(d => SpanPathMatchesSourceFile(d.FilePath, normalizedSourceFile));

    /// <summary>
    /// The compiler's and the analyzers' diagnostics of <paramref name="project"/>, from the
    /// analyzers that can report an ID the filters keep. A <c>.cs</c> <paramref name="normalizedSourceFile"/>
    /// limits the analyzers to that file. Analyzers that can't be listed or run are reported
    /// as RMCP0003, with the compiler's diagnostics returned on their own.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsWithAnalyzersAsync(
        Project project,
        Compilation compilation,
        DiagnosticContentFilter contentFilter,
        string? normalizedSourceFile,
        DiagnosticCollector diagnostics,
        CancellationToken cancellationToken)
    {
        var tree = normalizedSourceFile is not null && PathResolver.IsValidCSharpFilePath(normalizedSourceFile)
            ? compilation.SyntaxTrees.FirstOrDefault(t => SpanPathMatchesSourceFile(t.FilePath, normalizedSourceFile))
            : null;

        var failures = new List<string>();
        var analyzers = ProjectAnalyzers.Select(project, contentFilter.IncludedIds, contentFilter.ExcludedIds, failures);
        ImmutableArray<Diagnostic> reported;
        try
        {
            reported = await ProjectAnalyzers.GetDiagnosticsAsync(
                compilation, analyzers, project.AnalyzerOptions, tree, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
            reported = compilation.GetDiagnostics(cancellationToken);
        }

        foreach (var failure in failures)
            AddAnalyzerFailure(diagnostics, project, failure);

        return reported;
    }

    /// <summary>
    /// Reports that analyzers of <paramref name="project"/> this call wanted could not run
    /// (RMCP0003). It means the result is incomplete, so no filter drops it.
    /// </summary>
    private static void AddAnalyzerFailure(DiagnosticCollector diagnostics, Project project, string failure) =>
        AddAnalyzerFailureMessage(
            diagnostics,
            project,
            $"Analyzers failed while processing project '{project.Name}': {failure}. Their diagnostics will be missing.");

    /// <inheritdoc cref="AddAnalyzerFailure"/>
    private static void AddAnalyzerFailureMessage(DiagnosticCollector diagnostics, Project project, string message) =>
        diagnostics.Add(new DiagnosticInfo
        {
            Id = "RMCP0003",
            Message = message,
            Severity = DiagnosticSeverity.Info.ToString(),
            Category = "RoslynMcp.Workspace",
            File = null,
            Line = 0,
            Column = 0
        }, project, span: default, analyzerFailure: true);

    private static DiagnosticSeverityFilter ParseSeverityFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return DiagnosticSeverityFilter.Warning;

        return Enum.Parse<DiagnosticSeverityFilter>(filter, ignoreCase: true);
    }

    /// <summary>
    /// Whether a diagnostic span's file path refers to the same file as the
    /// caller-supplied <paramref name="normalizedSourceFile"/> (already run through
    /// <see cref="PathResolver.NormalizePath"/>). The span path is normalized the
    /// same way before comparing, so equivalent-but-differently-spelled paths — 8.3
    /// short-name components, mixed directory separators, or <c>..</c> segments —
    /// still match. Returns false for a null/empty span path (e.g. a location with
    /// no associated file).
    /// </summary>
    private static bool SpanPathMatchesSourceFile(string? spanPath, string normalizedSourceFile)
    {
        if (string.IsNullOrEmpty(spanPath))
            return false;

        return PathResolver.PathComparer.Equals(PathResolver.NormalizePath(spanPath), normalizedSourceFile);
    }

    private static bool PassesSeverityFilter(DiagnosticSeverity severity, DiagnosticSeverityFilter filter)
    {
        return filter switch
        {
            DiagnosticSeverityFilter.Error => severity == DiagnosticSeverity.Error,
            DiagnosticSeverityFilter.Warning => severity >= DiagnosticSeverity.Warning,
            DiagnosticSeverityFilter.Info => severity >= DiagnosticSeverity.Info,
            DiagnosticSeverityFilter.Hidden => true,
            DiagnosticSeverityFilter.All => true,
            _ => severity >= DiagnosticSeverity.Warning
        };
    }

    /// <summary>
    /// Runs the project's source generators against the supplied compilation and
    /// returns the diagnostics they reported via <c>GeneratorExecutionContext.ReportDiagnostic</c>.
    /// These are not part of <see cref="Compilation.GetDiagnostics(CancellationToken)"/>,
    /// so callers that want to surface generator-side diagnostics (e.g. Razor's
    /// RZ codes) must collect them separately. Returns an empty array (and a
    /// non-null failure message) when the driver throws during construction or
    /// execution — the caller surfaces that as RMCP0002. Returns an empty array
    /// with a null failure message when the project has no generators.
    /// </summary>
    private static async Task<(ImmutableArray<Diagnostic> Diagnostics, string? Failure)>
        GetGeneratorReportedDiagnosticsAsync(
            Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        ImmutableArray<ISourceGenerator> generators;
        try
        {
            generators = project.AnalyzerReferences
                .SelectMany(r => r.GetGenerators(LanguageNames.CSharp))
                .ToImmutableArray();
        }
        catch (Exception ex)
        {
            return (ImmutableArray<Diagnostic>.Empty, $"failed to enumerate generators: {ex.Message}");
        }
        if (generators.IsEmpty)
            return (ImmutableArray<Diagnostic>.Empty, null);

        var additionalTexts = ImmutableArray.CreateBuilder<AdditionalText>();
        foreach (var doc in project.AdditionalDocuments)
        {
            if (string.IsNullOrEmpty(doc.FilePath)) continue;
            var text = await doc.GetTextAsync(cancellationToken);
            additionalTexts.Add(new ProjectAdditionalText(doc.FilePath, text));
        }

        var parseOptions = project.ParseOptions as CSharpParseOptions;

        // Pass the project's analyzer-config options provider so the generator
        // sees MSBuild properties (e.g. build_property.RazorLangVersion,
        // build_property.RootNamespace). Without it the Razor source generator
        // emits RZ3600 ("Invalid value '' for RazorLangVersion") and uses a
        // synthetic 'ASP.C_*' namespace, which then conflicts with the
        // properly-namespaced components Roslyn's internal driver already
        // produced — surfacing as bogus RZ9985 "Multiple components use the
        // tag X" and RZ10009 duplicate-parameter diagnostics.
        var optionsProvider = project.AnalyzerOptions?.AnalyzerConfigOptionsProvider;

        // Strip Roslyn's internal generator output from the compilation before
        // re-running. If we leave it in, the Razor generator's tag-helper
        // discovery sees both Roslyn's already-emitted components and the
        // components it is about to emit, producing duplicate-component
        // diagnostics that don't appear in `dotnet build`.
        var cleanCompilation = StripGeneratedTrees(project, compilation);

        try
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators: generators,
                additionalTexts: additionalTexts.ToImmutable(),
                parseOptions: parseOptions,
                optionsProvider: optionsProvider);
            driver = driver.RunGenerators(cleanCompilation, cancellationToken);
            return (driver.GetRunResult().Diagnostics, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A generator that throws during construction or execution shouldn't
            // take down the whole get_diagnostics call. The compilation diagnostics
            // are still returned; the generator-side ones just won't be included.
            // Surface the failure as RMCP0002 via the caller.
            return (ImmutableArray<Diagnostic>.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Returns <paramref name="compilation"/> with any syntax trees that did not
    /// originate from a regular project <see cref="Document"/> removed. In
    /// practice this drops the trees Roslyn's internal generator driver added,
    /// giving us a "pre-generation" compilation safe to feed back into a
    /// manual driver run without producing duplicate-emit diagnostics.
    /// </summary>
    private static Compilation StripGeneratedTrees(Project project, Compilation compilation)
    {
        var documentPaths = new HashSet<string>(PathResolver.PathComparer);
        foreach (var doc in project.Documents)
        {
            if (!string.IsNullOrEmpty(doc.FilePath))
                documentPaths.Add(doc.FilePath);
        }

        var toRemove = new List<SyntaxTree>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            // Generated trees typically have a synthetic path of the form
            // "<GeneratorTypeName>/<output>.g.cs" that never matches a real
            // document path. Anything that doesn't correspond to a project
            // document is treated as generator output and dropped.
            if (!documentPaths.Contains(tree.FilePath))
                toRemove.Add(tree);
        }

        return toRemove.Count == 0 ? compilation : compilation.RemoveSyntaxTrees(toRemove);
    }

    /// <summary>
    /// Collects the diagnostics, listing an identical one (same ID, location, message and
    /// severity) once. Each target framework of a multi-targeted project is compiled on its
    /// own and reports the same diagnostic again; such a diagnostic names the frameworks that
    /// reported it. A linked file compiled by several projects is listed once as well.
    /// </summary>
    private sealed class DiagnosticCollector
    {
        private readonly Dictionary<DiagnosticKey, Entry> _byKey = new();
        private readonly List<DiagnosticKey> _order = new();
        private readonly HashSet<ProjectId> _multiTargeted;

        public DiagnosticCollector(Solution solution)
        {
            // A multi-targeted project is one project file loaded once per framework.
            _multiTargeted = solution.Projects
                .Where(p => !string.IsNullOrEmpty(p.FilePath))
                .GroupBy(p => PathResolver.NormalizePath(p.FilePath!), PathResolver.PathComparer)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(p => p.Id))
                .ToHashSet();
        }

        /// <param name="info">The diagnostic.</param>
        /// <param name="project">The project that reported it, if any.</param>
        /// <param name="span">Its location.</param>
        /// <param name="analyzerFailure">
        /// Whether it says that analyzers this call wanted failed (AD0001, RMCP0003, a load
        /// failure in RMCP0001), so the result is incomplete.
        /// </param>
        public void Add(DiagnosticInfo info, Project? project, FileLinePositionSpan span, bool analyzerFailure = false)
        {
            var key = new DiagnosticKey(
                info.Id, info.File ?? "", span.StartLinePosition, span.EndLinePosition, info.Message, info.Severity);
            if (!_byKey.TryGetValue(key, out var entry))
            {
                entry = new Entry(info);
                _byKey[key] = entry;
                _order.Add(key);
            }

            entry.AnalyzerFailure |= analyzerFailure;

            if (project is not null && _multiTargeted.Contains(project.Id))
            {
                var framework = WorkspaceDiagnoser.TargetFrameworkOf(project) ?? project.Name;
                if (!entry.Frameworks.Contains(framework))
                    entry.Frameworks.Add(framework);
            }
        }

        /// <summary>The diagnostics, each with whether it is an analyzer failure.</summary>
        public IEnumerable<(DiagnosticInfo Info, bool AnalyzerFailure)> Entries => _order.Select(key =>
        {
            var (info, frameworks) = (_byKey[key].Info, _byKey[key].Frameworks);
            var listed = frameworks.Count == 0 ? info : new DiagnosticInfo
            {
                Id = info.Id,
                Message = info.Message,
                Severity = info.Severity,
                File = info.File,
                Line = info.Line,
                Column = info.Column,
                Category = info.Category,
                TargetFrameworks = frameworks
            };
            return (listed, _byKey[key].AnalyzerFailure);
        });

        private sealed class Entry(DiagnosticInfo info)
        {
            public DiagnosticInfo Info { get; } = info;
            public List<string> Frameworks { get; } = [];
            public bool AnalyzerFailure { get; set; }
        }

        private readonly record struct DiagnosticKey(
            string Id, string File, LinePosition Start, LinePosition End, string Message, string Severity);
    }

    /// <summary>
    /// The ID and message filters. They apply to every diagnostic, the synthetic
    /// RMCP0001/RMCP0002 and generator-reported (e.g. RZ) ones included, except the failures
    /// of analyzers the call wanted (AD0001, RMCP0003, an RMCP0001 load failure in such a
    /// project), which mean the result is incomplete.
    /// </summary>
    private sealed class DiagnosticContentFilter
    {
        private readonly HashSet<string>? _include;
        private readonly HashSet<string> _exclude;
        private readonly string? _message;

        /// <summary>The IDs to keep, or null for every ID.</summary>
        public IReadOnlySet<string>? IncludedIds => _include;

        /// <summary>The IDs to leave out.</summary>
        public IReadOnlySet<string> ExcludedIds => _exclude;

        public DiagnosticContentFilter(GetDiagnosticsParams @params)
        {
            var include = ToIdSet(@params.DiagnosticIds);
            _include = include.Count > 0 ? include : null;
            _exclude = ToIdSet(@params.ExcludeDiagnosticIds);
            _message = string.IsNullOrEmpty(@params.MessageFilter) ? null : @params.MessageFilter;
        }

        public bool Matches(DiagnosticInfo diagnostic)
        {
            if (_exclude.Contains(diagnostic.Id))
                return false;

            if (_include is not null && !_include.Contains(diagnostic.Id))
                return false;

            return _message is null || diagnostic.Message.Contains(_message, StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> ToIdSet(IReadOnlyList<string>? ids) =>
            (ids ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProjectAdditionalText : AdditionalText
    {
        private readonly SourceText _text;
        public ProjectAdditionalText(string path, SourceText text) { Path = path; _text = text; }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
