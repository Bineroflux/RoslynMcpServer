namespace RoslynMcp.Core.Workspace;

/// <summary>
/// An analyzer reference MSBuild named for a project that Roslyn could not resolve (e.g. a
/// missing DLL). The workspace removes such references at load, so their analyzers and
/// source generators never run; <c>get_diagnostics</c> with <c>includeAnalyzers</c> reports
/// them as RMCP0003.
/// </summary>
/// <param name="Path">
/// The reference's path, made absolute against the project's directory when MSBuild gave a
/// relative one.
/// </param>
/// <param name="Reason">Why it could not be resolved.</param>
internal sealed record UnresolvedAnalyzerReferenceInfo(string Path, string Reason);
