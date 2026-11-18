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
    /// Only compiler, source-generator, Razor and RMCP diagnostics are reported (analyzers are
    /// not run), so IDE/CA IDs never match.
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
}
