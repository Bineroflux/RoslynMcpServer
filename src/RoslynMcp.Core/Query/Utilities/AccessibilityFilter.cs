using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Parses an <c>accessibilityFilter</c> parameter: the C# modifiers the visibility
/// parameters of the refactoring tools take, case-insensitive.
/// </summary>
internal static class AccessibilityFilter
{
    private static readonly Dictionary<string, Accessibility> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public"] = Accessibility.Public,
        ["internal"] = Accessibility.Internal,
        ["protected"] = Accessibility.Protected,
        ["private"] = Accessibility.Private,
        ["protected internal"] = Accessibility.ProtectedOrInternal,
        ["private protected"] = Accessibility.ProtectedAndInternal,

        // How get_symbol_info itself reports the two compound ones.
        ["protectedorinternal"] = Accessibility.ProtectedOrInternal,
        ["protectedandinternal"] = Accessibility.ProtectedAndInternal,
    };

    /// <summary>
    /// The accessibility <paramref name="accessibilityFilter"/> names, or null when it is null or blank.
    /// </summary>
    /// <exception cref="RefactoringException">The value is not an accessibility.</exception>
    public static Accessibility? Parse(string? accessibilityFilter)
    {
        if (string.IsNullOrWhiteSpace(accessibilityFilter))
            return null;

        var normalized = string.Join(' ', accessibilityFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (Values.TryGetValue(normalized, out var accessibility))
            return accessibility;

        throw new RefactoringException(
            ErrorCodes.InvalidVisibility,
            $"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: " +
            "public, internal, protected, private, protected internal, private protected");
    }
}
