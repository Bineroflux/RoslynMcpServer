using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// User-defined operators and conversions moved out of a type: how they are named, the operators
/// the compiler wants declared together, and what an interface needs to declare them in the
/// type's place (a self type parameter: <c>interface IAddable&lt;TSelf&gt; where TSelf : IAddable&lt;TSelf&gt;</c>).
/// </summary>
internal static partial class MovedOperators
{
    /// <summary>Operators declared in pairs: the compiler requires both or neither (CS0216).</summary>
    private static readonly Dictionary<string, string> RequiredPairs = new(StringComparer.Ordinal)
    {
        [WellKnownMemberNames.EqualityOperatorName] = WellKnownMemberNames.InequalityOperatorName,
        [WellKnownMemberNames.InequalityOperatorName] = WellKnownMemberNames.EqualityOperatorName,
        [WellKnownMemberNames.LessThanOperatorName] = WellKnownMemberNames.GreaterThanOperatorName,
        [WellKnownMemberNames.GreaterThanOperatorName] = WellKnownMemberNames.LessThanOperatorName,
        [WellKnownMemberNames.LessThanOrEqualOperatorName] = WellKnownMemberNames.GreaterThanOrEqualOperatorName,
        [WellKnownMemberNames.GreaterThanOrEqualOperatorName] = WellKnownMemberNames.LessThanOrEqualOperatorName,
        [WellKnownMemberNames.TrueOperatorName] = WellKnownMemberNames.FalseOperatorName,
        [WellKnownMemberNames.FalseOperatorName] = WellKnownMemberNames.TrueOperatorName
    };

    private const string CheckedPrefix = "op_Checked";

    /// <summary>Whether a member is a user-defined operator or conversion (static, or a C# 14 instance <c>+=</c> / <c>++</c>).</summary>
    public static bool IsOperator(ISymbol symbol) =>
        symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion };

    /// <summary>
    /// The operators of <paramref name="type"/> a name means as written, the word <c>operator</c>
    /// optional: <c>operator +</c> or <c>+</c> (every <c>+</c>, unary and binary),
    /// <c>operator checked +</c>, <c>operator ==</c>, <c>operator true</c>, <c>operator +=</c>,
    /// <c>explicit operator</c> (every explicit conversion) or <c>explicit operator Money</c> (the
    /// one to <c>Money</c>). Metadata names (<c>op_Addition</c>) are found as member names.
    /// </summary>
    public static IEnumerable<ISymbol> Named(INamedTypeSymbol type, string name, CancellationToken cancellationToken)
    {
        var wanted = Normalize(name);
        if (wanted.Length == 0)
            return [];

        return type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && IsOperator(m) && Keys((IMethodSymbol)m, cancellationToken).Contains(wanted, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>How the declaration names an operator: <c>operator +</c>, <c>operator checked +</c>, <c>explicit operator Money</c>.</summary>
    public static string WrittenName(IMethodSymbol op, CancellationToken cancellationToken) =>
        op.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) switch
        {
            OperatorDeclarationSyntax declaration => $"operator {Checked(declaration.CheckedKeyword)}{declaration.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax conversion =>
                $"{conversion.ImplicitOrExplicitKeyword.Text} operator {Checked(conversion.CheckedKeyword)}{conversion.Type}",
            _ => op.Name
        };

    /// <summary>
    /// <paramref name="selected"/> and the operators of <paramref name="type"/> that go with the
    /// selected ones, with the same parameter and return types: the other one of <c>==</c> and
    /// <c>!=</c>, <c>&lt;</c> and <c>&gt;</c>, <c>&lt;=</c> and <c>&gt;=</c>, <c>true</c> and
    /// <c>false</c> (CS0216); the unchecked version of a checked operator (CS9025); and the
    /// checked version of an unchecked one, which code in a checked context calls. One the target
    /// already declares (<paramref name="declaredByTarget"/>) is left where it is.
    /// <paramref name="notes"/> receives what was added and why.
    /// </summary>
    public static List<SelectedDeclaration> WithPartners(
        List<SelectedDeclaration> selected,
        INamedTypeSymbol type,
        Func<IMethodSymbol, bool> declaredByTarget,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var chosen = new HashSet<ISymbol>(selected.SelectMany(s => s.Symbols), SymbolEqualityComparer.Default);
        var pending = new Queue<IMethodSymbol>(chosen.OfType<IMethodSymbol>().Where(IsOperator));
        var added = new List<SelectedDeclaration>();
        while (pending.TryDequeue(out var op))
        {
            foreach (var (name, kind) in PartnerNames(op.Name))
            {
                var partners = type.GetMembers(name)
                    .OfType<IMethodSymbol>()
                    .Where(p => !p.IsImplicitlyDeclared && IsOperator(p) && SameSignature(p, op) && !chosen.Contains(p) && !declaredByTarget(p));
                foreach (var partner in partners.ToList())
                {
                    chosen.Add(partner);
                    pending.Enqueue(partner);
                    added.Add(MovedMembers.Whole(partner, cancellationToken));

                    var (written, partnerWritten) = (WrittenName(op, cancellationToken), WrittenName(partner, cancellationToken));
                    notes.Add($"'{partner.ToDisplayString()}' is pulled up too: " + kind switch
                    {
                        PartnerKind.Required => $"the compiler requires '{written}' and '{partnerWritten}' to be declared together (CS0216).",
                        PartnerKind.Unchecked => $"'{written}' needs its unchecked version declared with it (CS9025).",
                        _ => $"it is the checked version of '{written}', which code in a checked context calls instead."
                    });
                }
            }
        }

        return added.Count == 0 ? selected : MovedMembers.InSourceOrder(selected.Concat(added));
    }

    /// <summary>
    /// The type parameter of the interface <paramref name="view"/> that <paramref name="type"/>
    /// passes itself as (<c>IAddable&lt;TSelf&gt;</c> implemented as <c>IAddable&lt;Money&gt;</c>) and
    /// that is constrained to the interface itself (<c>where TSelf : IAddable&lt;TSelf&gt;</c>): what
    /// the interface's operators take in the type's place. Null when there is none.
    /// </summary>
    public static ITypeParameterSymbol? SelfTypeParameter(INamedTypeSymbol type, INamedTypeSymbol view) =>
        PassedAs(type, view).FirstOrDefault(p => p.ConstraintTypes.Any(c => SymbolEqualityComparer.Default.Equals(c, p.ContainingType)));

    /// <summary>
    /// <paramref name="symbol"/> with <paramref name="type"/> replaced by <paramref name="self"/>,
    /// also as a type argument or array element; null where it is <c>Nullable&lt;type&gt;</c>,
    /// which a type parameter can't stand for. Other types are returned as they are.
    /// </summary>
    public static ITypeSymbol? ReplaceSelf(ITypeSymbol symbol, INamedTypeSymbol type, ITypeParameterSymbol self, Compilation compilation)
    {
        if (SymbolEqualityComparer.Default.Equals(symbol, type))
            return self.WithNullableAnnotation(symbol.NullableAnnotation);

        switch (symbol)
        {
            case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                when SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], type):
                return null;
            case IArrayTypeSymbol array:
                return ReplaceSelf(array.ElementType, type, self, compilation) is { } element
                    ? compilation.CreateArrayTypeSymbol(element, array.Rank, element.NullableAnnotation).WithNullableAnnotation(array.NullableAnnotation)
                    : null;
            case INamedTypeSymbol { IsGenericType: true } generic when generic.TypeArguments.Any(a => Mentions(a, type)):
                var arguments = generic.TypeArguments.Select(a => ReplaceSelf(a, type, self, compilation)).ToList();
                return arguments.Any(a => a == null)
                    ? null
                    : generic.ConstructedFrom.Construct(arguments.ToImmutableArray()!, arguments.Select(a => a!.NullableAnnotation).ToImmutableArray())
                        .WithNullableAnnotation(generic.NullableAnnotation);
            default:
                return symbol;
        }
    }

    /// <summary>Whether a type is <paramref name="type"/> or names it in its type arguments or element type.</summary>
    public static bool Mentions(ITypeSymbol symbol, INamedTypeSymbol type) => symbol switch
    {
        _ when SymbolEqualityComparer.Default.Equals(symbol, type) => true,
        IArrayTypeSymbol array => Mentions(array.ElementType, type),
        IPointerTypeSymbol pointer => Mentions(pointer.PointedAtType, type),
        INamedTypeSymbol named => named.TypeArguments.Any(a => Mentions(a, type)) || (named.ContainingType != null && Mentions(named.ContainingType, type)),
        _ => false
    };

    /// <summary>
    /// Refuses an operator of <paramref name="type"/> that the interface <paramref name="view"/>
    /// can't declare: one that uses <c>Nullable&lt;type&gt;</c> where the interface would write its
    /// self type parameter <c>TSelf?</c> (which is not <c>Nullable&lt;TSelf&gt;</c>); and a static
    /// one none of whose operands (for a conversion: source or target; for a shift: the first)
    /// is, once translated, the interface or its self type parameter.
    /// </summary>
    public static void CheckForInterface(
        IMethodSymbol op,
        INamedTypeSymbol type,
        INamedTypeSymbol view,
        ITypeParameterSymbol? self,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
        Compilation compilation)
    {
        var target = view.OriginalDefinition;
        var name = op.ToDisplayString();
        if (self != null && op.Parameters.Select(p => p.Type).Prepend(op.ReturnType).FirstOrDefault(t => ReplaceSelf(t, type, self, compilation) == null) is { } nullable)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotMoveable,
                $"'{name}' uses '{nullable.ToDisplayString()}', which '{target.ToDisplayString()}' would have to write '{self.Name}?'; for a type parameter " +
                $"that is not Nullable<{self.Name}>, so '{type.Name}' would not implement the operator.");
        }

        // An instance operator (C# 14 +=, ++) operates on the interface itself.
        if (!op.IsStatic)
            return;

        var conversion = op.MethodKind == MethodKind.Conversion;
        var shift = op.Name is WellKnownMemberNames.LeftShiftOperatorName or WellKnownMemberNames.RightShiftOperatorName or WellKnownMemberNames.UnsignedRightShiftOperatorName;
        IEnumerable<ITypeSymbol> operands = conversion
            ? [op.Parameters[0].Type, op.ReturnType]
            : shift ? [op.Parameters[0].Type] : op.Parameters.Select(p => p.Type);
        if (operands.Any(IsInterfaceOrSelf))
            return;

        var position = conversion ? "its source or target type" : shift ? "its first operand" : "an operand";
        var passedAs = PassedAs(type, view).FirstOrDefault();
        var why = passedAs != null
            ? $"'{type.Name}' implements it as '{view.ToDisplayString()}', but '{passedAs.Name}' is not constrained to the interface " +
              $"(where {passedAs.Name} : {target.ToDisplayString()}), so the operator can't take it in place of '{type.Name}'."
            : $"'{target.ToDisplayString()}' has no type parameter '{type.Name}' passes itself as, which the operator could take in its place " +
              $"(interface IAddable<TSelf> where TSelf : IAddable<TSelf>, implemented as IAddable<{type.Name}>).";
        throw new RefactoringException(
            ErrorCodes.MemberNotMoveable,
            $"'{name}' can't be declared in '{target.ToDisplayString()}': an operator of an interface must have the interface, or a type parameter " +
            $"constrained to it, as {position}. {why}");

        bool IsInterfaceOrSelf(ITypeSymbol operand) =>
            (self != null && SymbolEqualityComparer.Default.Equals(operand, type)) ||
            (MemberFamily.Substitute(operand, typeMap, compilation) is { } translated && SymbolEqualityComparer.Default.Equals(translated, target));
    }

    /// <summary>
    /// The refusal of a static operator moving into a class: it must take the class that declares
    /// it (CS0563, for a conversion CS0556) and can't be abstract or virtual.
    /// </summary>
    public static RefactoringException IntoClass(IMethodSymbol op, INamedTypeSymbol target) =>
        new(
            ErrorCodes.MemberNotMoveable,
            $"'{op.ToDisplayString()}' is an operator, which can't move into the class '{target.ToDisplayString()}': " +
            (op.MethodKind == MethodKind.Conversion
                ? "a conversion must convert from or to the class that declares it (CS0556)"
                : "an operator must take the class that declares it as an operand (CS0563)") +
            $" and can't be abstract or virtual, so in '{target.Name}' it would need another signature, which would change its meaning. " +
            "To share an operator, declare it in an interface with a self type parameter (interface IAddable<TSelf> where TSelf : IAddable<TSelf>) " +
            "that the types implement, and pull it up into that interface (targetType).");

    /// <summary>
    /// The binary operator expressions in <paramref name="documents"/> that call one of the
    /// operators <paramref name="target"/> declares now but did not before (the
    /// <paramref name="pulled"/> ones), where they compiled before and called something else
    /// (e.g. <c>==</c> on a class-constrained type parameter, which compared references): one
    /// note each, <c>path(line,column): ...</c>.
    /// </summary>
    public static async Task<List<string>> NowCalledAsync(
        INamedTypeSymbol target,
        IEnumerable<ISymbol> pulled,
        Solution before,
        Solution after,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var tokens = pulled
            .SelectMany(p => p.DeclaringSyntaxReferences)
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<OperatorDeclarationSyntax>()
            .Select(o => o.OperatorToken.Text)
            .ToHashSet(StringComparer.Ordinal);
        if (tokens.Count == 0)
            return [];

        var declared = target.GetMembers().Select(m => m.GetDocumentationCommentId()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var id in documents)
        {
            if (after.GetDocument(id) is not { } document || before.GetDocument(id) is not { } previous ||
                await document.GetSyntaxRootAsync(cancellationToken) is not { } root ||
                await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            List<TextChange>? changes = null;
            SemanticModel? previousModel = null;
            foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(b => tokens.Contains(b.OperatorToken.Text)))
            {
                if (model.GetSymbolInfo(binary, cancellationToken).Symbol is not IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } called ||
                    !SymbolIdentity.SameDeclaration(called.ContainingType, target) ||
                    called.OriginalDefinition.GetDocumentationCommentId() is not { } calledId ||
                    declared.Contains(calledId) ||
                    model.GetDiagnostics(binary.Span, cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    continue;
                }

                changes ??= (await document.GetTextChangesAsync(previous, cancellationToken)).OrderBy(c => c.Span.Start).ToList();
                previousModel ??= await previous.GetSemanticModelAsync(cancellationToken);
                if (previousModel == null || OldPosition(changes, binary.SpanStart) is not { } start ||
                    previousModel.SyntaxTree.GetRoot(cancellationToken).FindNode(new TextSpan(start, binary.Span.Length), getInnermostNodeForTie: true)
                        .AncestorsAndSelf().OfType<BinaryExpressionSyntax>().FirstOrDefault(b => b.SpanStart == start && b.Span.Length == binary.Span.Length) is not { } old ||
                    previousModel.GetDiagnostics(old.Span, cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    continue;
                }

                var was = previousModel.GetOperation(old, cancellationToken) is IBinaryOperation { OperatorMethod: { } method }
                    ? $"'{method.ToDisplayString()}'"
                    : "the built-in operator" + (binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression ? " (a reference comparison)" : "");
                var line = binary.GetLocation().GetLineSpan();
                notes.Add($"{line.Path}({line.StartLinePosition.Line + 1},{line.StartLinePosition.Character + 1}): '{binary}' now calls " +
                    $"'{called.OriginalDefinition.ToDisplayString()}' instead of {was}.");
            }
        }

        return notes;
    }

    /// <summary>Where a position of the changed text was before <paramref name="changes"/>; null inside a change.</summary>
    private static int? OldPosition(List<TextChange> changes, int position)
    {
        var delta = 0;
        foreach (var change in changes)
        {
            var start = change.Span.Start + delta;
            if (position < start)
                break;
            if (position < start + change.NewText!.Length)
                return null;
            delta += change.NewText.Length - change.Span.Length;
        }

        return position - delta;
    }

    /// <summary>The interface's type parameters <paramref name="type"/> passes itself as.</summary>
    private static IEnumerable<ITypeParameterSymbol> PassedAs(INamedTypeSymbol type, INamedTypeSymbol view) =>
        view.OriginalDefinition.TypeParameters.Zip(view.TypeArguments)
            .Where(pair => SymbolEqualityComparer.Default.Equals(pair.Second, type))
            .Select(pair => pair.First);

    private enum PartnerKind
    {
        Required,
        Unchecked,
        Checked
    }

    private static IEnumerable<(string Name, PartnerKind Kind)> PartnerNames(string name)
    {
        if (RequiredPairs.TryGetValue(name, out var other))
            yield return (other, PartnerKind.Required);

        if (name.StartsWith(CheckedPrefix, StringComparison.Ordinal))
            yield return ("op_" + name[CheckedPrefix.Length..], PartnerKind.Unchecked);
        else if (name.StartsWith("op_", StringComparison.Ordinal))
            yield return (CheckedPrefix + name["op_".Length..], PartnerKind.Checked);
    }

    /// <summary>The same static-ness, parameter types and return type.</summary>
    private static bool SameSignature(IMethodSymbol left, IMethodSymbol right) =>
        left.IsStatic == right.IsStatic &&
        left.Parameters.Length == right.Parameters.Length &&
        SymbolEqualityComparer.Default.Equals(left.ReturnType, right.ReturnType) &&
        left.Parameters.Zip(right.Parameters).All(p => p.First.RefKind == p.Second.RefKind && SymbolEqualityComparer.Default.Equals(p.First.Type, p.Second.Type));

    /// <summary>The names a user may give an operator, normalized (<see cref="Normalize"/>).</summary>
    private static IEnumerable<string> Keys(IMethodSymbol op, CancellationToken cancellationToken)
    {
        switch (op.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken))
        {
            case OperatorDeclarationSyntax declaration:
                yield return Normalize(Checked(declaration.CheckedKeyword) + declaration.OperatorToken.Text);
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                var head = conversion.ImplicitOrExplicitKeyword.Text + " " + Checked(conversion.CheckedKeyword);
                yield return Normalize(head);
                yield return Normalize(head + conversion.Type);
                yield return Normalize(head + op.ReturnType.ToDisplayString());
                break;
        }
    }

    private static string Checked(SyntaxToken keyword) => keyword.IsKind(SyntaxKind.CheckedKeyword) ? "checked " : "";

    /// <summary>
    /// A name without the word <c>operator</c> and without the spaces next to punctuation:
    /// <c>operator checked +</c> and <c>checked+</c> are both <c>checked+</c>.
    /// </summary>
    private static string Normalize(string name)
    {
        var text = Whitespace().Replace(OperatorKeyword().Replace(name, " "), " ").Trim();
        return SpaceAroundPunctuation().Replace(text, "$1");
    }

    [GeneratedRegex(@"\boperator\b")]
    private static partial Regex OperatorKeyword();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@" ?([^\w ]) ?")]
    private static partial Regex SpaceAroundPunctuation();
}
