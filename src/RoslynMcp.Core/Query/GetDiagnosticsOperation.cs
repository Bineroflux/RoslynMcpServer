using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
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
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetDiagnosticsResult>> ExecuteCoreAsync(
        Guid operationId,
        GetDiagnosticsParams @params,
        CancellationToken cancellationToken)
    {
        var severityFilter = ParseSeverityFilter(@params.SeverityFilter);
        var diagnostics = new List<DiagnosticInfo>();

        // Surface workspace-level load issues (e.g. the Razor source generator
        // failed to load) as synthetic info-level diagnostics. Emitted whenever
        // the caller's filter would include informational results — i.e. for
        // anything except an explicit Errors-only request, because the message
        // explains why their .razor query came back empty.
        if (severityFilter != DiagnosticSeverityFilter.Error)
        {
            foreach (var issue in Context.GeneratorLoadIssues)
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    Id = "RMCP0001",
                    Message = issue,
                    Severity = DiagnosticSeverity.Info.ToString(),
                    Category = "RoslynMcp.Workspace",
                    File = null,
                    Line = 0,
                    Column = 0
                });
            }
        }

        foreach (var project in Context.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            // compilation.GetDiagnostics() includes everything Roslyn computes itself —
            // including CS errors inside generator-emitted .g.cs trees — but NOT the
            // diagnostics that source generators report via ReportDiagnostic. RZ
            // diagnostics from the Razor source generator fall into that gap, so we
            // re-run the generators and union their reported diagnostics in.
            var generatorDiags = await GetGeneratorReportedDiagnosticsAsync(project, compilation, cancellationToken);

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
                if (!string.IsNullOrWhiteSpace(@params.SourceFile))
                {
                    if (!hasLocation)
                        continue;

                    var matches =
                        string.Equals(preferredSpan.Path, @params.SourceFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unmappedSpan.Path, @params.SourceFile, StringComparison.OrdinalIgnoreCase);
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

                diagnostics.Add(info);
            }
        }

        var result = new GetDiagnosticsResult
        {
            Diagnostics = diagnostics,
            TotalCount = diagnostics.Count
        };

        return QueryResult<GetDiagnosticsResult>.Succeeded(operationId, result);
    }

    private static DiagnosticSeverityFilter ParseSeverityFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return DiagnosticSeverityFilter.Warning;

        return Enum.Parse<DiagnosticSeverityFilter>(filter, ignoreCase: true);
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
    /// RZ codes) must collect them separately. Returns an empty array when the
    /// project has no generators or when the driver fails to construct (e.g.
    /// unresolved analyzer references — those are surfaced via RMCP0001 instead).
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> GetGeneratorReportedDiagnosticsAsync(
        Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        ImmutableArray<ISourceGenerator> generators;
        try
        {
            generators = project.AnalyzerReferences
                .SelectMany(r => r.GetGenerators(LanguageNames.CSharp))
                .ToImmutableArray();
        }
        catch
        {
            return ImmutableArray<Diagnostic>.Empty;
        }
        if (generators.IsEmpty) return ImmutableArray<Diagnostic>.Empty;

        var additionalTexts = ImmutableArray.CreateBuilder<AdditionalText>();
        foreach (var doc in project.AdditionalDocuments)
        {
            if (string.IsNullOrEmpty(doc.FilePath)) continue;
            var text = await doc.GetTextAsync(cancellationToken);
            additionalTexts.Add(new ProjectAdditionalText(doc.FilePath, text));
        }

        var parseOptions = project.ParseOptions as CSharpParseOptions;

        try
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators: generators,
                additionalTexts: additionalTexts.ToImmutable(),
                parseOptions: parseOptions,
                optionsProvider: null);
            driver = driver.RunGenerators(compilation, cancellationToken);
            return driver.GetRunResult().Diagnostics;
        }
        catch
        {
            // A generator that throws during construction or initialization shouldn't
            // take down the whole get_diagnostics call. The compilation diagnostics
            // are still returned; the generator-side ones just won't be included.
            return ImmutableArray<Diagnostic>.Empty;
        }
    }

    private sealed class ProjectAdditionalText : AdditionalText
    {
        private readonly SourceText _text;
        public ProjectAdditionalText(string path, SourceText text) { Path = path; _text = text; }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
