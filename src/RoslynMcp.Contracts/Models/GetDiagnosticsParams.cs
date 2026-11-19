namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_diagnostics query.
/// </summary>
public sealed class GetDiagnosticsParams
{
    /// <summary>
    /// Absolute path to a source file to restrict diagnostics to. If null, returns all solution diagnostics.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// Minimum severity to include. Default: Warning (includes Error and Warning).
    /// Valid values: Error, Warning, Info, Hidden, All.
    /// </summary>
    public string? SeverityFilter { get; init; }

    /// <summary>
    /// Absolute path to a project file of the solution. Only that project (every target
    /// framework of it) is compiled and reported. If null, the whole solution is.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Diagnostic IDs to include (e.g. CS0168), case-insensitive. If null or empty, all IDs.
    /// IDE/CA and other analyzer IDs only match with <see cref="IncludeAnalyzers"/>; then only
    /// the analyzers that can report one of these IDs run.
    /// </summary>
    public IReadOnlyList<string>? DiagnosticIds { get; init; }

    /// <summary>
    /// Diagnostic IDs to leave out, case-insensitive. Wins over <see cref="DiagnosticIds"/>.
    /// </summary>
    public IReadOnlyList<string>? ExcludeDiagnosticIds { get; init; }

    /// <summary>
    /// Case-insensitive substring the diagnostic message must contain.
    /// </summary>
    public string? MessageFilter { get; init; }

    /// <summary>
    /// Maximum number of diagnostics to return. If null, all of them.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Also run the projects' analyzers (their analyzer references: for an SDK project the .NET
    /// CA analyzers, the IDE code-style analyzers when the project sets
    /// <c>EnforceCodeStyleInBuild</c>, and those of package references), with the severities and
    /// suppressions the build applies. Much slower. Default: false (compiler, source-generator,
    /// Razor and RMCP diagnostics only). The failures of the analyzers it wants (AD0001 for one
    /// that threw, RMCP0003 for analyzers that could not be listed or run, RMCP0001 for a
    /// reference of such a project that could not be loaded) mean the result is incomplete:
    /// they are listed first, ahead of <see cref="MaxResults"/>, and pass every filter.
    /// </summary>
    public bool? IncludeAnalyzers { get; init; }
}
