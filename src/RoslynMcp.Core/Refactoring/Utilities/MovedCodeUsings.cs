using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Simplification;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// What code moved into another file needs there: the using directives (and the names to write
/// qualified) for the names it uses, the way they bound where it was written.
/// </summary>
internal static class MovedCodeUsings
{
    /// <summary>
    /// The usings the target's file needs for the names the moved code uses: the namespaces of
    /// the types and extension methods it names; a <c>using static</c> of the type of a static
    /// member (or nested type) it names without its type, the way a <c>using static</c> or an
    /// enclosing type made it visible; and the aliases it uses. Where such a name would bind to
    /// something else in the target (a member of the target with that name, an alias of that name
    /// for something else), the name is written qualified instead: <paramref name="qualified"/>
    /// receives the replacement of each such name. Fails when the target's project can't see a
    /// type the moved code uses.
    /// </summary>
    /// <param name="nodes">The moved code, in its original tree.</param>
    /// <param name="source">The type the code comes from: what it names of that type (or of its nested types) is not imported.</param>
    /// <param name="sourceMoves">Whether <paramref name="source"/> moves along, so that its name binds wherever the code goes.</param>
    /// <param name="model">The semantic model of the moved code's document.</param>
    /// <param name="targetModel">The semantic model of the target's document.</param>
    /// <param name="position">Where the code goes in the target's document (where the names are bound).</param>
    /// <param name="nullable">Whether nullable annotations are enabled there, for the qualified names.</param>
    /// <param name="qualified">Receives the names to write qualified, with their replacements.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static List<UsingDirectives.NewUsing> Needed(
        IEnumerable<SyntaxNode> nodes,
        INamedTypeSymbol source,
        bool sourceMoves,
        SemanticModel model,
        SemanticModel targetModel,
        int position,
        bool nullable,
        Dictionary<SyntaxNode, SyntaxNode> qualified,
        CancellationToken cancellationToken)
    {
        var needed = new List<UsingDirectives.NewUsing>();
        foreach (var name in nodes.SelectMany(n => n.DescendantNodesAndSelf()).OfType<SimpleNameSyntax>())
        {
            if (name is IdentifierNameSyntax identifier && model.GetAliasInfo(identifier, cancellationToken) is { } alias)
            {
                AliasUsing(identifier, alias);
                continue;
            }

            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor && name.Parent is AttributeSyntax)
                symbol = constructor.ContainingType; // an attribute's name binds to its constructor

            switch (symbol)
            {
                case INamedTypeSymbol named when !IsQualified(name) && WrittenFor(name, named):
                    TypeUsing(name, named);
                    break;
                case IMethodSymbol { ReducedFrom: { } extension } when name.Parent is MemberAccessExpressionSyntax access && access.Name == name:
                    ExtensionUsing(extension);
                    break;
                case IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }
                    when !IsQualified(name) && (symbol.IsStatic || symbol is IFieldSymbol { IsConst: true }) && symbol.ContainingType is { } containing &&
                        !SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, source.OriginalDefinition):
                    StaticMemberUsing(name, symbol, containing);
                    break;
            }
        }

        return needed;

        // A type named by its simple name: its namespace, or for a nested type a static import of
        // the type it is nested in; qualified where the name means something else in the target.
        void TypeUsing(SimpleNameSyntax name, INamedTypeSymbol named)
        {
            if (sourceMoves && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, source.OriginalDefinition))
                return;

            var bound = targetModel.GetSpeculativeSymbolInfo(position, name, SpeculativeBindingOption.BindAsTypeOrNamespace);
            if (bound.Symbol != null && SameSymbol(bound.Symbol, named))
                return;

            CheckVisible(named);
            if (named.ContainingType is { } outer &&
                SymbolEqualityComparer.Default.Equals(outer.OriginalDefinition, source.OriginalDefinition))
            {
                return; // a dependency left behind, refused on its own
            }

            // Something else of that name (or several) in the target: the type's full name, unless
            // it uses type parameters the translation into the target's terms has to rewrite.
            if ((bound.Symbol != null || bound.CandidateSymbols.Length > 0) && !MentionsTypeParameter(named))
            {
                qualified[name] = Qualify(name, SyntaxGenerationHelper.CreateTypeSyntax(named, nullable));
                return;
            }

            if (named.ContainingType is { } container)
                needed.Add(new UsingDirectives.NewUsing(TypeName(container), Static: true));
            else if (named.ContainingNamespace is { IsGlobalNamespace: false } ns)
                needed.Add(new UsingDirectives.NewUsing(ns.ToDisplayString()));
        }

        static bool MentionsTypeParameter(ITypeSymbol type) => type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => MentionsTypeParameter(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(MentionsTypeParameter) || (named.ContainingType != null && MentionsTypeParameter(named.ContainingType)),
            _ => false
        };

        // An extension method called in extension form: the namespace of its class.
        void ExtensionUsing(IMethodSymbol extension)
        {
            if (targetModel.LookupNamespacesAndTypes(position, name: extension.ContainingType.Name)
                .Any(s => SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, extension.ContainingType.OriginalDefinition)))
            {
                return;
            }

            CheckVisible(extension.ContainingType);
            var outer = extension.ContainingType;
            while (outer.ContainingType != null)
                outer = outer.ContainingType;
            if (outer.ContainingNamespace is { IsGlobalNamespace: false } ns)
                needed.Add(new UsingDirectives.NewUsing(ns.ToDisplayString()));
        }

        // A static member named without its type (through a using static, or an enclosing type):
        // a using static of its type, or its type written in front of it.
        void StaticMemberUsing(SimpleNameSyntax name, ISymbol member, INamedTypeSymbol containing)
        {
            var bound = targetModel.GetSpeculativeSymbolInfo(position, SyntaxFactory.IdentifierName(name.Identifier), SpeculativeBindingOption.BindAsExpression);
            if (bound.Symbol is { } symbol ? SameSymbol(symbol, member) : bound.CandidateSymbols.Any(c => SameSymbol(c, member)))
                return;

            CheckVisible(containing);
            if (bound.Symbol == null && bound.CandidateSymbols.Length == 0)
            {
                needed.Add(new UsingDirectives.NewUsing(TypeName(containing), Static: true));
                return;
            }

            ExpressionSyntax type = SyntaxGenerationHelper.CreateTypeSyntax(containing, nullable);
            qualified[name] = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseExpression(type.ToString()).WithAdditionalAnnotations(Simplifier.Annotation),
                    name.WithoutTrivia())
                .WithTriviaFrom(name)
                .WithAdditionalAnnotations(Simplifier.Annotation);
        }

        // An alias: nothing when the target has the same one, else the same alias, or what it
        // stands for where its name means something else in the target.
        void AliasUsing(IdentifierNameSyntax name, IAliasSymbol alias)
        {
            var targetAlias = targetModel.GetSpeculativeAliasInfo(position, name, SpeculativeBindingOption.BindAsTypeOrNamespace);
            if (targetAlias != null && SameSymbol(targetAlias.Target, alias.Target))
                return;

            if (alias.Target is ITypeSymbol aliasedType)
                CheckVisible(aliasedType);

            var option = SyntaxFacts.IsInNamespaceOrTypeContext(name) ? SpeculativeBindingOption.BindAsTypeOrNamespace : SpeculativeBindingOption.BindAsExpression;
            var bound = targetModel.GetSpeculativeSymbolInfo(position, name, option);
            if ((targetAlias == null && bound.Symbol == null && bound.CandidateSymbols.Length == 0) || name.Parent is AliasQualifiedNameSyntax)
            {
                var target = alias.Target is ITypeSymbol type ? TypeName(type) : alias.Target.ToDisplayString();
                needed.Add(new UsingDirectives.NewUsing(target, Alias: alias.Name));
                return;
            }

            qualified[name] = Qualify(name, alias.Target is ITypeSymbol typeTarget
                ? SyntaxGenerationHelper.CreateTypeSyntax(typeTarget, nullable)
                : SyntaxFactory.ParseName("global::" + alias.Target.ToDisplayString()).WithAdditionalAnnotations(Simplifier.Annotation));
        }

        void CheckVisible(ITypeSymbol type)
        {
            if (type.ContainingAssembly != null && !SymbolFinder.FindSimilarSymbols(type.OriginalDefinition, targetModel.Compilation, cancellationToken).Any())
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"The moved code uses '{type.ToDisplayString()}' from {type.ContainingAssembly.Name}, which '{targetModel.Compilation.AssemblyName}' does not reference.");
            }
        }

        static bool IsQualified(SimpleNameSyntax name) =>
            name.Parent is QualifiedNameSyntax qualified && qualified.Right == name ||
            name.Parent is MemberAccessExpressionSyntax access && access.Name == name ||
            name.Parent is MemberBindingExpressionSyntax ||
            name.Parent is AliasQualifiedNameSyntax;

        // The name of the type itself, or of an attribute class without its Attribute suffix.
        static bool WrittenFor(SimpleNameSyntax name, INamedTypeSymbol type) =>
            name.Identifier.ValueText == type.Name ||
            (name.Identifier.ValueText + "Attribute" == type.Name && name.Ancestors().OfType<AttributeSyntax>().Any());

        // The same symbol, also across compilations (the target may be in another project).
        static bool SameSymbol(ISymbol left, ISymbol right) =>
            SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition) ||
            (left.Kind == right.Kind &&
                left.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == right.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) &&
                left.OriginalDefinition.GetDocumentationCommentId() == right.OriginalDefinition.GetDocumentationCommentId());

        // A type as a using directive names it: fully qualified, without global::.
        static string TypeName(ITypeSymbol type) =>
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));

        // What replaces a name: in an expression, member accesses; elsewhere the qualified name.
        static SyntaxNode Qualify(SimpleNameSyntax name, TypeSyntax qualifiedName) =>
            (SyntaxFacts.IsInNamespaceOrTypeContext(name)
                ? (SyntaxNode)SyntaxFactory.ParseTypeName(qualifiedName.ToString())
                : SyntaxFactory.ParseExpression(qualifiedName.ToString()))
            .WithTriviaFrom(name)
            .WithAdditionalAnnotations(Simplifier.Annotation);
    }
}
