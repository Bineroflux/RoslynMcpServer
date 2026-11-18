using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Delete;

/// <summary>
/// The syntax a type or member occupies: what deleting it removes, and the region in which a
/// reference to it is a reference to itself (a recursive call, a type naming itself).
/// </summary>
internal static class SymbolDeclarations
{
    /// <summary>
    /// The nodes declaring <paramref name="symbol"/>: every part of a partial type, method or
    /// property; for a field or event field, its whole declaration when it declares only that
    /// variable, else just the variable.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> Nodes(ISymbol symbol, CancellationToken cancellationToken)
    {
        IEnumerable<SyntaxReference> references = symbol switch
        {
            IMethodSymbol { PartialImplementationPart: { } implementation } =>
                symbol.DeclaringSyntaxReferences.Concat(implementation.DeclaringSyntaxReferences),
            IMethodSymbol { PartialDefinitionPart: { } definition } =>
                definition.DeclaringSyntaxReferences.Concat(symbol.DeclaringSyntaxReferences),
            IPropertySymbol { PartialImplementationPart: { } implementation } =>
                symbol.DeclaringSyntaxReferences.Concat(implementation.DeclaringSyntaxReferences),
            IPropertySymbol { PartialDefinitionPart: { } definition } =>
                definition.DeclaringSyntaxReferences.Concat(symbol.DeclaringSyntaxReferences),
            _ => symbol.DeclaringSyntaxReferences
        };

        return references
            .DistinctBy(r => (r.SyntaxTree, r.Span))
            .Select(r => Widen(r.GetSyntax(cancellationToken)))
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="location"/> lies inside one of <paramref name="nodes"/>, counting
    /// their attributes, documentation comments and other leading trivia.
    /// </summary>
    public static bool Contains(IReadOnlyList<SyntaxNode> nodes, Location location)
    {
        if (location.SourceTree is not { } tree)
            return false;

        foreach (var node in nodes)
        {
            if ((node.SyntaxTree == tree || SameFile(node.SyntaxTree, tree)) && node.FullSpan.Contains(location.SourceSpan))
                return true;
        }

        return false;
    }

    private static bool SameFile(SyntaxTree left, SyntaxTree right) =>
        !string.IsNullOrEmpty(left.FilePath) &&
        string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);

    private static SyntaxNode Widen(SyntaxNode node) =>
        node is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: BaseFieldDeclarationSyntax field } }
            ? field
            : node;
}
