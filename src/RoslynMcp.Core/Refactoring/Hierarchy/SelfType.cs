using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// An interface's self type parameter: the type parameter a type passes itself as
/// (<c>IAddable&lt;TSelf&gt;</c> implemented as <c>IAddable&lt;Money&gt;</c>) and that is constrained to
/// the interface itself (<c>where TSelf : IAddable&lt;TSelf&gt;</c>). Members pulled up into such an
/// interface name it where the type's members name the type, so that every implementer can
/// implement them (<c>TSelf Add(TSelf other)</c>, <c>static abstract TSelf Zero { get; }</c>).
/// </summary>
internal static class SelfType
{
    /// <summary>The self type parameter of the interface <paramref name="view"/> for <paramref name="type"/>, or null when it has none.</summary>
    public static ITypeParameterSymbol? Find(INamedTypeSymbol type, INamedTypeSymbol view) =>
        PassedAs(type, view).FirstOrDefault(p => p.ConstraintTypes.Any(c => SymbolEqualityComparer.Default.Equals(c, p.ContainingType)));

    /// <summary>The interface's type parameters <paramref name="type"/> passes itself as, constrained or not.</summary>
    public static IEnumerable<ITypeParameterSymbol> PassedAs(INamedTypeSymbol type, INamedTypeSymbol view) =>
        view.OriginalDefinition.TypeParameters.Zip(view.TypeArguments)
            .Where(pair => SymbolEqualityComparer.Default.Equals(pair.Second, type))
            .Select(pair => pair.First);

    /// <summary>
    /// <paramref name="symbol"/> with <paramref name="type"/> replaced by <paramref name="self"/>,
    /// also as a type argument or array element; null where it is <c>Nullable&lt;type&gt;</c>,
    /// which a type parameter can't stand for. Other types are returned as they are.
    /// </summary>
    public static ITypeSymbol? Replace(ITypeSymbol symbol, INamedTypeSymbol type, ITypeParameterSymbol self, Compilation compilation)
    {
        if (SymbolEqualityComparer.Default.Equals(symbol, type))
            return self.WithNullableAnnotation(symbol.NullableAnnotation);

        switch (symbol)
        {
            case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                when SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], type):
                return null;
            case IArrayTypeSymbol array:
                return Replace(array.ElementType, type, self, compilation) is { } element
                    ? compilation.CreateArrayTypeSymbol(element, array.Rank, element.NullableAnnotation).WithNullableAnnotation(array.NullableAnnotation)
                    : null;
            case INamedTypeSymbol { IsGenericType: true } generic when generic.TypeArguments.Any(a => Mentions(a, type)):
                var arguments = generic.TypeArguments.Select(a => Replace(a, type, self, compilation)).ToList();
                return arguments.Any(a => a == null)
                    ? null
                    : generic.ConstructedFrom.Construct(arguments.ToImmutableArray()!, arguments.Select(a => a!.NullableAnnotation).ToImmutableArray())
                        .WithNullableAnnotation(generic.NullableAnnotation);
            default:
                return symbol;
        }
    }

    /// <summary>Whether a type is <paramref name="type"/> or names it in its type arguments or element type.</summary>
    public static bool Mentions(ITypeSymbol symbol, INamedTypeSymbol type) => symbol switch
    {
        _ when SymbolEqualityComparer.Default.Equals(symbol, type) => true,
        IArrayTypeSymbol array => Mentions(array.ElementType, type),
        IPointerTypeSymbol pointer => Mentions(pointer.PointedAtType, type),
        INamedTypeSymbol named => named.TypeArguments.Any(a => Mentions(a, type)) || (named.ContainingType != null && Mentions(named.ContainingType, type)),
        _ => false
    };

    /// <summary>
    /// Refuses a member whose signature uses <c>Nullable&lt;type&gt;</c> (a struct's <c>Money?</c>):
    /// the interface would have to write <c>TSelf?</c>, which for a type parameter is not
    /// <c>Nullable&lt;TSelf&gt;</c>, so the type would no longer implement the member.
    /// </summary>
    public static void CheckExpressible(ISymbol member, INamedTypeSymbol type, INamedTypeSymbol target, ITypeParameterSymbol self, Compilation compilation)
    {
        if (SignatureTypes(member).FirstOrDefault(t => Replace(t, type, self, compilation) == null) is { } nullable)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotMoveable,
                $"'{member.ToDisplayString()}' uses '{nullable.ToDisplayString()}', which '{target.ToDisplayString()}' would have to write '{self.Name}?'; " +
                $"for a type parameter that is not Nullable<{self.Name}>, so '{type.Name}' would not implement the member.");
        }
    }

    /// <summary>The types a member's signature names: return or member type, parameters, constraints.</summary>
    private static IEnumerable<ITypeSymbol> SignatureTypes(ISymbol member) => member switch
    {
        IMethodSymbol method => method.Parameters.Select(p => p.Type)
            .Prepend(method.ReturnType)
            .Concat(method.TypeParameters.SelectMany(p => p.ConstraintTypes)),
        IPropertySymbol property => property.Parameters.Select(p => p.Type).Prepend(property.Type),
        IEventSymbol @event => [@event.Type],
        _ => []
    };
}
