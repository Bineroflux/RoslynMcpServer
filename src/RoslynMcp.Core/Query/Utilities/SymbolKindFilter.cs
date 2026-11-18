using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Parses the <c>kindFilter</c> parameter of the query tools: one
/// <see cref="Contracts.Enums.SymbolKind"/> name, case-insensitive.
/// </summary>
internal static class SymbolKindFilter
{
    /// <summary>
    /// The kind <paramref name="kindFilter"/> names, or null when it is null or blank.
    /// </summary>
    /// <exception cref="RefactoringException">The value is not a kind name.</exception>
    public static Contracts.Enums.SymbolKind? Parse(string? kindFilter)
    {
        if (string.IsNullOrWhiteSpace(kindFilter))
            return null;

        if (!Enum.TryParse<Contracts.Enums.SymbolKind>(kindFilter.Trim(), ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind))
        {
            var validKinds = string.Join(", ", Enum.GetNames<Contracts.Enums.SymbolKind>());
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Invalid kindFilter '{kindFilter}'. Valid values: {validKinds}");
        }

        return kind;
    }
}
