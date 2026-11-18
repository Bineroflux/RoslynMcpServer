using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Members a refactoring moves out of a type into another one: picking them by name, the
/// accessibility they need in their new type, and what of the old type they still use.
/// </summary>
internal static class MovedMembers
{
    /// <summary>
    /// The declarations of <paramref name="type"/>'s members named <paramref name="names"/>: every
    /// overload of a method, a property, an indexer (<c>this</c>), an event (field-like or with
    /// accessors) and a field. Variables declared together (<c>int a, b;</c>) form one declaration
    /// with only the named ones selected. Fails with <see cref="ErrorCodes.MemberNotFound"/> for
    /// a name that matches none, and <see cref="ErrorCodes.MemberNotMoveable"/> for constructors,
    /// operators, nested types and members declared outside the type's source.
    /// </summary>
    public static List<SelectedDeclaration> Select(INamedTypeSymbol type, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var symbols = new List<ISymbol>();
        foreach (var name in names.Select(n => n.Trim()).Distinct(StringComparer.Ordinal))
        {
            var members = name is "this" or "this[]"
                ? type.GetMembers().Where(m => m is IPropertySymbol { IsIndexer: true }).ToList()
                : type.GetMembers(name).Where(m => !m.IsImplicitlyDeclared).ToList();
            members = members.Where(m => m is not IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
                and not IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
                and not IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 }).ToList();

            if (members.Count == 0)
            {
                missing.Add(name);
                continue;
            }

            foreach (var member in members)
            {
                var reason = member switch
                {
                    INamedTypeSymbol => "nested types are not moved",
                    IMethodSymbol { MethodKind: not MethodKind.Ordinary } => "constructors, operators and finalizers are not moved",
                    IMethodSymbol { PartialDefinitionPart: not null } or IMethodSymbol { PartialImplementationPart: not null } => "it is partial",
                    IPropertySymbol { PartialDefinitionPart: not null } or IPropertySymbol { PartialImplementationPart: not null } => "it is partial",
                    _ when member.DeclaringSyntaxReferences.Length == 0 => "it is not declared in source",
                    _ when member.DeclaringSyntaxReferences.Any(r => r.GetSyntax(cancellationToken) is ParameterSyntax) => "it is declared by the record's parameter list",
                    _ => null
                };

                if (reason != null)
                    throw new RefactoringException(ErrorCodes.MemberNotMoveable, $"'{member.ToDisplayString()}' cannot be moved: {reason}.");

                symbols.Add(member);
            }
        }

        if (missing.Count > 0)
        {
            var available = type.GetMembers()
                .Where(m => !m.IsImplicitlyDeclared && m is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })
                .Select(m => m is IPropertySymbol { IsIndexer: true } ? "this" : m.Name)
                .Distinct()
                .Order(StringComparer.Ordinal);
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found in '{type.Name}': {string.Join(", ", missing)}. Its members: {string.Join(", ", available)}.");
        }

        // One entry per declaration; variables declared together share theirs.
        var selected = new List<SelectedDeclaration>();
        foreach (var group in symbols.GroupBy(s => Declaration(s, cancellationToken)))
        {
            var variables = group.Key is BaseFieldDeclarationSyntax
                ? group.Select(s => (VariableDeclaratorSyntax)s.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken)).ToList()
                : [];
            selected.Add(new SelectedDeclaration(group.Key, group.ToList(), variables));
        }

        return selected
            .OrderBy(s => s.Declaration.SyntaxTree.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Declaration.SpanStart)
            .ToList();
    }

    private static MemberDeclarationSyntax Declaration(ISymbol symbol, CancellationToken cancellationToken)
    {
        var syntax = symbol.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
        return syntax switch
        {
            VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } => field,
            MemberDeclarationSyntax member => member,
            _ => throw new RefactoringException(ErrorCodes.MemberNotMoveable, $"'{symbol.ToDisplayString()}' cannot be moved: unexpected declaration.")
        };
    }

    /// <summary>
    /// The modifiers of a member moved into a type that its old type derives from: a private member
    /// becomes <c>protected</c> so its old type can still use it, and a <c>private protected</c> one
    /// in another assembly becomes <c>protected</c>, the only access that reaches across. The note
    /// says what changed, or is null.
    /// </summary>
    public static (SyntaxTokenList Modifiers, string? Note) MovedModifiers(
        SyntaxTokenList modifiers,
        ISymbol member,
        string targetName,
        bool sameAssembly)
    {
        var isPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);
        var isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        var hasAccessibility = isPrivate || isProtected || modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.InternalKeyword);

        if (isPrivate && isProtected)
        {
            if (sameAssembly)
                return (modifiers, null);

            var withoutPrivate = SyntaxFactory.TokenList(modifiers.Where(m => !m.IsKind(SyntaxKind.PrivateKeyword)));
            return (withoutPrivate.Count > 0 ? withoutPrivate.Replace(withoutPrivate[0], withoutPrivate[0].WithLeadingTrivia(modifiers[0].LeadingTrivia)) : withoutPrivate,
                $"'{member.ToDisplayString()}' was private protected; it is protected in '{targetName}', which is in another assembly.");
        }

        if (isPrivate || !hasAccessibility)
        {
            var @protected = SyntaxFactory.Token(SyntaxKind.ProtectedKeyword);
            var newModifiers = isPrivate
                ? SyntaxFactory.TokenList(modifiers.Select(m => m.IsKind(SyntaxKind.PrivateKeyword) ? @protected.WithTriviaFrom(m) : m))
                : modifiers.Count == 0
                    ? SyntaxFactory.TokenList(@protected.WithTrailingTrivia(SyntaxFactory.Space))
                    : modifiers.Insert(0, @protected.WithLeadingTrivia(modifiers[0].LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space))
                        .Replace(modifiers[0], modifiers[0].WithLeadingTrivia());
            return (newModifiers, $"'{member.ToDisplayString()}' was private; it is protected in '{targetName}'.");
        }

        return (modifiers, null);
    }

    /// <summary>
    /// The members of <paramref name="source"/> (and its nested types) that <paramref name="nodes"/>
    /// use but that are not among <paramref name="moved"/>: what would be left behind.
    /// </summary>
    public static List<ISymbol> Dependencies(
        IEnumerable<SyntaxNode> nodes,
        INamedTypeSymbol source,
        IReadOnlyCollection<ISymbol> moved,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var movedSet = new HashSet<ISymbol>(moved.Select(m => m.OriginalDefinition), SymbolEqualityComparer.Default);
        var dependencies = new List<ISymbol>();
        foreach (var name in nodes.SelectMany(n => n.DescendantNodesAndSelf()).OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
            if (symbol is IMethodSymbol { AssociatedSymbol: { } associated })
                symbol = associated;
            if (symbol is null or IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.LocalFunction } or ILocalSymbol or IParameterSymbol or ITypeParameterSymbol)
                continue;

            var definition = symbol.OriginalDefinition;
            if (definition.ContainingType is { } containing &&
                SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, source.OriginalDefinition) &&
                !movedSet.Contains(definition) &&
                !dependencies.Contains(definition, SymbolEqualityComparer.Default))
            {
                dependencies.Add(definition);
            }
        }

        return dependencies;
    }

    /// <summary>
    /// The comments directly above a declaration (its documentation comment), without the blank
    /// lines, directives or indentation before them.
    /// </summary>
    public static SyntaxTriviaList Comments(SyntaxNode declaration)
    {
        var trivia = declaration.GetLeadingTrivia();
        var start = 0;
        for (var i = 0; i < trivia.Count; i++)
        {
            if (trivia[i].IsDirective || (trivia[i].IsKind(SyntaxKind.EndOfLineTrivia) && IsLineStart(trivia, i)))
                start = i + 1;
        }

        return SyntaxFactory.TriviaList(trivia.Skip(start).Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia)));

        // An end of line with nothing but whitespace before it on its line: a blank line.
        static bool IsLineStart(SyntaxTriviaList list, int index)
        {
            for (var j = index - 1; j >= 0; j--)
            {
                if (list[j].IsKind(SyntaxKind.EndOfLineTrivia))
                    return true;
                if (!list[j].IsKind(SyntaxKind.WhitespaceTrivia))
                    return false;
            }

            return true;
        }
    }
}

/// <summary>
/// A member declaration chosen to be moved, with the members it declares that were chosen; for a
/// field or event field, the chosen variables among those it declares.
/// </summary>
internal sealed record SelectedDeclaration(
    MemberDeclarationSyntax Declaration,
    IReadOnlyList<ISymbol> Symbols,
    IReadOnlyList<VariableDeclaratorSyntax> Variables)
{
    /// <summary>Whether every variable of a field declaration is chosen (always true for other members).</summary>
    public bool IsWhole => Declaration is not BaseFieldDeclarationSyntax fieldDeclaration ||
        fieldDeclaration.Declaration.Variables.Count == Variables.Count;

    /// <summary>
    /// <paramref name="declaration"/> (<see cref="Declaration"/> or a rewritten copy of it) with only
    /// the chosen variables of a field or event field.
    /// </summary>
    public MemberDeclarationSyntax Chosen(MemberDeclarationSyntax declaration)
    {
        if (IsWhole || Declaration is not BaseFieldDeclarationSyntax original || declaration is not BaseFieldDeclarationSyntax copy)
            return declaration;

        var indexes = Variables.Select(v => original.Declaration.Variables.IndexOf(v)).ToHashSet();
        var variables = copy.Declaration.Variables.Where((_, i) => indexes.Contains(i)).Select(v => v.WithoutTrivia()).ToList();
        return copy.WithDeclaration(copy.Declaration.WithVariables(SyntaxFactory.SeparatedList(
            variables,
            Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space), variables.Count - 1))));
    }

    /// <summary>
    /// <paramref name="declaration"/> (<see cref="Declaration"/> or a copy of it) without the chosen
    /// variables of a field or event field; null when the whole declaration is chosen.
    /// </summary>
    public MemberDeclarationSyntax? Remaining(MemberDeclarationSyntax declaration)
    {
        if (IsWhole)
            return null;
        if (Declaration is not BaseFieldDeclarationSyntax original || declaration is not BaseFieldDeclarationSyntax copy)
            return declaration;

        var indexes = Variables.Select(v => original.Declaration.Variables.IndexOf(v)).ToHashSet();
        var variables = copy.Declaration.Variables.Where((_, i) => !indexes.Contains(i)).Select(v => v.WithoutTrivia()).ToList();
        return copy.WithDeclaration(copy.Declaration.WithVariables(SyntaxFactory.SeparatedList(
            variables,
            Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space), variables.Count - 1))));
    }

    /// <summary>The syntax that leaves the old type: the whole declaration, or the chosen variables.</summary>
    public IEnumerable<SyntaxNode> Removed => IsWhole ? [Declaration] : Variables;
}
