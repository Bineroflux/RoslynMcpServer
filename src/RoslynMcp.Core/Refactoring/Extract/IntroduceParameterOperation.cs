using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Promotes a local variable to a method parameter and updates call sites.
/// </summary>
/// <remarks>
/// <para>
/// The local's declaration is removed and the method gets a parameter of its type, before its
/// optional and <c>params</c> parameters. Every call, in any project, passes the local's
/// initializer, with the method's parameters in it replaced by the call's arguments and the
/// names in it written as they bind at the call. The argument is positional where the call's
/// arguments before it are, named otherwise.
/// </para>
/// <para>
/// A call where the initializer cannot be written (it reads something the call cannot reach,
/// an argument it needs would be evaluated twice, it uses the instance and the call's receiver
/// is not a plain variable) passes a <c>default</c> placeholder, listed in the result's
/// <c>warnings</c>, as are method groups and implicit uses that cannot pass the argument, and
/// the compiler errors the change leaves. Refused: a method that overrides, implements or is
/// overridden (change the whole family with change_signature), a partial method, a local without
/// an initializer, a <c>const</c>, <c>ref</c> or <c>using</c> local, one declared in a loop or a
/// nested function, and an initializer that reads other locals or awaits.
/// </para>
/// </remarks>
public sealed class IntroduceParameterOperation : RefactoringOperationBase<IntroduceParameterParams>
{
    private const string PlaceholderAnnotationKind = "RoslynMcp.IntroduceParameter.Placeholder";

    /// <inheritdoc />
    public IntroduceParameterOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(IntroduceParameterParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.VariableName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "variableName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (@params.Line < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        IntroduceParameterParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        // Find the local variable declaration at the specified line
        var targetLine = @params.Line - 1; // 0-based
        var localDecl = root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Where(l => l.GetLocation().GetLineSpan().StartLinePosition.Line == targetLine)
            .SelectMany(l => l.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.ValueText == IdentifierNames.ValueText(@params.VariableName));

        if (localDecl == null)
        {
            throw new RefactoringException(ErrorCodes.SymbolNotFound,
                $"Local variable '{@params.VariableName}' not found at line {@params.Line}.");
        }

        var declarationStatement = (LocalDeclarationStatementSyntax)localDecl.Parent!.Parent!;
        var containingMethod = CheckDeclaration(localDecl, declarationStatement);

        var methodSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");
        var local = semanticModel.GetDeclaredSymbol(localDecl, cancellationToken) as ILocalSymbol
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve the local variable.");
        var solution = Context.Solution;

        await CheckMethodAsync(methodSymbol, semanticModel.Compilation, solution, cancellationToken);
        CheckNameIsFree(local, containingMethod, semanticModel, cancellationToken);

        var initializer = localDecl.Initializer?.Value ?? throw new RefactoringException(
            ErrorCodes.CannotConvert,
            $"'{local.Name}' has no initializer, so the calls would have no value to pass; the method assigns it before using it.");
        var portable = PortableInitializer.Create(initializer, methodSymbol, semanticModel, cancellationToken);

        var paramType = ParameterType(localDecl, declarationStatement, containingMethod, semanticModel, cancellationToken);
        var index = methodSymbol.Parameters.TakeWhile(p => !p.IsOptional && !p.IsParams).Count();

        // The edits are collected on the solution as it is, so the references found are the
        // calls of the method as it is declared now.
        var edits = new SyntaxEdits();
        var newParameter = SyntaxFactory.Parameter(IdentifierNames.Token(local.Name))
            .WithType(paramType.WithoutTrivia().WithTrailingTrivia(SyntaxFactory.Space));
        edits.Add(document.Id, containingMethod.ParameterList, rewritten =>
        {
            var list = (ParameterListSyntax)rewritten;
            return list.WithParameters(Insert(list.Parameters, index, newParameter));
        });
        AddRemoval(edits, document.Id, localDecl, declarationStatement);

        var warnings = new List<string>();
        var callSites = new Dictionary<DocumentId, int>();
        var references = (await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken))
            .SelectMany(r => r.Locations)
            .OrderBy(l => l.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Location.SourceSpan.Start)
            .ToList();

        foreach (var reference in references)
        {
            var site = await DescribeReferenceAsync(reference, cancellationToken);
            switch (site)
            {
                case { Invocation: { } invocation, Model: { } model }:
                    AddCall(edits, reference.Document.Id, invocation, model, methodSymbol, index, local.Name, portable, cancellationToken);
                    callSites[reference.Document.Id] = callSites.GetValueOrDefault(reference.Document.Id) + 1;
                    break;
                case { Problem: { } problem }:
                    warnings.Add(problem);
                    break;
            }
        }

        var newSolution = await edits.ApplyAsync(solution, cancellationToken);
        var affected = edits.Documents.Concat(references.Select(r => r.Document.Id)).Distinct().ToList();
        warnings.AddRange(await FindPlaceholdersAsync(newSolution, callSites.Keys, cancellationToken));
        warnings.AddRange(await IntroducedErrors.FindAsync(solution, newSolution, affected, cancellationToken, matchLines: false) ?? []);

        var newSignature = containingMethod.Identifier.Text +
            (await newSolution.GetDocument(document.Id)!.GetSyntaxRootAsync(cancellationToken))!
                .DescendantNodes().OfType<MethodDeclarationSyntax>()
                .First(m => m.SpanStart == containingMethod.SpanStart).ParameterList.ToString();

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Promote '{local.Name}' to parameter of '{containingMethod.Identifier.Text}'",
                    BeforeSnippet = containingMethod.Identifier.Text + containingMethod.ParameterList,
                    AfterSnippet = newSignature
                }
            };

            pendingChanges.AddRange(callSites.Select(entry => new PendingChange
            {
                File = solution.GetDocument(entry.Key)!.FilePath!,
                ChangeType = ChangeKind.Modify,
                Description = $"{entry.Value} call(s) of '{containingMethod.Identifier.Text}' pass '{local.Name}'"
            }));

            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = pendingChanges,
                Warnings = warnings.Count > 0 ? warnings : null
            };
        }

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
                Name = containingMethod.Identifier.Text,
                FullyQualifiedName = $"{methodSymbol.ContainingType.ToDisplayString()}.{containingMethod.Identifier.Text}",
                Kind = Contracts.Enums.SymbolKind.Method
            },
            ReferencesUpdated = callSites.Values.Sum(),
            Warnings = warnings.Count > 0 ? warnings : null
        };
    }

    /// <summary>
    /// The method the local belongs to; refuses locals that cannot become a parameter.
    /// </summary>
    private static MethodDeclarationSyntax CheckDeclaration(VariableDeclaratorSyntax local, LocalDeclarationStatementSyntax statement)
    {
        var name = local.Identifier.ValueText;
        if (statement.IsConst)
            throw new RefactoringException(ErrorCodes.CannotConvert, $"'{name}' is a constant; its uses may need a constant, which a parameter is not.");

        if (!statement.UsingKeyword.IsKind(SyntaxKind.None))
            throw new RefactoringException(ErrorCodes.CannotConvert, $"'{name}' is a using declaration; as a parameter it would no longer be disposed.");

        if (statement.Declaration.Type is RefTypeSyntax)
            throw new RefactoringException(ErrorCodes.CannotConvert, $"'{name}' is a ref local, which a parameter cannot replace.");

        foreach (var ancestor in statement.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method;
                case ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                    throw new RefactoringException(
                        ErrorCodes.CannotConvert,
                        $"'{name}' is declared in a loop and starts over on every iteration; a parameter would keep its value.");
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax:
                    throw new RefactoringException(
                        ErrorCodes.CannotConvert,
                        $"'{name}' is declared in a lambda or local function; only a local of the method itself can become its parameter.");
                case MemberDeclarationSyntax:
                    throw new RefactoringException(
                        ErrorCodes.CannotConvert,
                        "Local variable must be inside a method to be promoted to a parameter.");
            }
        }

        throw new RefactoringException(ErrorCodes.CannotConvert, "Local variable must be inside a method to be promoted to a parameter.");
    }

    /// <summary>
    /// Refuses a method whose signature cannot change on its own: one of an override /
    /// implementation family, or a partial method.
    /// </summary>
    private static async Task CheckMethodAsync(IMethodSymbol method, Compilation compilation, Solution solution, CancellationToken cancellationToken)
    {
        if (method.PartialDefinitionPart != null || method.PartialImplementationPart != null)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"'{method.ToDisplayString()}' is a partial method; give both parts the parameter with change_signature instead.");
        }

        var related = await MemberFamily.FindRelatedAsync(method, compilation, solution, cancellationToken);
        if (related.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberInFamily,
                $"'{method.ToDisplayString()}' must keep the signature of {string.Join(", ", related.Select(m => $"'{m.ToDisplayString()}'").Order(StringComparer.Ordinal))} " +
                "(it overrides, implements or is overridden by them). Add the parameter to the whole family with change_signature, then assign the local's value to it.",
                new Dictionary<string, object> { ["family"] = related.Select(m => m.ToDisplayString()).Order(StringComparer.Ordinal).ToList() });
        }
    }

    /// <summary>Refuses a name that another local, parameter or type parameter of the method already has.</summary>
    private static void CheckNameIsFree(ILocalSymbol local, MethodDeclarationSyntax method, SemanticModel model, CancellationToken cancellationToken)
    {
        var clash = method.DescendantNodes()
            .Select(node => node is ParameterSyntax or VariableDeclaratorSyntax or SingleVariableDesignationSyntax or CatchDeclarationSyntax
                    or CommonForEachStatementSyntax or LocalFunctionStatementSyntax or TypeParameterSyntax or QueryClauseSyntax or FromClauseSyntax
                ? model.GetDeclaredSymbol(node, cancellationToken)
                : null)
            .FirstOrDefault(symbol => symbol != null && symbol.Name == local.Name && !SymbolEqualityComparer.Default.Equals(symbol, local));

        if (clash != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"The method already declares another {clash.Kind.ToString().ToLowerInvariant()} named '{local.Name}', which the parameter would clash with (CS0136).");
        }
    }

    /// <summary>The parameter's type: the declared one, or a var local's initializer type written like generated code.</summary>
    private static TypeSyntax ParameterType(
        VariableDeclaratorSyntax localDecl,
        LocalDeclarationStatementSyntax declarationStatement,
        MethodDeclarationSyntax containingMethod,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var declaredType = declarationStatement.Declaration.Type;
        if (!declaredType.IsVar)
            return declaredType;

        // A `var` local is always nullable, so `new List<int>()` would become `List<int>?`: take
        // the initializer's type, with `?` only where nullable annotations are enabled (CS8632),
        // simplified to what the file's usings allow.
        var type = (localDecl.Initializer is { } varInitializer
                ? semanticModel.GetTypeInfo(varInitializer.Value, cancellationToken).Type
                : null)
            ?? semanticModel.GetTypeInfo(declaredType, cancellationToken).Type;

        if (type is null or { IsAnonymousType: true })
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"The type of '{localDecl.Identifier.ValueText}' cannot be written, so no parameter can have it.");
        }

        return SyntaxGenerationHelper.CreateTypeSyntax(
            type,
            semanticModel.GetNullableContext(containingMethod.ParameterList.SpanStart).AnnotationsEnabled());
    }

    /// <summary>Removes the declarator, or its whole statement when it declares nothing else.</summary>
    private static void AddRemoval(SyntaxEdits edits, DocumentId document, VariableDeclaratorSyntax local, LocalDeclarationStatementSyntax statement)
    {
        if (statement.Declaration.Variables.Count > 1)
        {
            var position = statement.Declaration.Variables.IndexOf(local);
            edits.Add(document, statement, rewritten =>
            {
                var declaration = ((LocalDeclarationStatementSyntax)rewritten).Declaration;
                return ((LocalDeclarationStatementSyntax)rewritten).WithDeclaration(
                    declaration.WithVariables(declaration.Variables.RemoveAt(position)));
            });
            return;
        }

        switch (statement.Parent)
        {
            case BlockSyntax block:
                {
                    var position = block.Statements.IndexOf(statement);
                    edits.Add(document, block, rewritten =>
                    {
                        var list = (BlockSyntax)rewritten;
                        return list.WithStatements(list.Statements.RemoveAt(position));
                    });
                    break;
                }
            case SwitchSectionSyntax section:
                {
                    var position = section.Statements.IndexOf(statement);
                    edits.Add(document, section, rewritten =>
                    {
                        var list = (SwitchSectionSyntax)rewritten;
                        return list.WithStatements(list.Statements.RemoveAt(position));
                    });
                    break;
                }
            default:
                throw new RefactoringException(ErrorCodes.CannotConvert, $"The declaration of '{local.Identifier.ValueText}' is not in a block.");
        }
    }

    /// <summary>A reference to the method: a call to rewrite, or a use that cannot pass the argument.</summary>
    private sealed record ReferenceSite(InvocationExpressionSyntax? Invocation, SemanticModel? Model, string? Problem);

    private static async Task<ReferenceSite?> DescribeReferenceAsync(ReferenceLocation reference, CancellationToken cancellationToken)
    {
        var span = reference.Location.GetLineSpan();
        var where = $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
        if (reference.IsImplicit)
            return new ReferenceSite(null, null, $"{where}: the method is used implicitly here (e.g. by a collection initializer, foreach or deconstruction), which cannot pass the new parameter.");

        var root = await reference.Document.GetSyntaxRootAsync(cancellationToken);
        var model = await reference.Document.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
            return null;

        var node = root.FindNode(reference.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);
        if (node.IsPartOfStructuredTrivia() || node.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsNameOf))
            return null; // a cref or nameof names the method without calling it

        SyntaxNode callee = node;
        if (callee.Parent is MemberAccessExpressionSyntax access && access.Name == callee ||
            callee.Parent is MemberBindingExpressionSyntax)
        {
            callee = callee.Parent;
        }

        return callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee
            ? new ReferenceSite(invocation, model, null)
            : new ReferenceSite(null, null, $"{where}: '{node}' is used as a method group here; it was not changed and now lacks the new parameter.");
    }

    private static bool IsNameOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" };

    /// <summary>Adds the argument to one call: positional where the arguments before it are, named otherwise.</summary>
    private static void AddCall(
        SyntaxEdits edits,
        DocumentId document,
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        IMethodSymbol method,
        int index,
        string name,
        PortableInitializer portable,
        CancellationToken cancellationToken)
    {
        var value = portable.WriteFor(invocation, model, cancellationToken, out var problem);
        if (value == null)
        {
            value = SyntaxFactory.ParseExpression($"default /* TODO: {name} */")
                .WithAdditionalAnnotations(new SyntaxAnnotation(PlaceholderAnnotationKind, $"{name}|{problem}"));
        }

        // A call in extension form passes the `this` parameter as its receiver.
        var invoked = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
        var offset = invoked?.ReducedFrom != null ? 1 : 0;
        var position = index - offset;
        var arguments = invocation.ArgumentList.Arguments;

        // Positional at its place when every argument before it is positional or named in
        // position; named at the end otherwise (and then the arguments after it keep binding by name).
        var inPosition = arguments.Count >= position && arguments.Take(position).Select((a, i) =>
            a.NameColon == null || a.NameColon.Name.Identifier.ValueText == method.Parameters[i + offset].Name).All(ok => ok);

        var argument = inPosition
            ? SyntaxFactory.Argument(value)
            : SyntaxFactory.Argument(
                SyntaxFactory.NameColon(IdentifierNames.Name(name), SyntaxFactory.Token(SyntaxKind.ColonToken).WithTrailingTrivia(SyntaxFactory.Space)),
                default,
                value);

        edits.Add(document, invocation.ArgumentList, rewritten =>
        {
            var list = (ArgumentListSyntax)rewritten;
            return list.WithArguments(Insert(list.Arguments, inPosition ? position : list.Arguments.Count, argument));
        });
    }

    /// <summary>
    /// <paramref name="list"/> with <paramref name="item"/> at <paramref name="index"/>; the new
    /// separator copies a neighbouring one (or is <c>", "</c>), and the item takes the leading
    /// trivia of the item it goes before (or after), so one-per-line lists stay that way.
    /// </summary>
    private static SeparatedSyntaxList<T> Insert<T>(SeparatedSyntaxList<T> list, int index, T item)
        where T : SyntaxNode
    {
        var nodes = list.ToList();
        var separators = Enumerable.Range(0, list.SeparatorCount).Select(list.GetSeparator).ToList();
        var newSeparator = list.SeparatorCount > 0
            ? list.GetSeparator(Math.Min(index, list.SeparatorCount - 1))
            : SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space);

        if (nodes.Count > 0)
            item = item.WithLeadingTrivia(nodes[Math.Min(index, nodes.Count - 1)].GetLeadingTrivia());

        nodes.Insert(index, item);
        if (nodes.Count > 1)
            separators.Insert(Math.Min(index, separators.Count), newSeparator);

        return SyntaxFactory.SeparatedList(nodes, separators);
    }

    /// <summary>The placeholders the calls pass, as <c>path(line,column): TODO: ...</c>.</summary>
    private static async Task<List<string>> FindPlaceholdersAsync(
        Solution solution,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var placeholders = new List<string>();
        foreach (var id in documents)
        {
            if (await solution.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken) is not { } root)
                continue;

            foreach (var argument in root.GetAnnotatedNodes(PlaceholderAnnotationKind))
            {
                var span = argument.GetLocation().GetLineSpan();
                var data = argument.GetAnnotations(PlaceholderAnnotationKind).First().Data!.Split('|', 2);
                placeholders.Add(
                    $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): " +
                    $"TODO: replace the placeholder 'default' passed for parameter '{data[0]}': {data[1]}");
            }
        }

        return placeholders;
    }

    /// <summary>
    /// The local's initializer, written for one call: types and static members fully qualified
    /// (and simplified again where they are written), instance members on the instance the
    /// method is called on, and the method's parameters and type parameters replaced by what
    /// the call passes.
    /// </summary>
    private sealed class PortableInitializer
    {
        private readonly ExpressionSyntax _initializer;
        private readonly SemanticModel _model;
        private readonly IMethodSymbol _method;
        private readonly IReadOnlyList<ISymbol> _usedSymbols;
        private readonly bool _nullable;

        private PortableInitializer(ExpressionSyntax initializer, SemanticModel model, IMethodSymbol method, IReadOnlyList<ISymbol> usedSymbols)
        {
            _initializer = initializer;
            _model = model;
            _method = method;
            _usedSymbols = usedSymbols;
            _nullable = model.GetNullableContext(initializer.SpanStart).AnnotationsEnabled();
        }

        /// <summary>Refuses an initializer no call could pass: one that reads other locals or awaits.</summary>
        public static PortableInitializer Create(
            ExpressionSyntax initializer,
            IMethodSymbol method,
            SemanticModel model,
            CancellationToken cancellationToken)
        {
            var operation = model.GetOperation(initializer, cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze the initializer.");
            var operations = operation.DescendantsAndSelf().ToList();

            if (operations.OfType<ILocalReferenceOperation>().FirstOrDefault(l => !initializer.Span.Contains(l.Local.Locations[0].SourceSpan)) is { } localReference)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"The initializer reads the local '{localReference.Local.Name}', which the callers do not have; only an initializer of parameters, members and constants can move to the calls.");
            }

            if (operations.Any(o => o is IAwaitOperation))
                throw new RefactoringException(ErrorCodes.CannotConvert, "The initializer awaits, which a call's argument cannot do in every caller.");

            var used = operations.SelectMany(o => o switch
            {
                IMemberReferenceOperation member => [member.Member],
                IInvocationOperation call => [call.TargetMethod],
                IObjectCreationOperation creation when creation.Constructor != null => new ISymbol[] { creation.Constructor },
                _ => []
            }).Distinct(SymbolEqualityComparer.Default).ToList();

            return new PortableInitializer(initializer, model, method, used);
        }

        /// <summary>The initializer as the call passes it, or null with the reason it cannot be written there.</summary>
        public ExpressionSyntax? WriteFor(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken, out string? problem)
        {
            problem = null;
            if (_usedSymbols.FirstOrDefault(s => !model.IsAccessible(invocation.SpanStart, s)) is { } inaccessible)
            {
                problem = $"the initializer uses '{inaccessible.ToDisplayString()}', which is not accessible here";
                return null;
            }

            if (model.GetOperation(invocation, cancellationToken) is not IInvocationOperation call)
            {
                problem = "the call could not be analyzed";
                return null;
            }

            // The type arguments of the call, by the names of the type parameters they stand for.
            var callNullable = model.GetNullableContext(invocation.SpanStart).AnnotationsEnabled();
            var typeNames = new Dictionary<string, TypeSyntax>(StringComparer.Ordinal);
            foreach (var (parameters, arguments) in new[]
                     {
                         (_method.ContainingType.TypeParameters, call.TargetMethod.ContainingType.TypeArguments),
                         (_method.TypeParameters, call.TargetMethod.TypeArguments)
                     })
            {
                foreach (var (parameter, argument) in parameters.Zip(arguments))
                {
                    if (argument.IsAnonymousType)
                    {
                        problem = $"the type argument for '{parameter.Name}' is an anonymous type, which cannot be written";
                        return null;
                    }

                    typeNames[parameter.Name] = SyntaxGenerationHelper.CreateTypeSyntax(argument, callNullable);
                }
            }

            // `this` in the initializer is the instance the method is called on: the caller's own
            // for M(...), this.M(...) and base.M(...), the receiver for x.M(...).
            ExpressionSyntax? receiver = null;
            var receiverIsPlain = true;
            if (invocation.Expression is MemberAccessExpressionSyntax { Expression: not (ThisExpressionSyntax or BaseExpressionSyntax) } access)
            {
                receiver = access.Expression.WithoutTrivia();
                receiverIsPlain = IsPlain(access.Expression);
            }
            else if (invocation.Expression is MemberBindingExpressionSyntax)
            {
                receiverIsPlain = false;
            }

            string? failure = null;
            ExpressionSyntax Instance(SyntaxNode original)
            {
                if (!receiverIsPlain)
                    failure ??= "the initializer uses the instance, and the call's receiver is not a plain variable that could be named again";
                return receiver == null ? SyntaxFactory.ThisExpression().WithTriviaFrom(original) : Parenthesize(receiver).WithTriviaFrom(original);
            }

            var names = _initializer.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Where(IsStandalone).Cast<SyntaxNode>();
            var selves = _initializer.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>();
            var written = _initializer.ReplaceNodes(names.Concat(selves), (original, rewritten) =>
            {
                if (original is ThisExpressionSyntax)
                    return Instance(original);

                var name = (SimpleNameSyntax)original;
                switch (_model.GetSymbolInfo(name, cancellationToken).Symbol)
                {
                    case IParameterSymbol parameter when SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, _method):
                        var argument = call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == parameter.Ordinal);
                        var passed = argument switch
                        {
                            { ArgumentKind: ArgumentKind.Explicit, Syntax: ArgumentSyntax { Expression: var expression } } => expression,
                            { ArgumentKind: ArgumentKind.Explicit, Syntax: ExpressionSyntax extensionReceiver } => extensionReceiver,
                            { ArgumentKind: ArgumentKind.DefaultValue } => DefaultValueOf(parameter, cancellationToken),
                            _ => null
                        };

                        if (passed == null || !IsPlain(passed))
                        {
                            failure ??= $"the initializer uses '{parameter.Name}', and the call passes '{argument?.Syntax.ToString() ?? "nothing"}' for it, which the new argument would evaluate again";
                            return rewritten;
                        }

                        return Parenthesize(passed.WithoutTrivia()).WithTriviaFrom(rewritten);

                    case ITypeParameterSymbol typeParameter when typeNames.TryGetValue(typeParameter.Name, out var typeArgument):
                        return typeArgument.WithTriviaFrom(rewritten);

                    case INamedTypeSymbol type when !IsInNameOf(name):
                        return Substitute(SyntaxGenerationHelper.CreateTypeSyntax(type, _nullable), typeNames).WithTriviaFrom(rewritten);

                    case IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary } when !IsInNameOf(name):
                        var member = _model.GetSymbolInfo(name, cancellationToken).Symbol!;
                        var memberReceiver = member.IsStatic
                            ? Substitute(SyntaxGenerationHelper.CreateTypeSyntax(member.ContainingType, _nullable), typeNames)
                            : Instance(original).WithoutTrivia();
                        return SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression, memberReceiver, (SimpleNameSyntax)rewritten.WithoutTrivia())
                            .WithAdditionalAnnotations(Simplifier.Annotation)
                            .WithTriviaFrom(rewritten);

                    default:
                        return rewritten;
                }
            });

            if (failure != null)
            {
                problem = failure;
                return null;
            }

            return written.WithoutTrivia().WithAdditionalAnnotations(Simplifier.Annotation);
        }

        /// <summary>A written type with the type parameters it names replaced by the call's type arguments.</summary>
        private static TypeSyntax Substitute(TypeSyntax type, IReadOnlyDictionary<string, TypeSyntax> typeNames) =>
            type.ReplaceNodes(
                type.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Where(n =>
                    n.Parent is not (QualifiedNameSyntax or AliasQualifiedNameSyntax) && typeNames.ContainsKey(n.Identifier.ValueText)),
                (original, _) => typeNames[original.Identifier.ValueText].WithTriviaFrom(original));

        private static bool IsInNameOf(SyntaxNode node) =>
            node.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsNameOf);

        /// <summary>A name on its own, not the member part of an access or a qualified name.</summary>
        private static bool IsStandalone(SimpleNameSyntax name) => name.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Expression == name,
            QualifiedNameSyntax or AliasQualifiedNameSyntax or NameColonSyntax or NameEqualsSyntax or MemberBindingExpressionSyntax => false,
            _ => name is not IdentifierNameSyntax { Identifier.ValueText: "nameof" }
        };

        /// <summary>An expression that can be evaluated once more without changing anything: names, literals, member accesses of them.</summary>
        private static bool IsPlain(ExpressionSyntax expression) => expression switch
        {
            LiteralExpressionSyntax or IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax or
                DefaultExpressionSyntax or TypeOfExpressionSyntax or PredefinedTypeSyntax => true,
            MemberAccessExpressionSyntax access => IsPlain(access.Expression),
            ParenthesizedExpressionSyntax parenthesized => IsPlain(parenthesized.Expression),
            PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression) || unary.IsKind(SyntaxKind.UnaryPlusExpression) => IsPlain(unary.Operand),
            CastExpressionSyntax cast => IsPlain(cast.Expression),
            InvocationExpressionSyntax invocation => IsNameOf(invocation),
            _ => false
        };

        private static ExpressionSyntax Parenthesize(ExpressionSyntax expression) =>
            SyntaxFactory.ParenthesizedExpression(expression).WithAdditionalAnnotations(Simplifier.Annotation);

        private static ExpressionSyntax? DefaultValueOf(IParameterSymbol parameter, CancellationToken cancellationToken) =>
            parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is ParameterSyntax { Default.Value: { } value }
                ? value.WithoutTrivia()
                : null;
    }
}
