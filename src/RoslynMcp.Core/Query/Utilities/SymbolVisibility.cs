using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Where a type or member can be referenced from, judged by its effective accessibility: the
/// most restrictive of its own and its containing types' (a public member of an internal type
/// is internal).
/// </summary>
internal static class SymbolVisibility
{
    /// <summary>
    /// Whether code in another assembly can reference <paramref name="symbol"/>: every level is
    /// public, or protected in a type that can be derived from.
    /// </summary>
    public static bool IsVisibleOutsideAssembly(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingType)
        {
            if (current is INamedTypeSymbol { IsFileLocal: true })
                return false;

            switch (current.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    break;
                case Accessibility.Protected or Accessibility.ProtectedOrInternal when CanBeDerivedFrom(current.ContainingType):
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The type whose declarations are the only place <paramref name="symbol"/> can be
    /// referenced from: the containing type of its innermost private level (a protected member
    /// of a sealed type counts as private). Null when it is visible beyond one type.
    /// </summary>
    public static INamedTypeSymbol? PrivateDomain(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility == Accessibility.Private ||
                (current.DeclaredAccessibility == Accessibility.Protected && !CanBeDerivedFrom(current.ContainingType)))
            {
                return current.ContainingType;
            }
        }

        return null;
    }

    /// <summary>
    /// The file-local type (<c>file class</c>) <paramref name="symbol"/> is or lies in, whose
    /// file is the only place it can be referenced from; null when there is none.
    /// </summary>
    public static INamedTypeSymbol? FileLocalType(ISymbol symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type != null; type = type.ContainingType)
        {
            if (type.IsFileLocal)
                return type;
        }

        return null;
    }

    private static bool CanBeDerivedFrom(INamedTypeSymbol? type) =>
        type is { IsSealed: false, IsStatic: false, TypeKind: TypeKind.Class or TypeKind.Interface };
}
