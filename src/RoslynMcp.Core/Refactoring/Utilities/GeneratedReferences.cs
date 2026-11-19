using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// References that lie in generated code: in a source generator's output (a Razor component's
/// C#, say), which is no document of the project and is generated anew from its own source, or in
/// a generated file (<see cref="GeneratedCode"/>). Tools do not edit them; they leave them and
/// say where to change them by hand: the place a <c>#line</c> directive maps them to (the
/// <c>.razor</c> file and line) when there is one.
/// </summary>
internal static class GeneratedReferences
{
    /// <summary>Whether <paramref name="reference"/> is in generated code.</summary>
    public static bool IsGenerated(ReferenceLocation reference, CancellationToken cancellationToken) =>
        IsGenerated(reference.Document, reference.Location, cancellationToken);

    /// <summary>Whether <paramref name="location"/>, in <paramref name="document"/>, is in generated code.</summary>
    public static bool IsGenerated(Document document, Location location, CancellationToken cancellationToken) =>
        document is SourceGeneratedDocument ||
        (location.SourceTree is { } tree && GeneratedCode.IsGenerated(tree, cancellationToken));

    /// <summary>
    /// The warning for a reference left in generated code:
    /// <c>path(line,column): {what} is in code generated from this file ...; change it here by hand.</c>
    /// with the place <c>#line</c> maps it to, else its place in the generated code.
    /// </summary>
    /// <param name="location">The reference.</param>
    /// <param name="what">What is there, e.g. "the call to 'Api.Format(int, string)'".</param>
    /// <param name="left">What was not done to it, e.g. "rewritten".</param>
    public static string Warning(Location location, string what, string left) =>
        Place(location) is { Mapped: true } place
            ? $"{place.Text}: {what} is in code generated from this file and was not {left}; change it here by hand."
            : $"{Place(location).Text}: {what} is in generated code and was not {left}; change what generates it by hand.";

    /// <summary>
    /// The warnings for those of <paramref name="references"/> that are in generated code (see
    /// <see cref="Warning"/>), each place once.
    /// </summary>
    public static List<string> Warnings(
        IEnumerable<ReferenceLocation> references,
        string what,
        string left,
        CancellationToken cancellationToken) =>
        references
            .Where(r => r.Location.IsInSource && IsGenerated(r, cancellationToken))
            .Select(r => (Place(r.Location).Text, Warning: Warning(r.Location, what, left)))
            .DistinctBy(w => w.Text)
            .OrderBy(w => w.Text, StringComparer.OrdinalIgnoreCase)
            .Select(w => w.Warning)
            .ToList();

    /// <summary>
    /// Where <paramref name="location"/> is, as <c>path(line,column)</c>: where <c>#line</c> maps
    /// it to (and <c>Mapped</c>) when it does, else in its own file.
    /// </summary>
    public static (string Text, string Path, int Line, int Column, bool Mapped) Place(Location location)
    {
        var mapped = location.GetMappedLineSpan();
        var span = mapped.IsValid && mapped.HasMappedPath ? mapped : location.GetLineSpan();
        var (line, column) = (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        return ($"{span.Path}({line},{column})", span.Path, line, column, mapped.IsValid && mapped.HasMappedPath);
    }
}
