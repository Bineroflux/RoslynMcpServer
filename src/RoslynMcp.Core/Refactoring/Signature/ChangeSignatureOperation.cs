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
/// Parameters correspond by position across the family, since an override may name them
/// differently. A rename in the request renames the parameter at that position in every member
/// (and its uses in the body and named arguments at the calls); otherwise each member keeps its
/// own name. A kept parameter keeps its own type, modifiers and attributes unless the request
/// gives a type. The compiler errors the change leaves in the affected documents are reported
/// as warnings.
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
            if (string.IsNullOrWhiteSpace(change.Name))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Each parameter change requires a name.");

            if (change.OriginalName == null && !change.Remove && string.IsNullOrWhiteSpace(change.Type))
                throw new RefactoringException(ErrorCodes.MissingRequiredParam, "New parameters require a type.");

            if (!change.Remove)
                IdentifierNames.Validate(change.Name, NameKind.Parameter, $"parameters[{i}].name", ErrorCodes.InvalidNewName);
        }
    }

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
        var newParameters = BuildNewParameterList(originalParams, @params.Parameters);

        // A rename applies to the parameter at that position in every member of the family.
        var renames = new string?[originalParams.Count];
        foreach (var parameter in newParameters.Where(p => p.OriginalIndex.HasValue))
        {
            var index = parameter.OriginalIndex!.Value;
            if (IdentifierNames.ValueText(parameter.Name) != originalParams[index].Name)
                renames[index] = parameter.Name;
        }

        // Types the request writes, bound where the method is so each member can be given the
        // same type. A type that does not resolve is written as given, everywhere.
        var typePosition = methodDecl.ParameterList.OpenParenToken.Span.End;
        var boundTypes = newParameters.ToDictionary(
            p => p,
            p => p.Type == null ? null : SignatureSyntax.BindType(semanticModel, typePosition, p.Type));

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

    private static List<NewParameter> BuildNewParameterList(
        List<IParameterSymbol> originalParams,
        IReadOnlyList<ParameterChange> changes)
    {
        var result = new List<NewParameter>();

        // First pass: handle existing parameters and removals
        foreach (var change in changes.Where(c => c.OriginalName != null))
        {
            if (change.Remove) continue;

            var index = originalParams.FindIndex(p => p.Name == IdentifierNames.ValueText(change.OriginalName!));
            if (index < 0)
            {
                throw new RefactoringException(
                    ErrorCodes.ParameterNotFound,
                    $"Parameter '{change.OriginalName}' not found in method.");
            }

            result.Add(new NewParameter
            {
                Name = change.Name,
                Type = change.Type,
                DefaultValue = change.DefaultValue,
                Position = change.NewPosition,
                OriginalIndex = index
            });
        }

        // Second pass: add new parameters
        foreach (var change in changes.Where(c => c.OriginalName == null && !c.Remove))
        {
            result.Add(new NewParameter
            {
                Name = change.Name,
                Type = change.Type!,
                DefaultValue = change.DefaultValue,
                Position = change.NewPosition,
                OriginalIndex = null
            });
        }

        // Sort by position
        if (result.Any(p => p.Position.HasValue))
        {
            result = result
                .OrderBy(p => p.Position ?? int.MaxValue)
                .ThenBy(p => result.IndexOf(p))
                .ToList();
        }

        return result;
    }

    /// <summary>
    /// The new parameter list of one family member. A kept parameter keeps its own declaration
    /// (name, type, modifiers, attributes) except for what the request changes; its default
    /// value is the request's, as before. An explicit interface implementation gets no default
    /// values, which would have no effect there (CS1066).
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

            parameters.Add(WithDefault(parameter, isExplicitImplementation ? null : newParameter.DefaultValue));
        }

        return original.WithParameters(SyntaxFactory.SeparatedList(parameters, Separators(original.Parameters, parameters.Count)));
    }

    private static ParameterSyntax WithDefault(ParameterSyntax parameter, string? defaultValue)
    {
        if (string.IsNullOrEmpty(defaultValue))
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

        return edits.Add(location.Document.Id, original, rewritten =>
            BuildArgumentList((ArgumentListSyntax)rewritten, parameters, offset, newParameters, renames) ?? rewritten);
    }

    /// <summary>
    /// The new arguments of one call. The call keeps its own argument order, so its arguments
    /// are evaluated as before: an argument of a removed parameter is dropped in place, a named
    /// argument is renamed in place, and only positional arguments move, to the new positions
    /// of their parameters. A parameter the call does not pass and has to (a new one, or one
    /// that lost its default value) gets the request's default value or a <c>TODO</c>
    /// placeholder: positional where every argument before it is positional and in position,
    /// otherwise named at the end. From the first argument out of position on, every argument
    /// is named (CS8323). Null when the call cannot be rewritten (an argument names no
    /// parameter, the receiver of an extension-form call moves, or an expanded <c>params</c>
    /// argument would need a name).
    /// </summary>
    private static ArgumentListSyntax? BuildArgumentList(
        ArgumentListSyntax original,
        IReadOnlyList<IParameterSymbol> parameters,
        int offset,
        IReadOnlyList<NewParameter> newParameters,
        IReadOnlyList<string?> renames)
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
            if (parameter.OriginalIndex is { } index &&
                (passed.Contains(index) || !string.IsNullOrEmpty(parameter.DefaultValue) || parameters[index - offset].IsParams))
            {
                continue; // passed, or still optional
            }

            var argument = string.IsNullOrEmpty(parameter.DefaultValue)
                ? SyntaxFactory.Argument(SyntaxFactory.ParseExpression($"default /* TODO: {NameAt(position)} */"))
                    .WithAdditionalAnnotations(new SyntaxAnnotation(PlaceholderAnnotationKind, NameAt(position)))
                : SyntaxFactory.Argument(SyntaxFactory.ParseExpression(parameter.DefaultValue));
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
        public required string Name { get; init; }

        /// <summary>The type the request gives, or null to keep each member's own.</summary>
        public string? Type { get; init; }

        public string? DefaultValue { get; init; }
        public int? Position { get; init; }

        /// <summary>The position the parameter had, or null for a new one.</summary>
        public int? OriginalIndex { get; init; }
    }
}
