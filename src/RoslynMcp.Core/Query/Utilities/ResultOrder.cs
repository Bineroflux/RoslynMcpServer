using Microsoft.CodeAnalysis;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Puts query results into a fixed order before they are truncated. SymbolFinder searches
/// documents in parallel, so its results arrive in a different order from one call to the
/// next, and <c>maxResults</c> would otherwise keep a different subset each time.
/// </summary>
internal static class ResultOrder
{
    /// <summary>
    /// Sorts <paramref name="items"/> by file (normalized, compared with
    /// <see cref="PathResolver.PathComparer"/>; items without a file first), then line, then
    /// column, then <paramref name="name"/> (ordinal).
    /// </summary>
    public static List<T> ByLocation<T>(
        IEnumerable<T> items,
        Func<T, string?> file,
        Func<T, int> line,
        Func<T, int> column,
        Func<T, string?> name)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);

        string Normalize(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return "";

            if (!normalized.TryGetValue(path, out var result))
            {
                result = PathResolver.NormalizePath(path);
                normalized[path] = result;
            }

            return result;
        }

        return items
            .Select(item => (Item: item, File: Normalize(file(item))))
            .OrderBy(x => x.File, PathResolver.PathComparer)
            .ThenBy(x => line(x.Item))
            .ThenBy(x => column(x.Item))
            .ThenBy(x => name(x.Item), StringComparer.Ordinal)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Sorts symbols by their first source location (symbols without one first), then by
    /// their display string.
    /// </summary>
    public static List<TSymbol> SymbolsByLocation<TSymbol>(IEnumerable<TSymbol> symbols)
        where TSymbol : ISymbol
    {
        var located = symbols
            .Select(s => (Symbol: s, Span: s.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan()))
            .ToList();

        return ByLocation(
                located,
                x => x.Span?.Path,
                x => x.Span?.StartLinePosition.Line ?? -1,
                x => x.Span?.StartLinePosition.Character ?? -1,
                x => x.Symbol.ToDisplayString())
            .Select(x => x.Symbol)
            .ToList();
    }
}
