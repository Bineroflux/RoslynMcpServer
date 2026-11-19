using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Changes a method's signature by adding, removing, or reordering parameters, together with
/// every method of its override / implementation family (see <see cref="MemberFamily"/>), and
/// updates the calls to all of them.
/// </summary>
/// <remarks>
/// The request lists changes: a parameter it does not list stays, in its relative order; an
/// entry with <c>originalName</c> renames, retypes, gives a default value to, moves or removes
/// that parameter, and an entry without one adds a parameter. Parameters correspond by position
/// across the family, since an override may name them differently. A rename in the request
/// renames the parameter at that position in every member (and its uses in the body and named
/// arguments at the calls); otherwise each member keeps its own name. A kept parameter keeps its
/// own type, default value, modifiers and attributes unless the request changes them. The
/// compiler errors the change leaves in the affected documents are reported as warnings.
/// </remarks>
public sealed class ChangeSignatureOperation : RefactoringOperationBase<ChangeSignatureParams>
{
    private const string ParameterListAnnotationKind = "RoslynMcp.ChangeSignature.ParameterList";
    private const string PlaceholderAnnotationKind = "RoslynMcp.ChangeSignature.Placeholder";

    /// <summary>
    /// Creates a new change signature operation.
    /// </summary>
    public ChangeSignatureOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ChangeSignatureParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (@params.Parameters == null || @params.Parameters.Count == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "parameters is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        // Validate parameter changes
        for (var i = 0; i < @params.Parameters.Count; i++)
        {
            var change = @params.Parameters[i];
            if (IsNew(change))
            {
                if (change.Remove)
                    throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"parameters[{i}] removes a parameter but names none: give its originalName.");

                if (string.IsNullOrWhiteSpace(change.Name))
                    throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"parameters[{i}] adds a parameter (it has no originalName), which needs a name.");

                if (string.IsNullOrWhiteSpace(change.Type))
                    throw new RefactoringException(ErrorCodes.MissingRequiredParam, $"parameters[{i}] adds a parameter (it has no originalName), which needs a type.");
            }

            if (change.NewPosition < 0)
                throw new RefactoringException(ErrorCodes.InvalidParameterPosition, $"parameters[{i}].newPosition must be 0 or more.");

            if (change.Name != null && !change.Remove)
                IdentifierNames.Validate(change.Name, NameKind.Parameter, $"parameters[{i}].name", ErrorCodes.InvalidNewName);
        }
    }

    /// <summary>Whether an entry adds a parameter: it names no existing one.</summary>
    private static bool IsNew(ParameterChange change) => string.IsNullOrWhiteSpace(change.OriginalName);

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ChangeSignatureParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        var methodDecl = SignatureSyntax.SelectDeclaration(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == @params.MethodName).ToList(),
            "method",
            @params.MethodName,
            @params.Line,
            m => m.Identifier,
            ErrorCodes.MethodNotFound);

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        var originalParams = methodSymbol.Parameters.ToList();

        // Types the request writes, bound where the method is so each member can be given the
        // same type. A type that does not resolve is written as given, everywhere.
        var typePosition = methodDecl.ParameterList.OpenParenToken.Span.End;
        ITypeSymbol? Bind(string type) => SignatureSyntax.BindType(semanticModel, typePosition, type);

        var newParameters = BuildNewParameterList(methodDecl, originalParams, @params.Parameters, Bind);
        CheckOptionalParametersLast(newParameters, originalParams);

        // A rename applies to the parameter at that position in every member of the family.
        var renames = new string?[originalParams.Count];
        foreach (var parameter in newParameters.Where(p => p.OriginalIndex.HasValue))
        {
            var index = parameter.OriginalIndex!.Value;
            if (IdentifierNames.ValueText(parameter.Name) != originalParams[index].Name)
                renames[index] = parameter.Name;
        }

        var boundTypes = newParameters.ToDictionary(p => p, p => p.Type == null ? null : Bind(p.Type));

        var solution = Context.Solution;
        var family = await MemberFamily.FindAsync(methodSymbol, semanticModel.Compilation, solution, cancellationToken);
        var edits = new SyntaxEdits();
        var declarations = new List<(Document Document, MethodDeclarationSyntax Declaration, SyntaxAnnotation Annotation, IMethodSymbol Symbol)>();

        foreach (var member in family.Members)
        {
            foreach (var reference in member.Declarations)
            {
                if (await reference.GetSyntaxAsync(cancellationToken) is not MethodDeclarationSyntax declaration ||
                    solution.GetDocument(reference.SyntaxTree) is not { } declaringDocument)
                {
                    continue;
                }

                var model = await declaringDocument.GetSemanticModelAsync(cancellationToken);
                if (model?.GetDeclaredSymbol(declaration, cancellationToken) is not { } declared ||
                    declared.Parameters.Length != originalParams.Count)
                {
                    throw new RefactoringException(
                        ErrorCodes.RoslynError,
                        $"Could not match the parameters of '{member.Symbol.ToDisplayString()}' to those of '{methodSymbol.ToDisplayString()}'.");
                }

                CheckNamesInMember(declaration, model, declared, newParameters, renames, cancellationToken);

                var types = newParameters.ToDictionary(
                    p => p,
                    p => p.Type == null
                        ? null
                        : boundTypes[p] is { } bound
                            ? SignatureSyntax.WriteType(bound, member, model, declaration.SpanStart)
                            : SyntaxFactory.ParseTypeName(p.Type));
                var isExplicit = declared.ExplicitInterfaceImplementations.Length > 0;
                var annotation = new SyntaxAnnotation(ParameterListAnnotationKind);

                edits.Add(declaringDocument.Id, declaration.ParameterList, rewritten =>
                    BuildParameterList((ParameterListSyntax)rewritten, newParameters, renames, types, isExplicit)
                        .WithAdditionalAnnotations(annotation));
                declarations.Add((declaringDocument, declaration, annotation, declared));

                AddParameterRenames(edits, declaringDocument.Id, declaration, model, declared, renames, cancellationToken);
            }
        }

        var references = await family.FindReferencesAsync(solution, cancellationToken);
        var callSites = new Dictionary<DocumentId, int>();
        foreach (var location in references.Where(l => !l.IsImplicit))
        {
            if (await TryAddCallSiteAsync(edits, location, family, newParameters, renames, cancellationToken))
                callSites[location.Document.Id] = callSites.GetValueOrDefault(location.Document.Id) + 1;
        }

        var newSolution = await edits.ApplyAsync(solution, cancellationToken);

        // What only a person can fix: method groups converted to delegates, a removed parameter
        // the body still uses, the TODO placeholders passed for parameters without a default value.
        var errors = await IntroducedErrors.FindAsync(
            solution, newSolution, family.AffectedDocuments(solution, references), cancellationToken);
        var placeholders = await FindPlaceholdersAsync(newSolution, callSites.Keys, cancellationToken);
        var warnings = errors == null && placeholders.Count == 0 ? null : (errors ?? []).Concat(placeholders).ToList();

        if (@params.Preview)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(newSolution, declarations, callSites, cancellationToken),
                Warnings = warnings
            };
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = @params.MethodName,
                FullyQualifiedName = methodSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            ReferencesUpdated = callSites.Values.Sum(),
            Warnings = warnings
        };
    }

    /// <summary>
    /// The new parameter list: the method's parameters with the request's changes, then the new
    /// ones. A parameter with a newPosition goes there; the others keep their order in the
    /// remaining places, so a parameter the request does not move keeps its place among them and
    /// a new one without a position goes at the end.
    /// </summary>
    /// <param name="declaration">The method's declaration.</param>
    /// <param name="originalParams">The method's parameters.</param>
    /// <param name="changes">The request's entries.</param>
    /// <param name="bindType">Binds a type the request writes where the method is.</param>
    private static List<NewParameter> BuildNewParameterList(
        MethodDeclarationSyntax declaration,
        List<IParameterSymbol> originalParams,
        IReadOnlyList<ParameterChange> changes,
        Func<string, ITypeSymbol?> bindType)
    {
        // The entry that changes each parameter.
        var entries = new int?[originalParams.Count];
        var added = new List<NewParameter>();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (IsNew(change))
            {
                added.Add(new NewParameter
                {
                    Name = change.Name!,
                    Type = change.Type,
                    DefaultValue = change.DefaultValue,
                    Position = change.NewPosition,
                    Entry = i
                });
                continue;
            }

            var name = IdentifierNames.ValueText(change.OriginalName!);
            var index = originalParams.FindIndex(p => p.Name == name);
            if (index < 0)
            {
                throw new RefactoringException(
                    ErrorCodes.ParameterNotFound,
                    $"parameters[{i}].originalName: the method has no parameter '{change.OriginalName}'. " +
                    $"Its parameters: {(originalParams.Count == 0 ? "none" : string.Join(", ", originalParams.Select(p => p.Name)))}.");
            }

            if (entries[index] is { } first)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidParameterValue,
                    $"Parameter '{name}' is listed twice, in parameters[{first}] and parameters[{i}]. Give each parameter one entry.");
            }

            entries[index] = i;
            if (change.Remove)
                CheckRemoval(change, i, originalParams[index], declaration.ParameterList.Parameters[index], bindType);
        }

        var parameters = new List<NewParameter>();
        for (var index = 0; index < originalParams.Count; index++)
        {
            var change = entries[index] is { } entry ? changes[entry] : null;
            if (change?.Remove == true)
                continue;

            parameters.Add(new NewParameter
            {
                Name = change?.Name ?? declaration.ParameterList.Parameters[index].Identifier.Text,
                Type = change?.Type,
                DefaultValue = change?.DefaultValue,
                Position = change?.NewPosition,
                OriginalIndex = index,
                Entry = entries[index]
            });
        }

        parameters.AddRange(added);
        var result = PlaceParameters(parameters);

        var duplicate = result.GroupBy(p => IdentifierNames.ValueText(p.Name)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            string Describe(NewParameter p) => p.OriginalIndex is { } index
                ? p.Entry is { } entry && IdentifierNames.ValueText(p.Name) != originalParams[index].Name
                    ? $"'{originalParams[index].Name}', renamed in parameters[{entry}]"
                    : $"'{originalParams[index].Name}'"
                : $"the new parameter of parameters[{p.Entry}]";

            throw new RefactoringException(
                ErrorCodes.NameConflictScope,
                $"Two parameters would be named '{duplicate.Key}': {string.Join(" and ", duplicate.Select(Describe))}. Give them different names.");
        }

        return result;
    }

    /// <summary>
    /// Puts each parameter with a position there and fills the other places with the rest, in
    /// their order. Refuses a position outside the new list and two parameters at one position.
    /// </summary>
    private static List<NewParameter> PlaceParameters(List<NewParameter> parameters)
    {
        var slots = new NewParameter?[parameters.Count];
        foreach (var parameter in parameters.Where(p => p.Position.HasValue))
        {
            var position = parameter.Position!.Value;
            if (position >= parameters.Count)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidParameterPosition,
                    $"parameters[{parameter.Entry}].newPosition {position} is outside the new parameter list, which has " +
                    $"{parameters.Count} parameter{(parameters.Count == 1 ? "" : "s")} (positions 0 to {parameters.Count - 1}).");
            }

            if (slots[position] is { } taken)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidParameterPosition,
                    $"parameters[{taken.Entry}] and parameters[{parameter.Entry}] both ask for newPosition {position}.");
            }

            slots[position] = parameter;
        }

        using var rest = parameters.Where(p => !p.Position.HasValue).GetEnumerator();
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null && rest.MoveNext())
                slots[i] = rest.Current;
        }

        return slots.Select(p => p!).ToList();
    }

    /// <summary>
    /// Refuses an entry that removes a parameter and also changes it: its other fields must be
    /// absent or equal to what the parameter has.
    /// </summary>
    private static void CheckRemoval(
        ParameterChange change,
        int entry,
        IParameterSymbol parameter,
        ParameterSyntax syntax,
        Func<string, ITypeSymbol?> bindType)
    {
        static string Compact(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

        var changed = new List<string>();
        if (change.Name != null && IdentifierNames.ValueText(change.Name) != parameter.Name)
            changed.Add($"the name '{change.Name}'");
        if (change.Type != null && !SymbolEqualityComparer.Default.Equals(bindType(change.Type), parameter.Type))
            changed.Add($"the type '{change.Type}'");
        if (change.DefaultValue != null && Compact(change.DefaultValue) != Compact(syntax.Default?.Value.ToString() ?? ""))
            changed.Add($"the defaultValue '{change.DefaultValue}'");
        if (change.NewPosition != null)
            changed.Add("a newPosition");

        if (changed.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterValue,
                $"parameters[{entry}] removes '{parameter.Name}' but also gives it {string.Join(" and ", changed)}. " +
                "An entry that removes a parameter changes nothing else: leave the other fields out.");
        }
    }

    /// <summary>
    /// Refuses a parameter list in which a parameter without a default value follows one with a
    /// default value (CS1737). Only a <c>params</c> array may follow, as the last parameter. A
    /// parameter has the default value the request gives it, or else the one it has.
    /// </summary>
    private static void CheckOptionalParametersLast(IReadOnlyList<NewParameter> parameters, IReadOnlyList<IParameterSymbol> originalParams)
    {
        NewParameter? optional = null;
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var hasDefault = parameter.KeepsDefault
                ? parameter.OriginalIndex is { } kept && originalParams[kept].HasExplicitDefaultValue
                : parameter.NewDefault != null;
            if (hasDefault)
            {
                optional ??= parameter;
                continue;
            }

            var isParamsArray = parameter.OriginalIndex is { } index && originalParams[index].IsParams && i == parameters.Count - 1;
            if (optional != null && !isParamsArray)
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidParameterPosition,
                    $"Parameter '{IdentifierNames.ValueText(parameter.Name)}' has no defaultValue but comes after " +
                    $"'{IdentifierNames.ValueText(optional.Name)}', which has one: a required parameter cannot follow an optional " +
                    "one (CS1737). Give it a defaultValue or move it before the optional parameters.");
            }
        }
    }

    /// <summary>
    /// Refuses a rename that would clash in one member of the family: two of its parameters
    /// would get the same name, or the new name is already used in its body (a local, a
    /// parameter of a lambda or local function, a range or pattern variable, a local function,
    /// a type parameter, or a simple name such as a field the body reads), so a use of the
    /// parameter, or of that other symbol, would bind to the wrong one.
    /// </summary>
    private static void CheckNamesInMember(
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        IMethodSymbol declared,
        IReadOnlyList<NewParameter> newParameters,
        IReadOnlyList<string?> renames,
        CancellationToken cancellationToken)
    {
        var names = newParameters.Select(p => IdentifierNames.ValueText(p.OriginalIndex is { } index
            ? renames[index] ?? declared.Parameters[index].Name
            : p.Name));
        if (names.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new RefactoringException(
                ErrorCodes.NameConflictScope,
                $"Two parameters of '{declared.ToDisplayString()}' would be named '{duplicate.Key}' (its parameters, matched by " +
                $"position: {string.Join(", ", declared.Parameters.Select(p => p.Name))}). Choose another name.");
        }

        var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;
        for (var i = 0; i < renames.Count; i++)
        {
            if (renames[i] is not { } newName || IdentifierNames.ValueText(newName) == declared.Parameters[i].Name)
                continue;

            var name = IdentifierNames.ValueText(newName);
            var parameter = declared.Parameters[i];
            var clash = declared.TypeParameters.Any(t => t.Name == name)
                ? $"the method has a type parameter '{name}'"
                : body == null ? null : FindNameInBody(body, model, parameter, name, cancellationToken);

            if (clash != null)
            {
                throw new RefactoringException(
                    ErrorCodes.NameConflictScope,
                    $"Cannot rename parameter '{parameter.Name}' of '{declared.ToDisplayString()}' to '{name}': {clash}. Choose another name.");
            }
        }
    }

    /// <summary>
    /// Where <paramref name="body"/> declares <paramref name="name"/>, or uses it as a simple
    /// name for something declared outside the method other than its parameters (whose new
    /// names are checked against each other); null when it does neither.
    /// </summary>
    private static string? FindNameInBody(
        SyntaxNode body,
        SemanticModel model,
        IParameterSymbol parameter,
        string name,
        CancellationToken cancellationToken)
    {
        static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        foreach (var node in body.DescendantNodesAndSelf())
        {
            var declaredHere = node switch
            {
                VariableDeclaratorSyntax or SingleVariableDesignationSyntax or ParameterSyntax or ForEachStatementSyntax
                    or CatchDeclarationSyntax or LocalFunctionStatementSyntax or TypeParameterSyntax or QueryClauseSyntax
                    or QueryContinuationSyntax => model.GetDeclaredSymbol(node, cancellationToken),
                _ => null
            };
            if (declaredHere?.Name == name)
            {
                var what = declaredHere switch
                {
                    ILocalSymbol => "a local",
                    IParameterSymbol => "a lambda or local function parameter",
                    IRangeVariableSymbol => "a query range variable",
                    IMethodSymbol => "a local function",
                    ITypeParameterSymbol => "a type parameter",
                    _ => "a symbol"
                };
                return $"the body declares {what} '{name}' (line {Line(node)})";
            }

            if (node is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == name && IsSimpleNameLookup(identifier) &&
                model.GetSymbolInfo(identifier, cancellationToken).Symbol is { } used &&
                !(used is IParameterSymbol && SymbolEqualityComparer.Default.Equals(used.ContainingSymbol, parameter.ContainingSymbol)) &&
                !IsDeclaredInside(used, body))
            {
                var what = used switch
                {
                    IFieldSymbol => "field",
                    IPropertySymbol => "property",
                    IMethodSymbol => "method",
                    IEventSymbol => "event",
                    INamespaceSymbol => "namespace",
                    ITypeSymbol => "type",
                    _ => "symbol"
                };
                return $"the body uses the {what} '{used.ToDisplayString()}' by that name (line {Line(identifier)}), which would then mean the parameter";
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="symbol"/> is declared inside <paramref name="body"/> (reported where it is declared).</summary>
    private static bool IsDeclaredInside(ISymbol symbol, SyntaxNode body) =>
        symbol.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == body.SyntaxTree && body.Span.Contains(r.Span));

    /// <summary>
    /// Whether <paramref name="identifier"/> is looked up as a simple name in its scope (so a
    /// parameter of that name would hide what it means now), rather than as a member of
    /// something, an argument or member name, or a type.
    /// </summary>
    private static bool IsSimpleNameLookup(IdentifierNameSyntax identifier) => identifier.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Name == identifier => false,
        MemberBindingExpressionSyntax => false,
        QualifiedNameSyntax qualified when qualified.Right == identifier => false,
        AliasQualifiedNameSyntax alias when alias.Name == identifier => false,
        NameColonSyntax or NameEqualsSyntax => false,
        AssignmentExpressionSyntax assignment when assignment.Left == identifier &&
            (assignment.Parent.IsKind(SyntaxKind.ObjectInitializerExpression) || assignment.Parent.IsKind(SyntaxKind.WithInitializerExpression)) => false,
        _ => !identifier.IsVar && !SyntaxFacts.IsInTypeOnlyContext(identifier)
    };

    /// <summary>
    /// The new parameter list of one family member. A kept parameter keeps its own declaration
    /// (name, type, default value, modifiers, attributes) except for what the request changes.
    /// An explicit interface implementation gets no new default values, which would have no
    /// effect there (CS1066).
    /// </summary>
    private static ParameterListSyntax BuildParameterList(
        ParameterListSyntax original,
        IReadOnlyList<NewParameter> newParameters,
        IReadOnlyList<string?> renames,
        IReadOnlyDictionary<NewParameter, TypeSyntax?> types,
        bool isExplicitImplementation)
    {
        var parameters = new List<ParameterSyntax>();
        foreach (var newParameter in newParameters)
        {
            ParameterSyntax parameter;
            if (newParameter.OriginalIndex is { } index)
            {
                parameter = original.Parameters[index];
                if (renames[index] is { } name)
                    parameter = parameter.WithIdentifier(IdentifierNames.Token(name).WithTriviaFrom(parameter.Identifier));
                if (types[newParameter] is { } type)
                    parameter = parameter.WithType(parameter.Type == null ? type : type.WithTriviaFrom(parameter.Type));
            }
            else
            {
                parameter = SyntaxFactory.Parameter(IdentifierNames.Token(newParameter.Name))
                    .WithType(types[newParameter]!.WithTrailingTrivia(SyntaxFactory.Space));
                if (original.Parameters.Count > 0)
                    parameter = parameter.WithLeadingTrivia(original.Parameters[^1].GetLeadingTrivia());
            }

            var defaultValue = newParameter.OriginalIndex.HasValue && newParameter.KeepsDefault
                ? null
                : isExplicitImplementation ? "" : newParameter.NewDefault ?? "";
            parameters.Add(WithDefault(parameter, defaultValue));
        }

        return original.WithParameters(SyntaxFactory.SeparatedList(parameters, Separators(original.Parameters, parameters.Count)));
    }

    /// <summary>
    /// <paramref name="parameter"/> with <paramref name="defaultValue"/>: null keeps its default
    /// value (or none), an empty string removes it.
    /// </summary>
    private static ParameterSyntax WithDefault(ParameterSyntax parameter, string? defaultValue)
    {
        if (defaultValue == null)
            return parameter;

        if (defaultValue.Length == 0)
        {
            return parameter.Default == null
                ? parameter
                : parameter.WithDefault(null).WithIdentifier(parameter.Identifier.WithTrailingTrivia(
                    parameter.Identifier.TrailingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia))));
        }

        var value = SyntaxFactory.ParseExpression(defaultValue);
        if (parameter.Default != null)
            return parameter.WithDefault(parameter.Default.WithValue(value.WithTriviaFrom(parameter.Default.Value)));

        return parameter
            .WithIdentifier(parameter.Identifier.WithTrailingTrivia(SyntaxFactory.Space))
            .WithDefault(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.Token(SyntaxKind.EqualsToken).WithTrailingTrivia(SyntaxFactory.Space),
                value));
    }

    /// <summary>
    /// Separators for a list of <paramref name="count"/> items: the original ones (and so their
    /// line breaks) as far as they go, then copies of the last one, or <c>", "</c>.
    /// </summary>
    private static IEnumerable<SyntaxToken> Separators<TNode>(SeparatedSyntaxList<TNode> original, int count)
        where TNode : SyntaxNode
    {
        var fallback = original.SeparatorCount > 0
            ? original.GetSeparator(original.SeparatorCount - 1)
            : SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);

        for (var i = 0; i < count - 1; i++)
            yield return i < original.SeparatorCount ? original.GetSeparator(i) : fallback;
    }

    /// <summary>
    /// Renames the uses of a renamed parameter inside <paramref name="declaration"/>: the body,
    /// <c>nameof</c>, and documentation comments.
    /// </summary>
    private static void AddParameterRenames(
        SyntaxEdits edits,
        DocumentId document,
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        IMethodSymbol declared,
        IReadOnlyList<string?> renames,
        CancellationToken cancellationToken)
    {
        var renamed = new Dictionary<IParameterSymbol, string>(SymbolEqualityComparer.Default);
        for (var i = 0; i < renames.Count; i++)
        {
            if (renames[i] is { } name && declared.Parameters[i].Name != IdentifierNames.ValueText(name))
                renamed[declared.Parameters[i]] = name;
        }

        if (renamed.Count == 0)
            return;

        foreach (var identifier in declaration.DescendantNodes(descendIntoTrivia: true).OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is IParameterSymbol parameter &&
                renamed.TryGetValue(parameter, out var name))
            {
                edits.Add(document, identifier, rewritten =>
                {
                    var node = (IdentifierNameSyntax)rewritten;
                    return node.WithIdentifier(IdentifierNames.Token(name).WithTriviaFrom(node.Identifier));
                });
            }
        }
    }

    /// <summary>
    /// Rewrites the arguments of a call to a family member found at <paramref name="location"/>.
    /// Other references (method groups, <c>nameof</c>, crefs) are left alone.
    /// </summary>
    private static async Task<bool> TryAddCallSiteAsync(
        SyntaxEdits edits,
        ReferenceLocation location,
        MemberFamily family,
        IReadOnlyList<NewParameter> newParameters,
        IReadOnlyList<string?> renames,
        CancellationToken cancellationToken)
    {
        var root = await location.Document.GetSyntaxRootAsync(cancellationToken);
        var model = await location.Document.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
            return false;

        SyntaxNode callee = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
        if (callee.Parent is MemberAccessExpressionSyntax access && access.Name == callee ||
            callee.Parent is MemberBindingExpressionSyntax)
        {
            callee = callee.Parent;
        }

        if (callee.Parent is not InvocationExpressionSyntax invocation || invocation.Expression != callee ||
            model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol invoked)
        {
            return false;
        }

        if (!family.Contains(invoked.ReducedFrom ?? invoked))
            return false;

        // A call in extension form passes the `this` parameter as the receiver.
        var offset = invoked.ReducedFrom != null ? 1 : 0;
        var parameters = invoked.Parameters;
        var original = invocation.ArgumentList;

        // A new parameter with a default value is left out, unless an argument after it cannot
        // be passed by name then (an expanded params argument): such a call passes the default.
        var passNewOptional = BuildArgumentList(original, parameters, offset, newParameters, renames, passNewOptional: false) == null;
        var rewrittenHere = BuildArgumentList(original, parameters, offset, newParameters, renames, passNewOptional);
        if (rewrittenHere == null || rewrittenHere.IsEquivalentTo(original))
            return false; // nothing to change, or a call it cannot rewrite

        return edits.Add(location.Document.Id, original, rewritten =>
            BuildArgumentList((ArgumentListSyntax)rewritten, parameters, offset, newParameters, renames, passNewOptional) ?? rewritten);
    }

    /// <summary>
    /// The new arguments of one call. The call keeps its own argument order, so its arguments
    /// are evaluated as before: an argument of a removed parameter is dropped in place, a named
    /// argument is renamed in place, and only positional arguments move, to the new positions
    /// of their parameters. A new parameter with a default value is left out (unless
    /// <paramref name="passNewOptional"/>), so the arguments after it are named. A parameter the
    /// call does not pass and has to (a new one without a default value, or one that lost its
    /// default value) gets a <c>TODO</c> placeholder: positional where every argument before it
    /// is positional and in position, otherwise named at the end. From the first argument out of
    /// position on, every argument is named (CS8323). Null when the call cannot be rewritten (an
    /// argument names no parameter, the receiver of an extension-form call moves, or an expanded
    /// <c>params</c> argument would need a name).
    /// </summary>
    private static ArgumentListSyntax? BuildArgumentList(
        ArgumentListSyntax original,
        IReadOnlyList<IParameterSymbol> parameters,
        int offset,
        IReadOnlyList<NewParameter> newParameters,
        IReadOnlyList<string?> renames,
        bool passNewOptional)
    {
        // A call in extension form passes the `this` parameter as the receiver, which has to stay first.
        if (offset > 0 && newParameters.FirstOrDefault()?.OriginalIndex != 0)
            return null;

        var callParameters = newParameters.Skip(offset).ToList();
        string NameAt(int position) => callParameters[position].OriginalIndex is { } index
            ? renames[index] ?? parameters[index - offset].Name
            : callParameters[position].Name;

        // The call's arguments in its own order, each with the new position of its parameter.
        var entries = new List<CallArgument>();
        var passed = new HashSet<int>();
        for (var i = 0; i < original.Arguments.Count; i++)
        {
            var argument = original.Arguments[i];
            var index = argument.NameColon is { } nameColon
                ? parameters.ToList().FindIndex(p => p.Name == nameColon.Name.Identifier.ValueText)
                : Math.Min(i, parameters.Count - 1);
            if (index < 0)
                return null;

            index += offset;
            passed.Add(index);
            var position = callParameters.FindIndex(p => p.OriginalIndex == index);
            if (position < 0)
                continue; // the parameter is removed

            if (argument.NameColon is { } named && renames[index] is { } newName)
                argument = argument.WithNameColon(named.WithName(IdentifierNames.Name(newName).WithTriviaFrom(named.Name)));

            entries.Add(new CallArgument(argument, position, IsNew: false));
        }

        // Positional arguments move to their parameters' new positions, within the places
        // positional arguments take in the call.
        var slots = Enumerable.Range(0, entries.Count).Where(i => entries[i].Argument.NameColon == null).ToList();
        var moved = slots.Select(i => entries[i]).OrderBy(e => e.Position).ToList();
        for (var i = 0; i < slots.Count; i++)
            entries[slots[i]] = moved[i];

        // Arguments the call has to pass now.
        for (var position = 0; position < callParameters.Count; position++)
        {
            var parameter = callParameters[position];
            if (parameter.OriginalIndex is { } index)
            {
                var declared = parameters[index - offset];
                var optional = parameter.KeepsDefault ? declared.IsOptional : parameter.NewDefault != null;
                if (passed.Contains(index) || optional || declared.IsParams)
                    continue; // passed, or still optional
            }
            else if (parameter.NewDefault != null && !passNewOptional)
            {
                continue; // a new optional parameter
            }

            var argument = parameter.NewDefault == null
                ? SyntaxFactory.Argument(SyntaxFactory.ParseExpression($"default /* TODO: {NameAt(position)} */"))
                    .WithAdditionalAnnotations(new SyntaxAnnotation(PlaceholderAnnotationKind, NameAt(position)))
                : SyntaxFactory.Argument(SyntaxFactory.ParseExpression(parameter.NewDefault));
            var inPositionPrefix = entries.TakeWhile((e, i) => e.Argument.NameColon == null && e.Position == i).Count();
            entries.Insert(position <= inPositionPrefix ? position : entries.Count, new CallArgument(argument, position, IsNew: true));
        }

        var arguments = new List<ArgumentSyntax>();
        var nameTheRest = false;
        var seenNamed = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var (argument, position, isNew) = entries[i];
            var expandedParams = (i > 0 && entries[i - 1].Position == position) ||
                (i + 1 < entries.Count && entries[i + 1].Position == position);
            var inPosition = position == i || (i > 0 && entries[i - 1].Position == position);

            if (argument.NameColon == null && (nameTheRest || !inPosition || (isNew && seenNamed)))
            {
                if (expandedParams)
                    return null;
                argument = WithName(argument, NameAt(position));
            }

            nameTheRest |= !inPosition;
            seenNamed |= argument.NameColon != null;
            arguments.Add(argument);
        }

        return original.WithArguments(SyntaxFactory.SeparatedList(arguments, Separators(original.Arguments, arguments.Count)));
    }

    /// <summary>The <c>TODO</c> placeholders the calls now pass, as <c>path(line,column): TODO: ...</c>.</summary>
    private static async Task<List<string>> FindPlaceholdersAsync(
        Solution solution,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var placeholders = new List<(string Path, int Line, int Column, string Text)>();
        foreach (var id in documents)
        {
            if (await solution.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken) is not { } root)
                continue;

            foreach (var argument in root.GetAnnotatedNodes(PlaceholderAnnotationKind))
            {
                var span = argument.GetLocation().GetLineSpan();
                var (line, column) = (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
                var name = argument.GetAnnotations(PlaceholderAnnotationKind).First().Data;
                placeholders.Add((span.Path, line, column,
                    $"{span.Path}({line},{column}): TODO: replace the placeholder 'default' passed for parameter '{name}'"));
            }
        }

        return placeholders
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Line)
            .ThenBy(p => p.Column)
            .Select(p => p.Text)
            .ToList();
    }

    private static ArgumentSyntax WithName(ArgumentSyntax argument, string name) =>
        argument.WithoutLeadingTrivia()
            .WithNameColon(SyntaxFactory.NameColon(
                IdentifierNames.Name(name),
                SyntaxFactory.Token(SyntaxKind.ColonToken).WithTrailingTrivia(SyntaxFactory.Space)))
            .WithLeadingTrivia(argument.GetLeadingTrivia());

    private sealed record CallArgument(ArgumentSyntax Argument, int Position, bool IsNew);

    /// <summary>
    /// One pending change per changed declaration (its parameter list before and after) and
    /// one per document with updated calls.
    /// </summary>
    private static async Task<List<PendingChange>> CreatePendingChangesAsync(
        Solution newSolution,
        List<(Document Document, MethodDeclarationSyntax Declaration, SyntaxAnnotation Annotation, IMethodSymbol Symbol)> declarations,
        Dictionary<DocumentId, int> callSites,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        foreach (var (document, declaration, annotation, symbol) in declarations)
        {
            var newRoot = await newSolution.GetDocument(document.Id)!.GetSyntaxRootAsync(cancellationToken);
            var line = declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            changes.Add(new PendingChange
            {
                File = document.FilePath ?? document.Name,
                ChangeType = ChangeKind.Modify,
                Description = $"Change the parameters of '{symbol.ToDisplayString()}'",
                BeforeSnippet = declaration.Identifier.Text + declaration.ParameterList,
                AfterSnippet = declaration.Identifier.Text + newRoot?.GetAnnotatedNodes(annotation).FirstOrDefault(),
                StartLine = line,
                EndLine = line
            });
        }

        foreach (var (id, count) in callSites.OrderBy(c => newSolution.GetDocument(c.Key)?.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var document = newSolution.GetDocument(id)!;
            changes.Add(new PendingChange
            {
                File = document.FilePath ?? document.Name,
                ChangeType = ChangeKind.Modify,
                Description = count == 1 ? "Update 1 call site" : $"Update {count} call sites"
            });
        }

        return changes;
    }

    private sealed class NewParameter
    {
        /// <summary>The name the request gives, or the parameter's own.</summary>
        public required string Name { get; init; }

        /// <summary>The type the request gives, or null to keep each member's own.</summary>
        public string? Type { get; init; }

        /// <summary>
        /// The default value the request gives: null keeps the parameter's own (a new one has
        /// none), blank removes it.
        /// </summary>
        public string? DefaultValue { get; init; }

        /// <summary>The position the request gives in the new list, or null.</summary>
        public int? Position { get; init; }

        /// <summary>The position the parameter had, or null for a new one.</summary>
        public int? OriginalIndex { get; init; }

        /// <summary>The index of the request entry that lists the parameter, or null when none does.</summary>
        public int? Entry { get; init; }

        /// <summary>Whether each member keeps the parameter's own default value (or lack of one).</summary>
        public bool KeepsDefault => DefaultValue == null;

        /// <summary>The default value the request gives, null when it gives none or removes it.</summary>
        public string? NewDefault => string.IsNullOrWhiteSpace(DefaultValue) ? null : DefaultValue;
    }
}
