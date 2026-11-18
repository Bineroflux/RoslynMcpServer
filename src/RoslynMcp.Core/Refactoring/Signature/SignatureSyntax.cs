using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Shared steps of the signature operations: picking the declaration a request names, binding
/// a type the request writes, writing it into each family member, and applying the edits.
/// </summary>
internal static class SignatureSyntax
{
    /// <summary>
    /// The one declaration among <paramref name="candidates"/>; with several, the one whose
    /// header (from its first attribute or modifier to its name) includes <paramref name="line"/>.
    /// </summary>
    /// <param name="candidates">The declarations with the requested name.</param>
    /// <param name="kind">What they are, lower case: "method", "member".</param>
    /// <param name="name">The requested name.</param>
    /// <param name="line">The 1-based line that picks one of several.</param>
    /// <param name="identifier">A declaration's name token.</param>
    /// <param name="notFoundCode">Error code when nothing matches.</param>
    public static TNode SelectDeclaration<TNode>(
        IReadOnlyList<TNode> candidates,
        string kind,
        string name,
        int? line,
        Func<TNode, SyntaxToken> identifier,
        string notFoundCode)
        where TNode : SyntaxNode
    {
        var what = $"{char.ToUpperInvariant(kind[0])}{kind[1..]} '{name}'";
        if (candidates.Count == 0)
            throw new RefactoringException(notFoundCode, $"{what} not found.");

        if (candidates.Count == 1)
            return candidates[0];

        if (!line.HasValue)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple {kind}s named '{name}' found. Provide line number. Options: {string.Join(", ", candidates.Select(StartLine))}");
        }

        return candidates.FirstOrDefault(c =>
                StartLine(c) <= line.Value && line.Value <= identifier(c).GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            ?? throw new RefactoringException(notFoundCode, $"{what} not found at line {line}.");
    }

    private static int StartLine(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>
    /// The type <paramref name="text"/> names at <paramref name="position"/>, or null when it
    /// does not parse as a type or does not resolve to one there.
    /// </summary>
    public static ITypeSymbol? BindType(SemanticModel model, int position, string text)
    {
        // The parser flags void outside a return type.
        if (text.Trim() == "void")
            return model.Compilation.GetSpecialType(SpecialType.System_Void);

        var syntax = SyntaxFactory.ParseTypeName(text);
        if (syntax.ContainsDiagnostics || syntax is RefTypeSyntax or ScopedTypeSyntax)
            return null;

        var type = model.GetSpeculativeTypeInfo(position, syntax, SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
        if (type == null || ContainsError(type))
            return null;

        // Speculative binding keeps the annotations inside the type (List<string?>) but not
        // the outermost one: string? comes back as string.
        return syntax is NullableTypeSyntax && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
            ? type.WithNullableAnnotation(NullableAnnotation.Annotated)
            : type;
    }

    private static bool ContainsError(ITypeSymbol type) => type switch
    {
        { TypeKind: TypeKind.Error } => true,
        IArrayTypeSymbol array => ContainsError(array.ElementType),
        IPointerTypeSymbol pointer => ContainsError(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsError),
        _ => false
    };

    /// <summary>
    /// <paramref name="type"/> (written in the starting member's context) as it is written in
    /// <paramref name="member"/>'s declaration at <paramref name="position"/>: with nullable
    /// annotations only where they are enabled there, and marked for the simplifier.
    /// </summary>
    public static TypeSyntax WriteType(ITypeSymbol type, FamilyMember member, SemanticModel model, int position)
    {
        var translated = member.Translate(type) ?? throw new RefactoringException(
            ErrorCodes.TypeNotExpressible,
            $"'{type.ToDisplayString()}' cannot be written in '{member.Symbol.ToDisplayString()}': it uses a type " +
            "parameter that has no counterpart there. Start from the member whose type parameters it uses.");

        return SyntaxGenerationHelper.CreateTypeSyntax(translated, model.GetNullableContext(position).AnnotationsEnabled());
    }
}

/// <summary>
/// Node edits collected per document against the original syntax trees and applied in one
/// pass, so edits never see each other's shifted positions. A nested edit sees its
/// descendants already edited.
/// </summary>
internal sealed class SyntaxEdits
{
    private readonly Dictionary<DocumentId, Dictionary<SyntaxNode, Func<SyntaxNode, SyntaxNode>>> _edits = [];

    /// <summary>Adds an edit of <paramref name="node"/>; false when the node already has one.</summary>
    public bool Add(DocumentId document, SyntaxNode node, Func<SyntaxNode, SyntaxNode> edit)
    {
        if (!_edits.TryGetValue(document, out var edits))
            _edits[document] = edits = new Dictionary<SyntaxNode, Func<SyntaxNode, SyntaxNode>>();
        return edits.TryAdd(node, edit);
    }

    /// <summary>
    /// Applies the edits to <paramref name="solution"/>, then simplifies the type names marked
    /// for it; line endings and the rest of each file stay as they were.
    /// </summary>
    public async Task<Solution> ApplyAsync(Solution solution, CancellationToken cancellationToken)
    {
        var result = solution;
        foreach (var (id, edits) in _edits)
        {
            var root = await solution.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            result = result.WithDocumentSyntaxRoot(id, root.ReplaceNodes(edits.Keys, (original, rewritten) => edits[original](rewritten)));
        }

        foreach (var id in _edits.Keys)
            result = await GeneratedCodeFormatter.FormatAsync(result, solution.GetDocument(id)!, cancellationToken);

        return result;
    }
}
