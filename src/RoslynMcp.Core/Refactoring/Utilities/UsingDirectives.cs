using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Adds <c>using</c> directives to a file without touching the ones it has: code moved or copied
/// into a file brings the namespaces (and static imports and aliases) it needs along.
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
    public static CompilationUnitSyntax Add(CompilationUnitSyntax root, IEnumerable<string> namespaces, string lineEnding) =>
        Add(root, namespaces.Select(n => new NewUsing(n)), lineEnding);

    /// <summary>
    /// <paramref name="root"/> with each of <paramref name="usings"/> the file does not have yet: a
    /// plain using of a namespace it doesn't import with one, a <c>using static</c> of a type it
    /// doesn't import that way, an alias whose name it doesn't declare. They go where
    /// <see cref="Add(CompilationUnitSyntax, IEnumerable{string}, string)"/> puts plain ones, in
    /// the groups plain, static, alias, each in alphabetical order (<c>System</c> first; aliases by
    /// name). Each new line ends in <paramref name="lineEnding"/>.
    /// </summary>
    public static CompilationUnitSyntax Add(CompilationUnitSyntax root, IEnumerable<NewUsing> usings, string lineEnding)
    {
        var existing = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None))
            .Select(u => (Group(u), Key(u)))
            .ToHashSet();

        var added = usings.Distinct()
            .Where(u => !existing.Contains((u.Group, u.Key)))
            .OrderBy(u => u.Group)
            .ThenBy(u => u.Group == AliasGroup ? "" : u.Key, Comparer)
            .ThenBy(u => u.Key, StringComparer.Ordinal)
            .ToList();
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
        var directives = added.Select(u => Directive(u, lineEnding)).ToList();
        directives[0] = directives[0].WithLeadingTrivia(first.LeadingTrivia);
        var newRoot = root.ReplaceToken(first, first.WithLeadingTrivia(SyntaxFactory.EndOfLine(lineEnding)));
        return newRoot.WithUsings(SyntaxFactory.List(directives));
    }

    /// <summary>
    /// <paramref name="declaration"/> with a <c>using</c> inside it for each of
    /// <paramref name="namespaces"/> it does not import yet with a plain using: among its usings in
    /// alphabetical order, <c>System</c> first, or before its first member, followed by a blank
    /// line. The new directives are indented like the usings or members next to them, and each
    /// new line ends in <paramref name="lineEnding"/>.
    /// </summary>
    public static BaseNamespaceDeclarationSyntax AddInside(
        BaseNamespaceDeclarationSyntax declaration, IEnumerable<string> namespaces, string lineEnding)
    {
        var existing = declaration.Usings.Where(IsPlain).Select(u => u.Name!.ToString()).ToHashSet(StringComparer.Ordinal);
        var added = namespaces.Distinct(StringComparer.Ordinal).Where(n => !existing.Contains(n)).Order(Comparer).Select(n => new NewUsing(n)).ToList();
        if (added.Count == 0)
            return declaration;

        if (declaration.Usings.Count > 0)
            return declaration.WithUsings(Insert(declaration.Usings, added, lineEnding, Indentation(declaration.Usings[0].GetLeadingTrivia())));

        if (declaration.Members.FirstOrDefault() is not { } first)
            return declaration;

        // The blank lines and indentation before the first member go before the new directives;
        // its comments stay with it, after a blank line.
        var leading = first.GetLeadingTrivia();
        var layout = leading.TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia)).ToList();
        var indentation = Indentation(SyntaxFactory.TriviaList(layout));
        var directives = added.Select(n => Directive(n, lineEnding).WithLeadingTrivia(indentation)).ToList();
        directives[0] = directives[0].WithLeadingTrivia(layout);
        var moved = first.WithLeadingTrivia(
            SyntaxFactory.TriviaList(SyntaxFactory.EndOfLine(lineEnding)).AddRange(indentation).AddRange(leading.Skip(layout.Count)));
        return declaration.ReplaceNode(first, moved).WithUsings(SyntaxFactory.List(directives));
    }

    /// <summary>The indentation a line's leading trivia ends with.</summary>
    private static SyntaxTriviaList Indentation(SyntaxTriviaList leading) =>
        leading.Count > 0 && leading[^1].IsKind(SyntaxKind.WhitespaceTrivia) ? SyntaxFactory.TriviaList(leading[^1]) : default;

    /// <summary>
    /// <paramref name="usings"/> with <paramref name="added"/> (sorted) inserted: each before the
    /// first directive of its group (plain, static, alias) that sorts after it, else after the
    /// last of its group, else after the last global using or directive of a group before its own.
    /// </summary>
    private static SyntaxList<UsingDirectiveSyntax> Insert(
        SyntaxList<UsingDirectiveSyntax> usings,
        IReadOnlyList<NewUsing> added,
        string lineEnding,
        SyntaxTriviaList indentation = default)
    {
        var result = usings;
        foreach (var newUsing in added)
        {
            var directive = Directive(newUsing, lineEnding).WithLeadingTrivia(indentation);
            IComparer<string> comparer = newUsing.Group == AliasGroup ? StringComparer.Ordinal : Comparer;
            var index = result.IndexOf(u => u.GlobalKeyword.IsKind(SyntaxKind.None) && Group(u) == newUsing.Group && comparer.Compare(newUsing.Key, Key(u)) < 0);
            if (index < 0)
            {
                var last = result.LastIndexOf(u => u.GlobalKeyword.IsKind(SyntaxKind.None) && Group(u) == newUsing.Group);
                if (last < 0)
                    last = result.LastIndexOf(u => !u.GlobalKeyword.IsKind(SyntaxKind.None) || Group(u) < newUsing.Group);
                index = last + 1;
            }

            if (index == 0 && result.Count > 0)
            {
                // The first directive keeps the file's header comments.
                var first = result[0];
                directive = directive.WithLeadingTrivia(first.GetLeadingTrivia());
                result = result.Replace(first, first.WithLeadingTrivia(indentation));
            }

            result = result.Insert(index, directive);
        }

        return result;
    }

    private static UsingDirectiveSyntax Directive(NewUsing newUsing, string lineEnding)
    {
        var name = SyntaxFactory.ParseName(newUsing.Name).WithLeadingTrivia(SyntaxFactory.Space);
        var directive = newUsing switch
        {
            { Alias: { } alias } => SyntaxFactory.UsingDirective(
                SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(alias).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space)),
                SyntaxFactory.ParseTypeName(newUsing.Name).WithLeadingTrivia(SyntaxFactory.Space)),
            { Static: true } => SyntaxFactory.UsingDirective(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithLeadingTrivia(SyntaxFactory.Space), null, name),
            _ => SyntaxFactory.UsingDirective(name)
        };
        return directive.WithTrailingTrivia(SyntaxFactory.EndOfLine(lineEnding));
    }

    private static bool IsPlain(UsingDirectiveSyntax directive) =>
        directive.GlobalKeyword.IsKind(SyntaxKind.None) &&
        directive.StaticKeyword.IsKind(SyntaxKind.None) &&
        directive.Alias == null &&
        directive.Name != null;

    private const int PlainGroup = 0;
    private const int StaticGroup = 1;
    private const int AliasGroup = 2;

    private static int Group(UsingDirectiveSyntax directive) =>
        directive.Alias != null ? AliasGroup : directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? StaticGroup : PlainGroup;

    /// <summary>What a directive is sorted and recognized by: the alias name of an alias, else the name it imports.</summary>
    private static string Key(UsingDirectiveSyntax directive) =>
        directive.Alias?.Name.Identifier.ValueText ?? directive.NamespaceOrType.ToString();

    /// <summary><c>System</c> and its sub-namespaces first, then ordinal.</summary>
    private static readonly Comparer<string> Comparer = Comparer<string>.Create((left, right) =>
    {
        var leftSystem = left == "System" || left.StartsWith("System.", StringComparison.Ordinal);
        var rightSystem = right == "System" || right.StartsWith("System.", StringComparison.Ordinal);
        return leftSystem != rightSystem ? (leftSystem ? -1 : 1) : string.CompareOrdinal(left, right);
    });

    /// <summary>A using directive to add.</summary>
    /// <param name="Name">The namespace of a plain using, the type of a <c>using static</c>, or the target of an alias (written as it goes into the directive).</param>
    /// <param name="Static">Whether it is a <c>using static</c>.</param>
    /// <param name="Alias">The alias's name, for an alias.</param>
    public sealed record NewUsing(string Name, bool Static = false, string? Alias = null)
    {
        internal int Group => Alias != null ? AliasGroup : Static ? StaticGroup : PlainGroup;

        internal string Key => Alias ?? Name;
    }
}
