using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Adds plain <c>using</c> directives to a file without touching the ones it has: code moved or
/// copied into a file brings the namespaces it needs along.
/// </summary>
internal static class UsingDirectives
{
    /// <summary>
    /// <paramref name="root"/> with a <c>using</c> for each of <paramref name="namespaces"/> the
    /// file does not import yet with a plain using. A new directive goes among the plain usings of
    /// the file (or of its first namespace declaration when only that one has usings) in
    /// alphabetical order, <c>System</c> first; in a file without usings, at the top, after its
    /// header comments, followed by a blank line. Each new line ends in <paramref name="lineEnding"/>.
    /// </summary>
    public static CompilationUnitSyntax Add(CompilationUnitSyntax root, IEnumerable<string> namespaces, string lineEnding)
    {
        var existing = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Where(IsPlain)
            .Select(u => u.Name!.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var added = namespaces.Distinct(StringComparer.Ordinal).Where(n => !existing.Contains(n)).Order(Comparer).ToList();
        if (added.Count == 0)
            return root;

        if (root.Usings.Count == 0 &&
            root.Members.FirstOrDefault() is BaseNamespaceDeclarationSyntax { Usings.Count: > 0 } ns)
        {
            return root.ReplaceNode(ns, ns.WithUsings(Insert(ns.Usings, added, lineEnding)));
        }

        if (root.Usings.Count > 0)
            return root.WithUsings(Insert(root.Usings, added, lineEnding));

        // A file without usings: the new block takes the header comments of the first line and is
        // followed by a blank line.
        var first = root.GetFirstToken(includeZeroWidth: true);
        var directives = added.Select(n => Directive(n, lineEnding)).ToList();
        directives[0] = directives[0].WithLeadingTrivia(first.LeadingTrivia);
        var newRoot = root.ReplaceToken(first, first.WithLeadingTrivia(SyntaxFactory.EndOfLine(lineEnding)));
        return newRoot.WithUsings(SyntaxFactory.List(directives));
    }

    private static SyntaxList<UsingDirectiveSyntax> Insert(
        SyntaxList<UsingDirectiveSyntax> usings,
        IReadOnlyList<string> namespaces,
        string lineEnding)
    {
        var result = usings;
        foreach (var name in namespaces)
        {
            var directive = Directive(name, lineEnding);
            var index = result.IndexOf(u => IsPlain(u) && Comparer.Compare(name, u.Name!.ToString()) < 0);
            if (index < 0)
            {
                var lastPlain = result.LastIndexOf(IsPlain);
                index = lastPlain < 0 ? result.Count : lastPlain + 1;
            }

            if (index == 0)
            {
                // The first directive keeps the file's header comments.
                var first = result[0];
                directive = directive.WithLeadingTrivia(first.GetLeadingTrivia());
                result = result.Replace(first, first.WithLeadingTrivia());
            }

            result = result.Insert(index, directive);
        }

        return result;
    }

    private static UsingDirectiveSyntax Directive(string name, string lineEnding) =>
        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(name).WithLeadingTrivia(SyntaxFactory.Space))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(lineEnding));

    private static bool IsPlain(UsingDirectiveSyntax directive) =>
        directive.GlobalKeyword.IsKind(SyntaxKind.None) &&
        directive.StaticKeyword.IsKind(SyntaxKind.None) &&
        directive.Alias == null &&
        directive.Name != null;

    /// <summary><c>System</c> and its sub-namespaces first, then ordinal.</summary>
    private static readonly Comparer<string> Comparer = Comparer<string>.Create((left, right) =>
    {
        var leftSystem = left == "System" || left.StartsWith("System.", StringComparison.Ordinal);
        var rightSystem = right == "System" || right.StartsWith("System.", StringComparison.Ordinal);
        return leftSystem != rightSystem ? (leftSystem ? -1 : 1) : string.CompareOrdinal(left, right);
    });
}
