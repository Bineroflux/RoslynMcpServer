using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Resolution;

/// <summary>
/// Analyzes type members for extraction, implementation, and override operations.
/// </summary>
public static class MemberAnalyzer
{
    /// <summary>
    /// Gets members that can be extracted to an interface.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Public instance members suitable for interface extraction.</returns>
    public static IEnumerable<ISymbol> GetExtractableMembers(INamedTypeSymbol type)
    {
        return type.GetMembers()
            .Where(m => !m.IsStatic &&
                        !m.IsImplicitlyDeclared &&
                        m.DeclaredAccessibility == Accessibility.Public &&
                        IsExtractableKind(m));
    }

    /// <summary>
    /// Gets members from an interface that are not implemented by a type.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <param name="iface">The interface to check against.</param>
    /// <returns>Unimplemented members of the interface and of the interfaces it extends.</returns>
    /// <remarks>
    /// Property, indexer and event accessors are not returned on their own: they are
    /// implemented as part of their property, indexer or event (as standalone methods they
    /// would be emitted as e.g. a bogus <c>get_Item</c> method). Members that need no
    /// implementation (non-virtual static or sealed members, fields, nested types) are
    /// skipped too.
    /// </remarks>
    public static IEnumerable<ISymbol> GetUnimplementedMembers(INamedTypeSymbol type, INamedTypeSymbol iface)
    {
        foreach (var member in iface.AllInterfaces.Prepend(iface).SelectMany(i => i.GetMembers()))
        {
            if (member.IsImplicitlyDeclared) continue;
            if (member is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol)) continue;
            if (!member.IsAbstract && !member.IsVirtual) continue;

            var implementation = type.FindImplementationForInterfaceMember(member);
            if (implementation == null)
            {
                yield return member;
            }
        }
    }

    /// <summary>
    /// Gets virtual/abstract members from base classes (up to and including
    /// <see cref="object"/>) that can be overridden.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Overridable members from base classes, each signature at most once.</returns>
    /// <remarks>
    /// The base chain is walked from the nearest base outwards and only the most derived
    /// declaration of a signature counts: once the type itself or a nearer base declares,
    /// overrides or hides a member, the same member further up must not be offered again,
    /// or it would be emitted twice (CS0111). Members the type cannot access (internal
    /// members of another assembly) cannot be overridden from it and are skipped.
    /// </remarks>
    public static IEnumerable<ISymbol> GetOverridableMembers(INamedTypeSymbol type)
    {
        // The type's own members (overrides or not) already occupy their signatures.
        var seen = new HashSet<string>(
            type.GetMembers()
                .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol)
                .Select(m => GetMemberSignature(m)));

        for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            foreach (var member in baseType.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                if (member is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol)) continue;
                if (!seen.Add(GetMemberSignature(member))) continue;

                if (IsOverridable(member) && SyntaxGenerationHelper.IsOverridableFrom(member, type))
                {
                    yield return member;
                }
            }
        }
    }

    /// <summary>
    /// Determines if an expression is safe to inline (no side effects).
    /// </summary>
    /// <param name="expr">The expression to analyze.</param>
    /// <param name="model">The semantic model.</param>
    /// <returns>True if the expression can be safely inlined.</returns>
    public static bool IsSafeToInline(ExpressionSyntax expr, SemanticModel model)
    {
        // Check for method invocations (potential side effects)
        if (expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any())
        {
            return false;
        }

        // Check for increment/decrement
        if (expr.DescendantNodesAndSelf().OfType<PostfixUnaryExpressionSyntax>().Any() ||
            expr.DescendantNodesAndSelf().OfType<PrefixUnaryExpressionSyntax>().Any(p =>
                p.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression) ||
                p.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression)))
        {
            return false;
        }

        // Check for assignments within expression
        if (expr.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Any())
        {
            return false;
        }

        // Check for object creation (potential side effects in constructor)
        if (expr.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().Any())
        {
            return false;
        }

        // Check for await expressions
        if (expr.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any())
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets members that can be moved to a base class.
    /// </summary>
    /// <param name="type">The type to analyze.</param>
    /// <returns>Members suitable for extraction to base class.</returns>
    public static IEnumerable<ISymbol> GetMembersForBaseClass(INamedTypeSymbol type)
    {
        return type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared &&
                        !m.IsStatic &&
                        CanMoveToBase(m));
    }

    private static bool IsExtractableKind(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => !method.IsConstructor() &&
                                    method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol => true,
            IEventSymbol => true,
            _ => false
        };
    }

    private static bool IsOverridable(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => (method.IsVirtual || method.IsAbstract || method.IsOverride) &&
                                    !method.IsSealed &&
                                    method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol prop => (prop.IsVirtual || prop.IsAbstract || prop.IsOverride) &&
                                    !prop.IsSealed,
            _ => false
        };
    }

    private static bool CanMoveToBase(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary &&
                                    method.DeclaredAccessibility != Accessibility.Private,
            IPropertySymbol prop => prop.DeclaredAccessibility != Accessibility.Private,
            IFieldSymbol field => field.DeclaredAccessibility != Accessibility.Private,
            _ => false
        };
    }

    private static string GetMemberSignature(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => $"{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))})",
            IPropertySymbol { IsIndexer: true } indexer => $"this[{string.Join(",", indexer.Parameters.Select(p => p.Type.ToDisplayString()))}]",
            IPropertySymbol prop => prop.Name,
            _ => member.Name
        };
    }
}

/// <summary>
/// Extension methods for IMethodSymbol.
/// </summary>
public static class MethodSymbolExtensions
{
    /// <summary>
    /// Determines if the method is a constructor.
    /// </summary>
    public static bool IsConstructor(this IMethodSymbol method)
    {
        return method.MethodKind == MethodKind.Constructor ||
               method.MethodKind == MethodKind.StaticConstructor;
    }
}
