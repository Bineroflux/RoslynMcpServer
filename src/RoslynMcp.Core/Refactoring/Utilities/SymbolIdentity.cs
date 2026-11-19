using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Compares symbols that may come from different compilations. Each project of a solution has
/// its own compilation, and one project can see another's declarations through a compilation
/// reference or an emitted skeleton (metadata), so the same declaration can be several symbol
/// instances that <see cref="SymbolEqualityComparer"/> tells apart: a member found through
/// another project, the same type in each target framework of a multi-targeted project.
/// Symbols from one semantic model or one compilation compare with
/// <see cref="SymbolEqualityComparer"/>, as before.
/// </summary>
internal static class SymbolIdentity
{
    /// <summary>
    /// Compares declarations (<see cref="SameDeclaration"/>): a generic type or member equals
    /// its definition and every construction of it.
    /// </summary>
    public static IEqualityComparer<ISymbol> Declarations { get; } = new DeclarationComparer();

    /// <summary>
    /// Whether two symbols are the same declaration: their definitions are the same symbol, or
    /// have the same documentation ID in the same assembly (assemblies themselves by name,
    /// namespaces by their full name).
    /// <c>List&lt;int&gt;</c> and <c>List&lt;string&gt;</c> are the same declaration.
    /// </summary>
    public static bool SameDeclaration(ISymbol? left, ISymbol? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        var (leftDefinition, rightDefinition) = (left.OriginalDefinition, right.OriginalDefinition);
        if (SymbolEqualityComparer.Default.Equals(leftDefinition, rightDefinition))
            return true;

        if (leftDefinition is IAssemblySymbol leftAssembly && rightDefinition is IAssemblySymbol rightAssembly)
            return leftAssembly.Identity.Name == rightAssembly.Identity.Name;

        // A namespace spans assemblies.
        if (leftDefinition is INamespaceSymbol && rightDefinition is INamespaceSymbol)
            return leftDefinition.ToDisplayString() == rightDefinition.ToDisplayString();

        return leftDefinition.Kind == rightDefinition.Kind &&
            leftDefinition.GetDocumentationCommentId() is { } id &&
            id == rightDefinition.GetDocumentationCommentId() &&
            leftDefinition.ContainingAssembly?.Name == rightDefinition.ContainingAssembly?.Name;
    }

    /// <summary>
    /// Whether two types are the same type, constructions included (<c>List&lt;int&gt;</c> is
    /// not <c>List&lt;string&gt;</c>), also from different compilations: equal symbols, or the
    /// same reference ID in the same assembly.
    /// </summary>
    public static bool SameType(ITypeSymbol? left, ITypeSymbol? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return SymbolEqualityComparer.Default.Equals(left, right) ||
            (left is not ITypeParameterSymbol &&
             DocumentationCommentId.CreateReferenceId(left) == DocumentationCommentId.CreateReferenceId(right) &&
             left.ContainingAssembly?.Name == right.ContainingAssembly?.Name);
    }

    /// <summary>
    /// A string that identifies a declaration across compilations, as
    /// <see cref="SameDeclaration"/> compares them: its documentation ID (or display string
    /// when it has none) and its assembly's name.
    /// </summary>
    public static string Key(ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        return $"{definition.GetDocumentationCommentId() ?? definition.ToDisplayString()}|{definition.ContainingAssembly?.Name}";
    }

    private sealed class DeclarationComparer : IEqualityComparer<ISymbol>
    {
        public bool Equals(ISymbol? left, ISymbol? right) => SameDeclaration(left, right);

        public int GetHashCode(ISymbol symbol) => symbol.OriginalDefinition switch
        {
            IAssemblySymbol assembly => StringComparer.Ordinal.GetHashCode(assembly.Identity.Name),
            INamespaceSymbol ns => StringComparer.Ordinal.GetHashCode(ns.ToDisplayString()),
            var definition when definition.GetDocumentationCommentId() is { } id => StringComparer.Ordinal.GetHashCode(id),
            var definition => SymbolEqualityComparer.Default.GetHashCode(definition)
        };
    }
}
