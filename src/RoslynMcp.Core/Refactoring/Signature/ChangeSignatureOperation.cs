using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Simplification;
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
/// compiler errors the change leaves, in any project that compiles or depends on what changed,
/// are reported as warnings. A file compiled once per target framework is changed in each
/// framework's document (its own <c>#if</c> branches) and gets the changes of all of them.
/// </remarks>
public sealed class ChangeSignatureOperation : RefactoringOperationBase<ChangeSignatureParams>
{
    private const string ParameterListAnnotationKind = "RoslynMcp.ChangeSignature.ParameterList";
    private const string PlaceholderPrefix = "/* TODO: ";
    private const string PlaceholderSuffix = " */";

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
        var (document, methodDecl) = await SignatureSyntax.SelectDeclarationAsync(
            GetDocumentOrThrow(@params.SourceFile),
            root => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == @params.MethodName),
            "method",
            @params.MethodName,
            @params.Line,
            m => m.Identifier,
            ErrorCodes.MethodNotFound,
            cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

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
        var places = new SourcePlaces();

        // Each declaration is changed in the document of every target framework compiling it, so
        // the uses of a renamed parameter in each framework's #if branches are renamed too.
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
                if (places.Add(declaringDocument, declaration.Span))
                    declarations.Add((declaringDocument, declaration, annotation, declared));

                AddParameterRenames(edits, declaringDocument.Id, declaration, model, declared, renames, cancellationToken);
            }
        }

        // A call that several target frameworks compile is rewritten in each one's document and
        // counted once, for the file's first document.
        var references = await family.FindReferencesAsync(solution, cancellationToken);
        var callSites = new Dictionary<DocumentId, int>();
        foreach (var location in references.Where(l => !l.IsImplicit))
        {
            if (await TryAddCallSiteAsync(edits, location, family, newParameters, renames, cancellationToken) &&
                places.Add(location.Document, location.Location.SourceSpan))
            {
                var file = places.FirstDocument(location.Document);
                callSites[file] = callSites.GetValueOrDefault(file) + 1;
            }
        }

        // Each target framework's document of a file has the edits of its own #if branches; the
        // file gets them all.
        var applied = await edits.ApplyAsync(solution, cancellationToken);
        var conflicts = new List<string>();
        var newSolution = await LinkedDocuments.MergeAsync(solution, applied, document.Id, cancellationToken, conflicts);

        // What only a person can fix: method groups converted to delegates, a removed parameter
        // the body still uses, the TODO placeholders passed for parameters without a default value.
        var errors = await IntroducedErrors.FindInProjectsAsync(
            solution, newSolution, family.AffectedDocuments(solution, references), cancellationToken);
        var placeholders = await FindPlaceholdersAsync(solution, newSolution, callSites.Keys, cancellationToken);
        var warnings = conflicts.Concat(errors ?? []).Concat(placeholders).ToList();
        if (methodSymbol.IsExtensionMethod && newParameters.FirstOrDefault()?.OriginalIndex != 0)
        {
            warnings.Insert(0,
                $"'{methodSymbol.ToDisplayString()}' is no longer an extension method: its 'this' parameter '{originalParams[0].Name}' " +
                $"{(newParameters.Any(p => p.OriginalIndex == 0) ? "is no longer first" : "is removed")}, so its calls in extension form " +
                "now call it as a static method.");
        }

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(applied, declarations, callSites, cancellationToken),
                Warnings = warnings.Count == 0 ? null : warnings
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
            Warnings = warnings.Count == 0 ? null : warnings
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

                // Only the first parameter can be an extension method's `this`.
                if (parameters.Count > 0 && parameter.Modifiers.IndexOf(SyntaxKind.ThisKeyword) is var thisModifier and >= 0)
                    parameter = WithoutModifier(parameter, thisModifier);
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
    /// <paramref name="parameter"/> without its modifier at <paramref name="index"/>; the
    /// parameter keeps the leading trivia (its indentation) the modifier had.
    /// </summary>
    private static ParameterSyntax WithoutModifier(ParameterSyntax parameter, int index)
    {
        var modifier = parameter.Modifiers[index];
        var result = parameter.WithModifiers(parameter.Modifiers.RemoveAt(index));
        return index == 0 && parameter.AttributeLists.Count == 0
            ? result.WithLeadingTrivia(modifier.LeadingTrivia)
            : result;
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

        // A call in extension form passes the `this` parameter as the receiver. When that
        // parameter does not stay first, the method is no longer an extension method and the
        // call is written in static form, the receiver as its first argument: x.M(1) is Ext.M(x, 1).
        var toStaticForm = invoked.ReducedFrom != null && newParameters.FirstOrDefault()?.OriginalIndex != 0;
        if (toStaticForm && invocation.Expression is not MemberAccessExpressionSyntax)
            return false; // x?.M(1) is left alone
        var unreduced = toStaticForm ? invoked.GetConstructedReducedFrom()! : null;
        var (parameters, offset) = unreduced != null
            ? (unreduced.Parameters, 0)
            : (invoked.Parameters, invoked.ReducedFrom != null ? 1 : 0);
        var original = unreduced != null ? WithReceiver(invocation, unreduced) : invocation.ArgumentList;

        // A parameter whose default value the request removes keeps meaning what it meant in a
        // call that left it out: the call passes the default of the member it binds to.
        var oldDefaults = new Dictionary<int, ExpressionSyntax>();
        foreach (var parameter in newParameters)
        {
            if (parameter is { OriginalIndex: { } index, KeepsDefault: false, NewDefault: null, Type: null } &&
                index - offset >= 0 &&
                await OldDefaultAsync(parameters[index - offset], model, invocation.ArgumentList.SpanStart, location.Document, cancellationToken) is { } value)
            {
                oldDefaults[index] = value;
            }
        }

        // Expanded params arguments that have to be named are passed as one collection.
        var nullable = model.GetNullableContext(invocation.SpanStart).AnnotationsEnabled();
        Func<IReadOnlyList<ExpressionSyntax>, ExpressionSyntax>? collect = invoked.Parameters.LastOrDefault() is { IsParams: true } last
            ? elements => ParamsCollection(elements, last.Type, invocation.SyntaxTree.Options, nullable)
            : null;

        // A new parameter with a default value is left out, unless an argument after it cannot
        // be passed by name then (an expanded params argument): such a call passes the default,
        // or else passes the expanded arguments as one collection, by name.
        (bool PassNewOptional, bool Collect)[] ways = [(false, false), (true, false), (false, true)];
        ArgumentListSyntax? Build(ArgumentListSyntax list, (bool PassNewOptional, bool Collect) way) =>
            BuildArgumentList(list, parameters, offset, newParameters, renames, oldDefaults, way.PassNewOptional, way.Collect ? collect : null);

        var chosen = ways.Select(w => ((bool PassNewOptional, bool Collect)?)w).FirstOrDefault(w => Build(original, w!.Value) != null);
        if (chosen is not { } way)
            return false; // a call it cannot rewrite

        if (unreduced == null)
        {
            if (Build(original, way)!.IsEquivalentTo(original))
                return false; // nothing to change

            return edits.Add(location.Document.Id, original, rewritten => Build((ArgumentListSyntax)rewritten, way) ?? rewritten);
        }

        return edits.Add(location.Document.Id, invocation, rewritten =>
        {
            var call = (InvocationExpressionSyntax)rewritten;
            return Build(WithReceiver(call, unreduced), way) is { } arguments ? StaticCall(call, unreduced, arguments) : call;
        });
    }

    /// <summary>
    /// The arguments of <paramref name="call"/>, an extension method call in extension form
    /// (<c>x.M(1)</c>), with the receiver first, as the static form passes them (<c>(x, 1)</c>).
    /// </summary>
    private static ArgumentListSyntax WithReceiver(InvocationExpressionSyntax call, IMethodSymbol method)
    {
        var receiver = SyntaxFactory.Argument(((MemberAccessExpressionSyntax)call.Expression).Expression.WithoutTrivia());
        if (method.Parameters[0].RefKind == RefKind.Ref)
            receiver = receiver.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        var arguments = call.ArgumentList.Arguments;
        var separators = arguments.Count == 0
            ? []
            : new[] { SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space) }.Concat(arguments.GetSeparators());
        return call.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(arguments.Insert(0, receiver), separators));
    }

    /// <summary>
    /// <paramref name="call"/> (<c>x.M(1)</c>) in static form with <paramref name="arguments"/>:
    /// <c>Ext.M(x, 1)</c>, the class name marked for the simplifier.
    /// </summary>
    private static InvocationExpressionSyntax StaticCall(InvocationExpressionSyntax call, IMethodSymbol method, ArgumentListSyntax arguments)
    {
        var name = ((MemberAccessExpressionSyntax)call.Expression).Name;
        var callee = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxGenerationHelper.CreateTypeSyntax(method.ContainingType, nullableAnnotations: false),
            name.WithoutTrivia());
        return SyntaxFactory.InvocationExpression(callee, arguments).WithTriviaFrom(call);
    }

    /// <summary>
    /// The expanded arguments <paramref name="elements"/> of a <c>params</c> parameter of type
    /// <paramref name="type"/> as one collection: a collection expression (<c>["a", "b"]</c>)
    /// from C# 12 on, an array creation (<c>new string[] { "a", "b" }</c>) before.
    /// </summary>
    private static ExpressionSyntax ParamsCollection(
        IReadOnlyList<ExpressionSyntax> elements,
        ITypeSymbol type,
        ParseOptions options,
        bool nullableAnnotations)
    {
        var items = elements.Select(e => e.WithoutTrivia()).ToList();
        var separators = Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space), Math.Max(0, items.Count - 1));
        if (options is CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp12 } || type is not IArrayTypeSymbol array)
            return SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>(items.Select(SyntaxFactory.ExpressionElement), separators));

        return SyntaxFactory.ArrayCreationExpression(
            SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.ArrayType(
                SyntaxGenerationHelper.CreateTypeSyntax(array.ElementType, nullableAnnotations),
                SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier(
                    SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.OmittedArraySizeExpression()))))
                .WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SeparatedList(items, separators),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space)));
    }

    /// <summary>
    /// The default value <paramref name="parameter"/> has, written for a call at
    /// <paramref name="position"/>: its expression as the declaration writes it, with the types
    /// and members it names qualified for the simplifier so they mean the same at the call; or,
    /// when it names something the call cannot access (a private constant), its value as a
    /// literal. Null when the parameter has no default value.
    /// </summary>
    private static async Task<ExpressionSyntax?> OldDefaultAsync(
        IParameterSymbol parameter,
        SemanticModel callModel,
        int position,
        Document callDocument,
        CancellationToken cancellationToken)
    {
        if (!parameter.HasExplicitDefaultValue)
            return null;

        var solution = callDocument.Project.Solution;
        foreach (var reference in parameter.OriginalDefinition.DeclaringSyntaxReferences)
        {
            if (await reference.GetSyntaxAsync(cancellationToken) is ParameterSyntax { Default.Value: { } written } &&
                solution.GetDocument(reference.SyntaxTree) is { } declaringDocument &&
                await declaringDocument.GetSemanticModelAsync(cancellationToken) is { } declaringModel)
            {
                return QualifiedForCall(written, declaringModel, callModel, position, cancellationToken)
                    ?? ConstantArgument(parameter, callModel, position, callDocument);
            }
        }

        return ConstantArgument(parameter, callModel, position, callDocument);
    }

    /// <summary>
    /// <paramref name="written"/> with each type and member it names written fully qualified
    /// and marked for the simplifier, or null when it names something that cannot be written
    /// at <paramref name="position"/> (not accessible there, a type parameter of the member, or
    /// not a type, namespace or static member, such as <c>nameof</c>).
    /// </summary>
    private static ExpressionSyntax? QualifiedForCall(
        ExpressionSyntax written,
        SemanticModel declaringModel,
        SemanticModel callModel,
        int position,
        CancellationToken cancellationToken)
    {
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var name in written.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var symbol = declaringModel.GetSymbolInfo(name, cancellationToken).Symbol;
            if (symbol is ITypeParameterSymbol or not (INamespaceOrTypeSymbol or { IsStatic: true }) ||
                (symbol is not INamespaceSymbol && !callModel.IsAccessible(position, symbol)))
            {
                return null;
            }

            // The right side of a dot is reached through its left side.
            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name ||
                name.Parent is QualifiedNameSyntax qualified && qualified.Right == name ||
                name.Parent is AliasQualifiedNameSyntax alias && alias.Name == name)
            {
                continue;
            }

            var text = symbol is INamespaceOrTypeSymbol namespaceOrType
                ? namespaceOrType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : $"{symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{IdentifierNames.Token(symbol.Name).Text}";
            // A name in a type (default(T), a cast's type) has to stay a name there.
            ExpressionSyntax qualifiedName = SyntaxFacts.IsInNamespaceOrTypeContext(name)
                ? SyntaxFactory.ParseName(text)
                : SyntaxFactory.ParseExpression(text);
            replacements[name] = qualifiedName.WithTriviaFrom(name).WithAdditionalAnnotations(Simplifier.Annotation);
        }

        return written.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]).WithoutTrivia();
    }

    /// <summary>
    /// The constant default value of <paramref name="parameter"/> as a literal: an enum value
    /// by its member's name where there is one, <c>null</c> or <c>default</c> for a null value.
    /// </summary>
    private static ExpressionSyntax ConstantArgument(IParameterSymbol parameter, SemanticModel callModel, int position, Document callDocument)
    {
        var value = parameter.ExplicitDefaultValue;
        var type = parameter.Type;
        if (value == null)
        {
            return type.IsReferenceType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                : SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression, SyntaxFactory.Token(SyntaxKind.DefaultKeyword));
        }

        var enumType = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? ((INamedTypeSymbol)type).TypeArguments[0]
            : type;
        var generator = SyntaxGenerator.GetGenerator(callDocument);
        if (enumType.TypeKind == TypeKind.Enum)
        {
            var typeName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var member = enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f =>
                f.HasConstantValue && Equals(f.ConstantValue, value) && callModel.IsAccessible(position, f));
            var expression = member != null
                ? SyntaxFactory.ParseExpression($"{typeName}.{IdentifierNames.Token(member.Name).Text}")
                : SyntaxFactory.ParseExpression($"({typeName})({((ExpressionSyntax)generator.LiteralExpression(value)).ToFullString()})");
            return expression.WithAdditionalAnnotations(Simplifier.Annotation);
        }

        return (ExpressionSyntax)generator.LiteralExpression(value);
    }

    /// <summary>
    /// The new arguments of one call. The call keeps its own argument order, so its arguments
    /// are evaluated as before: an argument of a removed parameter is dropped in place, a named
    /// argument is renamed in place, and only positional arguments move, to the new positions
    /// of their parameters. A new parameter with a default value is left out (unless
    /// <paramref name="passNewOptional"/>), so the arguments after it are named. A parameter the
    /// call does not pass and has to gets an argument: one that lost its default value gets that
    /// value (from <paramref name="oldDefaults"/>), a new one without a default value a
    /// <c>TODO</c> placeholder; positional where every argument before it is positional and in
    /// position, otherwise named at the end. From the first argument out of
    /// position on, every argument is named (CS8323); expanded <c>params</c> arguments that
    /// would need a name are passed as one collection made by <paramref name="collect"/>. Null
    /// when the call cannot be rewritten (an argument names no parameter, the receiver of an
    /// extension-form call moves, or expanded <c>params</c> arguments would need a name and
    /// <paramref name="collect"/> is null).
    /// </summary>
    private static ArgumentListSyntax? BuildArgumentList(
        ArgumentListSyntax original,
        IReadOnlyList<IParameterSymbol> parameters,
        int offset,
        IReadOnlyList<NewParameter> newParameters,
        IReadOnlyList<string?> renames,
        IReadOnlyDictionary<int, ExpressionSyntax> oldDefaults,
        bool passNewOptional,
        Func<IReadOnlyList<ExpressionSyntax>, ExpressionSyntax>? collect)
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

            var argument = parameter.NewDefault != null
                ? SyntaxFactory.Argument(SyntaxFactory.ParseExpression(parameter.NewDefault))
                : parameter.OriginalIndex is { } kept && oldDefaults.TryGetValue(kept, out var oldDefault)
                    ? SyntaxFactory.Argument(oldDefault)
                    : SyntaxFactory.Argument(SyntaxFactory.ParseExpression($"default {PlaceholderPrefix}{NameAt(position)}{PlaceholderSuffix}"));
            var inPositionPrefix = entries.TakeWhile((e, i) => e.Argument.NameColon == null && e.Position == i).Count();
            entries.Insert(position <= inPositionPrefix ? position : entries.Count, new CallArgument(argument, position, IsNew: true));
        }

        var arguments = new List<ArgumentSyntax>();
        var nameTheRest = false;
        var seenNamed = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var (argument, position, isNew) = entries[i];

            // A new argument is laid out like the call's last one (its own line, indented, in
            // a call that puts each argument on a line), or like the first when it comes first.
            if (isNew && original.Arguments.Count > 0)
                argument = argument.WithLeadingTrivia(original.Arguments[i == 0 ? 0 : ^1].GetLeadingTrivia());

            var expandedParams = (i > 0 && entries[i - 1].Position == position) ||
                (i + 1 < entries.Count && entries[i + 1].Position == position);
            var inPosition = position == i || (i > 0 && entries[i - 1].Position == position);

            if (argument.NameColon == null && (nameTheRest || !inPosition || (isNew && seenNamed)))
            {
                if (expandedParams)
                {
                    // Expanded params arguments can only be named as one collection.
                    if (collect == null)
                        return null;

                    var expanded = entries.Skip(i).TakeWhile(e => e.Position == position).Select(e => e.Argument.Expression).ToList();
                    argument = SyntaxFactory.Argument(collect(expanded)).WithLeadingTrivia(argument.GetLeadingTrivia());
                    i += expanded.Count - 1;
                }

                argument = WithName(argument, NameAt(position));
            }

            nameTheRest |= !inPosition;
            seenNamed |= argument.NameColon != null;
            arguments.Add(argument);
        }

        return original.WithArguments(SyntaxFactory.SeparatedList(arguments, Separators(original.Arguments, arguments.Count)));
    }

    /// <summary>
    /// The <c>TODO</c> placeholders the calls now pass, as <c>path(line,column): TODO: ...</c>:
    /// the <c>default /* TODO: name */</c> arguments of each file that it did not have before.
    /// They are found by their text, in the document of every target framework (a file compiled
    /// for several is merged into new text, with placeholders in each one's <c>#if</c> branches).
    /// </summary>
    private static async Task<List<string>> FindPlaceholdersAsync(
        Solution before,
        Solution after,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var placeholders = new List<(string Path, int Line, int Column, string Text)>();
        foreach (var id in documents)
        {
            var existing = (await PlaceholdersAsync(before, id, cancellationToken)).Values.Select(p => p.Name).ToList();
            foreach (var (argument, name) in (await PlaceholdersAsync(after, id, cancellationToken)).Values.OrderBy(p => p.Argument.SpanStart))
            {
                if (existing.Remove(name))
                    continue;

                var span = argument.GetLocation().GetLineSpan();
                var (line, column) = (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
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

    /// <summary>
    /// The placeholder arguments in the file of document <paramref name="id"/>, by position,
    /// in the documents of all its target frameworks; none when the solution lacks the document.
    /// </summary>
    private static async Task<Dictionary<int, (ArgumentSyntax Argument, string Name)>> PlaceholdersAsync(
        Solution solution,
        DocumentId id,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<int, (ArgumentSyntax, string)>();
        if (solution.GetDocument(id) is not { } document)
            return found;

        var ids = document.FilePath == null ? [id] : solution.GetDocumentIdsWithFilePath(document.FilePath);
        foreach (var linked in ids)
        {
            if (await solution.GetDocument(linked)!.GetSyntaxRootAsync(cancellationToken) is not { } root)
                continue;

            foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (argument.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression } literal &&
                    literal.Token.TrailingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.MultiLineCommentTrivia)).ToString() is var comment &&
                    comment.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) &&
                    comment.EndsWith(PlaceholderSuffix, StringComparison.Ordinal))
                {
                    found.TryAdd(argument.SpanStart, (argument, comment[PlaceholderPrefix.Length..^PlaceholderSuffix.Length]));
                }
            }
        }

        return found;
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
