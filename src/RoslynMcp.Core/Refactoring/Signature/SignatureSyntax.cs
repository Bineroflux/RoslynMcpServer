using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
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
    /// The declaration <see cref="SelectDeclaration"/> picks in <paramref name="first"/>, or,
    /// when it has none, in the first other document of its file (another target framework,
    /// for a member declared in an <c>#if</c> branch only that one compiles), with its document.
    /// </summary>
    /// <param name="first">The document the request names.</param>
    /// <param name="candidates">The declarations with the requested name in a document's syntax root.</param>
    /// <param name="kind">As for <see cref="SelectDeclaration"/>.</param>
    /// <param name="name">As for <see cref="SelectDeclaration"/>.</param>
    /// <param name="line">As for <see cref="SelectDeclaration"/>.</param>
    /// <param name="identifier">As for <see cref="SelectDeclaration"/>.</param>
    /// <param name="notFoundCode">As for <see cref="SelectDeclaration"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<(Document Document, TNode Declaration)> SelectDeclarationAsync<TNode>(
        Document first,
        Func<SyntaxNode, IEnumerable<TNode>> candidates,
        string kind,
        string name,
        int? line,
        Func<TNode, SyntaxToken> identifier,
        string notFoundCode,
        CancellationToken cancellationToken)
        where TNode : SyntaxNode
    {
        var solution = first.Project.Solution;
        var documents = first.FilePath == null
            ? [first]
            : solution.GetDocumentIdsWithFilePath(first.FilePath).Where(id => id != first.Id).Select(solution.GetDocument).OfType<Document>().Prepend(first);

        RefactoringException? notFound = null;
        foreach (var document in documents)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
                continue;

            try
            {
                return (document, SelectDeclaration(candidates(root).ToList(), kind, name, line, identifier, notFoundCode));
            }
            catch (RefactoringException ex) when (ex.ErrorCode == notFoundCode)
            {
                notFound ??= ex;
            }
        }

        throw notFound ?? new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
    }

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
/// Places in source files, each counted once although a file that several projects compile
/// (one per target framework) has a document, and so a reference or declaration, in each.
/// </summary>
internal sealed class SourcePlaces
{
    private readonly Dictionary<string, (DocumentId First, HashSet<TextSpan> Spans)> _files = new(PathResolver.PathComparer);

    /// <summary>Adds a place; false when it was added before, through any document of its file.</summary>
    public bool Add(Document document, TextSpan span) => File(document).Spans.Add(span);

    /// <summary>The first document of <paramref name="document"/>'s file this was given.</summary>
    public DocumentId FirstDocument(Document document) => File(document).First;

    private (DocumentId First, HashSet<TextSpan> Spans) File(Document document)
    {
        var key = document.FilePath is { } path ? PathResolver.NormalizePath(path) : document.Id.Id.ToString();
        if (!_files.TryGetValue(key, out var file))
            _files[key] = file = (document.Id, []);
        return file;
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

    /// <summary>The documents with edits.</summary>
    public IReadOnlyCollection<DocumentId> Documents => _edits.Keys;

    /// <summary>
    /// Applies the edits to <paramref name="solution"/>, then simplifies the type names marked
    /// for it; line endings and the rest of each file stay as they were.
    /// </summary>
    /// <param name="solution">The solution the edited nodes belong to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="finish">A last change to each edited document's root before it is simplified and formatted.</param>
    /// <param name="warnings">
    /// Receives what laying out the edits reports: string literals of code moved from another
    /// file that keep line breaks other than the file's (see <see cref="GeneratedCodeFormatter.FormatAsync"/>).
    /// </param>
    public async Task<Solution> ApplyAsync(
        Solution solution,
        CancellationToken cancellationToken,
        Func<DocumentId, SyntaxNode, SyntaxNode>? finish = null,
        ICollection<string>? warnings = null)
    {
        var result = solution;
        foreach (var (id, edits) in _edits)
        {
            var root = await solution.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
            var newRoot = root.ReplaceNodes(edits.Keys, (original, rewritten) => edits[original](rewritten));
            result = result.WithDocumentSyntaxRoot(id, finish == null ? newRoot : finish(id, newRoot));
        }

        foreach (var id in _edits.Keys)
            result = await GeneratedCodeFormatter.FormatAsync(result, solution.GetDocument(id)!, cancellationToken, warnings);

        return result;
    }
}
