using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Refactoring.Delete;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Cleans up using directives that a change of namespaces broke: a directive that named
/// something in the old namespace (the namespace itself, a namespace below it or a type in it)
/// and bound before the change, but no longer binds after it. Directives that still bind, or
/// that did not bind before either, are left as they are, and so are generated files (the
/// build writes them anew).
/// </summary>
internal static class StaleUsingDirectives
{
    /// <summary>What <see cref="FixAsync"/> changed.</summary>
    /// <param name="Solution">The solution with the directives removed or repointed.</param>
    /// <param name="Removed">How many directives were removed.</param>
    /// <param name="Repointed">How many directives were pointed at their new target.</param>
    public sealed record Result(Solution Solution, int Removed, int Repointed);

    /// <summary>
    /// In every document of <paramref name="after"/>: a namespace import (<c>using X;</c>, also
    /// <c>global using X;</c>) of something in <paramref name="oldNamespace"/> that bound in
    /// <paramref name="before"/> but no longer does is removed (with the comment lines above it);
    /// a <c>using static</c> or an alias is pointed at the name <paramref name="retarget"/> gives
    /// for what it bound to before, and left as it is when that gives none (removing it would only
    /// break the code that uses it).
    /// </summary>
    /// <param name="before">The solution before the change.</param>
    /// <param name="after">The solution after it.</param>
    /// <param name="oldNamespace">The namespace whose contents moved or went away.</param>
    /// <param name="retarget">
    /// For the namespace or type a static using or alias bound to before, its name now (fully
    /// qualified), or null to leave the directive alone.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<Result> FixAsync(
        Solution before,
        Solution after,
        string oldNamespace,
        Func<ISymbol, string?> retarget,
        CancellationToken cancellationToken)
    {
        // A cheap filter first: only directives whose text mentions the old namespace's last
        // segment can name something in it (also when written relative to an enclosing namespace).
        var lastSegment = oldNamespace[(oldNamespace.LastIndexOf('.') + 1)..];
        var removed = 0;
        var repointed = 0;
        var solution = after;

        foreach (var documentId in after.Projects.SelectMany(p => p.DocumentIds))
        {
            var document = solution.GetDocument(documentId);
            var original = before.GetDocument(documentId);
            if (document == null || original == null ||
                await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root ||
                GeneratedCode.IsGenerated(root.SyntaxTree, cancellationToken))
            {
                continue;
            }

            var candidates = Directives(root)
                .Where(d => d.NamespaceOrType.ToString().Contains(lastSegment, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 0)
                continue;

            var model = await document.GetSemanticModelAsync(cancellationToken);
            var unbound = candidates.Where(d => model?.GetSymbolInfo(d.NamespaceOrType, cancellationToken).Symbol == null).ToList();
            if (unbound.Count == 0)
                continue;

            if (await original.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax originalRoot)
                continue;
            var originalModel = await original.GetSemanticModelAsync(cancellationToken);
            var originalDirectives = Directives(originalRoot).ToList();

            var directives = Directives(root).ToList();
            var toRemove = new List<UsingDirectiveSyntax>();
            var changes = new List<TextChange>();
            foreach (var directive in unbound)
            {
                var boundBefore = BoundBefore(directive, directives, originalDirectives, originalModel, cancellationToken);
                if (boundBefore == null || !IsIn(boundBefore, oldNamespace))
                    continue;

                if (directive.Alias == null && directive.StaticKeyword.IsKind(SyntaxKind.None))
                {
                    toRemove.Add(directive);
                }
                else if (retarget(boundBefore) is { } target)
                {
                    changes.Add(new TextChange(directive.NamespaceOrType.Span, target));
                    repointed++;
                }
            }

            if (toRemove.Count == 0 && changes.Count == 0)
                continue;

            var text = await document.GetTextAsync(cancellationToken);
            changes.AddRange(DeclarationRemoval.Spans(text, toRemove).Select(span => new TextChange(span, "")));
            removed += toRemove.Count;
            solution = solution.WithDocumentText(documentId, text.WithChanges(changes));
        }

        return new Result(solution, removed, repointed);
    }

    /// <summary>The using directives of a file, at file level and inside namespace declarations.</summary>
    private static IEnumerable<UsingDirectiveSyntax> Directives(CompilationUnitSyntax root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>();

    /// <summary>
    /// What <paramref name="directive"/> bound to before the change: the directive written the
    /// same way, the same number of times before it, in the file as it was. Null when it did not
    /// bind (an error before already) or is not found.
    /// </summary>
    private static ISymbol? BoundBefore(
        UsingDirectiveSyntax directive,
        List<UsingDirectiveSyntax> directives,
        List<UsingDirectiveSyntax> originalDirectives,
        SemanticModel? originalModel,
        CancellationToken cancellationToken)
    {
        if (originalModel == null)
            return null;

        var written = directive.WithoutTrivia().ToString();
        var occurrence = directives.TakeWhile(d => d != directive).Count(d => d.WithoutTrivia().ToString() == written);
        var original = originalDirectives.Where(d => d.WithoutTrivia().ToString() == written).Skip(occurrence).FirstOrDefault();
        return original == null ? null : originalModel.GetSymbolInfo(original.NamespaceOrType, cancellationToken).Symbol;
    }

    /// <summary>Whether <paramref name="symbol"/> is <paramref name="ns"/>, a namespace below it or a type in one of them.</summary>
    private static bool IsIn(ISymbol symbol, string ns)
    {
        var namespaceSymbol = symbol as INamespaceSymbol ?? symbol.ContainingNamespace;
        var name = namespaceSymbol?.ToDisplayString();
        return name != null && (name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal));
    }
}
