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

        // Surface workspace-level load issues (e.g. the Razor source generator
        // failed to load) as synthetic info-level diagnostics. Emitted whenever
        // the caller's filter would include informational results — i.e. for
        // anything except an explicit Errors-only request, because the message
        // explains why their .razor query came back empty. A projectPath keeps
        // only the issues of its project.
        if (severityFilter != DiagnosticSeverityFilter.Error)
        {
            List<string> issues;
            lock (Context.GeneratorLoadIssues)
            {
                // The list grows when an analyzer fails to load later; it is locked while it does.
                issues = Context.GeneratorLoadIssues.ToList();
            }

            foreach (var issue in issues)
            {
                if (projectScope is not null &&
                    !projectScope.Projects.Any(p => WorkspaceContext.IssueConcernsProject(issue, p.Name)))
                {
                    continue;
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
                }, project: null, span: default);
            }
        }

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

            if (compilation is null) continue;

            foreach (var diag in compilation.GetDiagnostics(cancellationToken).Concat(generatorDiags))
            {
                if (!PassesSeverityFilter(diag.Severity, severityFilter))
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

                // Filter by file if specified
                if (normalizedSourceFile is not null)
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

                diagnostics.Add(info, project, hasLocation ? preferredSpan : default);
            }
        }

        // Sort, since compilations bind method bodies concurrently and projects are
        // independent: diagnostics without a file (RMCP0001/RMCP0002, project-level) first,
        // then by file, line, column, ID and message. Filter before truncating, so
        // totalCount counts every match.
        var matching = ResultOrder.ByLocation(
                diagnostics.Diagnostics.Where(contentFilter.Matches),
                d => d.File,
                d => d.Line ?? 0,
                d => d.Column ?? 0,
                d => d.Id + "\0" + d.Message)
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
    }

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
        private readonly Dictionary<DiagnosticKey, (DiagnosticInfo Info, List<string> Frameworks)> _byKey = new();
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

        public void Add(DiagnosticInfo info, Project? project, FileLinePositionSpan span)
        {
            var key = new DiagnosticKey(
                info.Id, info.File ?? "", span.StartLinePosition, span.EndLinePosition, info.Message, info.Severity);
            if (!_byKey.TryGetValue(key, out var entry))
            {
                entry = (info, []);
                _byKey[key] = entry;
                _order.Add(key);
            }

            if (project is not null && _multiTargeted.Contains(project.Id))
            {
                var framework = WorkspaceDiagnoser.TargetFrameworkOf(project) ?? project.Name;
                if (!entry.Frameworks.Contains(framework))
                    entry.Frameworks.Add(framework);
            }
        }

        public IEnumerable<DiagnosticInfo> Diagnostics => _order.Select(key =>
        {
            var (info, frameworks) = _byKey[key];
            return frameworks.Count == 0 ? info : new DiagnosticInfo
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
        });

        private readonly record struct DiagnosticKey(
            string Id, string File, LinePosition Start, LinePosition End, string Message, string Severity);
    }

    /// <summary>
    /// The ID and message filters. They apply to every diagnostic, the synthetic
    /// RMCP0001/RMCP0002 and generator-reported (e.g. RZ) ones included.
    /// </summary>
    private sealed class DiagnosticContentFilter
    {
        private readonly HashSet<string>? _include;
        private readonly HashSet<string> _exclude;
        private readonly string? _message;

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
