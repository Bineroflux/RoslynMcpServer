using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts a synchronous method into an async one. The method is renamed first
/// (<c>Load</c> → <c>LoadAsync</c>, with Roslyn's renamer, so every reference follows), then made
/// <c>async</c> with <c>Task</c> / <c>Task&lt;T&gt;</c> as its return type. In its body a task
/// waited for with <c>.Result</c>, <c>.Wait()</c> or <c>.GetAwaiter().GetResult()</c> is awaited
/// instead, and so is a call whose task was discarded (a call used as a statement); lambdas and
/// local functions are not the method and are left alone. A caller that is async (a method, lambda
/// or local function, or top-level statements) awaits its calls; the other callers are left as
/// they are and reported in the warnings, with the compiler errors the change introduces.
/// </summary>
/// <remarks>
/// Refused: a method that is already async, an iterator, a method without a body (abstract,
/// extern, partial), with <c>ref</c> / <c>out</c> / <c>in</c> parameters or a <c>ref</c> return, one
/// that already returns a task, a member of an override / implementation family (virtual,
/// abstract, override, interface implementation), a rename to a name the type already uses, and a
/// method with nothing to await (it would only gain warning CS1998). The program's entry point
/// keeps its name.
/// </remarks>
public sealed class ConvertToAsyncOperation : RefactoringOperationBase<ConvertToAsyncParams>
{
    private const string TasksNamespace = "System.Threading.Tasks";
    private const string MethodAnnotationKind = "RoslynMcp.ConvertToAsync.Method";
    private const string AwaitAnnotationKind = "RoslynMcp.ConvertToAsync.Await";
    private const string NotAwaitedAnnotationKind = "RoslynMcp.ConvertToAsync.NotAwaited";

    /// <summary>
    /// Creates a new convert to async operation.
    /// </summary>
    public ConvertToAsyncOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertToAsyncParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line is < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertToAsyncParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var declaration = SignatureSyntax.SelectDeclaration(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == @params.MethodName).ToList(),
            "method",
            @params.MethodName,
            @params.Line,
            m => m.Identifier,
            ErrorCodes.MethodNotFound);
        var method = model.GetDeclaredSymbol(declaration, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve method symbol.");

        var solution = Context.Solution;
        var tasks = new TaskTypes(model.Compilation);
        CheckMethod(method, declaration, tasks, cancellationToken);
        await CheckFamilyAsync(method, model.Compilation, solution, cancellationToken);
        CheckSomethingToAwait(method, FindBodyAwaits(declaration, model, tasks, cancellationToken));

        var warnings = new List<string>();
        var newName = method.Name;
        if (@params.RenameToAsync && !method.Name.EndsWith("Async", StringComparison.Ordinal))
        {
            if (SymbolEqualityComparer.Default.Equals(model.Compilation.GetEntryPoint(cancellationToken), method))
            {
                warnings.Add($"'{method.Name}' keeps its name: it is the program's entry point.");
            }
            else
            {
                newName = method.Name + "Async";
                CheckNameIsFree(method, newName, model.Compilation);
            }
        }

        // Rename first, on the unchanged solution, so every reference follows; then convert the
        // method found again in the renamed solution.
        var (renamed, methodDeclaration) = newName == method.Name
            ? (solution, declaration)
            : await RenameAsync(solution, document, method, newName, cancellationToken);
        var methodDocument = renamed.GetDocument(document.Id)!;
        var methodModel = await methodDocument.GetSemanticModelAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze file.");
        var renamedMethod = methodModel.GetDeclaredSymbol(methodDeclaration, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve the renamed method.");

        var asyncType = SyntaxGenerationHelper.ToAsyncReturnTypeSymbol(renamedMethod.ReturnType, methodModel.Compilation);
        var returns = asyncType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
        var edits = new SyntaxEdits();

        foreach (var site in FindBodyAwaits(methodDeclaration, methodModel, new TaskTypes(methodModel.Compilation), cancellationToken))
        {
            if (site.Reason is { } reason)
            {
                edits.Add(document.Id, site.Node, rewritten => rewritten.WithAdditionalAnnotations(
                    new SyntaxAnnotation(NotAwaitedAnnotationKind, reason)));
            }
            else
            {
                edits.Add(document.Id, site.Node, rewritten => Await(
                    site.Operand((ExpressionSyntax)rewritten), rewritten, site.Node, site.Description));
            }
        }

        var references = await FindReferencesAsync(renamedMethod, methodDocument, renamed, cancellationToken);
        var awaitedCalls = 0;
        foreach (var (callDocument, invocation) in references.Calls)
        {
            if (WhyCallIsNotAwaited(invocation, methodDeclaration, returns) is { } reason)
            {
                edits.Add(callDocument.Id, invocation, rewritten => rewritten.WithAdditionalAnnotations(
                    new SyntaxAnnotation(NotAwaitedAnnotationKind, reason)));
            }
            else
            {
                edits.Add(callDocument.Id, invocation, rewritten => Await(
                    (ExpressionSyntax)rewritten, rewritten, invocation, $"Await the call to '{newName}'"));
                awaitedCalls++;
            }
        }

        var nullable = methodModel.GetNullableContext(methodDeclaration.ReturnType.SpanStart).AnnotationsEnabled();
        var returnType = SyntaxGenerationHelper.ToAsyncReturnType(renamedMethod.ReturnType, methodModel.Compilation, nullable);
        edits.Add(document.Id, methodDeclaration, rewritten => MakeAsync((MethodDeclarationSyntax)rewritten, returnType)
            .WithAdditionalAnnotations(new SyntaxAnnotation(MethodAnnotationKind)));

        // A file that sees no type named Task gets the using; one that sees another Task keeps the
        // qualified name instead of risking an ambiguity.
        var addUsing = !methodModel.LookupNamespacesAndTypes(methodDeclaration.ReturnType.SpanStart, name: "Task").Any();
        var lineEnding = await LineEndings.GetAsync(methodDocument, cancellationToken);
        var newSolution = await edits.ApplyAsync(
            renamed,
            cancellationToken,
            (id, newRoot) => addUsing && id == document.Id && newRoot is CompilationUnitSyntax unit
                ? UsingDirectives.Add(unit, [TasksNamespace], lineEnding)
                : newRoot);
        newSolution = await LinkedDocuments.SyncAsync(renamed, newSolution, edits.Documents, cancellationToken);

        var affected = new HashSet<DocumentId>(references.Locations.Select(l => l.Document.Id)) { document.Id };
        affected.UnionWith(ChangedDocuments(solution, newSolution));
        if (document.FilePath != null)
            affected.UnionWith(solution.GetDocumentIdsWithFilePath(document.FilePath));

        warnings.AddRange(await NotAwaitedWarningsAsync(renamed, newSolution, edits.Documents, references.Hidden, returns, cancellationToken));
        warnings.AddRange(await IntroducedErrors.FindInProjectsAsync(solution, newSolution, affected, cancellationToken) ?? []);

        var toCommit = await WithTextChangesOnlyAsync(solution, newSolution, cancellationToken);
        if (@params.Preview)
        {
            await PreviewCommitAsync(toCommit, cancellationToken);
            var declarationChange = $"Make '{method.Name}' async" +
                (newName != method.Name ? $" as '{newName}'" : "") + $", returning '{returns}'";
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(
                    solution,
                    newSolution,
                    edits.Documents,
                    declarationChange,
                    newName != method.Name ? $"Rename '{method.Name}' to '{newName}'" : null,
                    cancellationToken),
                Warnings = warnings.Count == 0 ? null : warnings
            };
        }

        var commit = await CommitChangesAsync(toCommit, cancellationToken);

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commit.FilesModified.Distinct(PathResolver.PathComparer).ToList(),
                FilesCreated = commit.FilesCreated,
                FilesDeleted = commit.FilesDeleted
            },
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = newName,
                FullyQualifiedName = renamedMethod.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            ReferencesUpdated = newName != method.Name ? references.Count : awaitedCalls,
            UsingDirectivesAdded = addUsing ? 1 : 0,
            Warnings = warnings.Count == 0 ? null : warnings
        };
    }

    /// <summary>Refuses methods whose own declaration cannot become async.</summary>
    private static void CheckMethod(IMethodSymbol method, MethodDeclarationSyntax declaration, TaskTypes tasks, CancellationToken cancellationToken)
    {
        if (method.IsAsync)
            throw new RefactoringException(ErrorCodes.AlreadyAsync, $"'{method.ToDisplayString()}' is already async.");

        if (method.IsIterator)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvertIterator,
                $"'{method.ToDisplayString()}' is an iterator (it uses yield) and cannot be converted to async.");
        }

        var reason = method switch
        {
            _ when GeneratedCode.IsGenerated(declaration.SyntaxTree, cancellationToken) => "it is declared in generated code",
            { PartialDefinitionPart: not null } or { PartialImplementationPart: not null } =>
                "it is partial, and its declaration and implementation would have to change together",
            _ when declaration.Modifiers.Any(SyntaxKind.PartialKeyword) => "it is partial and has no implementation",
            { IsExtern: true } => "it is extern, so it has no body",
            { IsAbstract: false } when declaration.Body == null && declaration.ExpressionBody == null => "it has no body",
            { ReturnsByRef: true } or { ReturnsByRefReadonly: true } => "it returns by reference, which an async method cannot",
            { IsVararg: true } => "it uses __arglist, which an async method cannot",
            _ when method.Parameters.FirstOrDefault(p => p.RefKind != RefKind.None) is { } parameter =>
                $"its parameter '{parameter.Name}' is {RefKindText(parameter.RefKind)}, and an async method cannot have ref, out or in parameters",
            _ when method.Parameters.FirstOrDefault(p => IsRefLikeOrPointer(p.Type)) is { } parameter =>
                $"its parameter '{parameter.Name}' has type '{parameter.Type.ToDisplayString()}', which an async method cannot take",
            _ when tasks.IsAwaitable(method.ReturnType) =>
                $"it already returns '{method.ReturnType.ToDisplayString()}', which its callers can await as it is",
            _ when IsRefLikeOrPointer(method.ReturnType) =>
                $"its return type '{method.ReturnType.ToDisplayString()}' cannot be a type argument of Task<T>",
            _ when !tasks.Available => "the project does not reference System.Threading.Tasks.Task",
            _ => null
        };

        if (reason != null)
        {
            throw new RefactoringException(
                ErrorCodes.NotConvertibleToAsync,
                $"'{method.ToDisplayString()}' cannot be converted to async: {reason}.");
        }
    }

    private static bool IsRefLikeOrPointer(ITypeSymbol type) =>
        type.IsRefLikeType || type is IPointerTypeSymbol or IFunctionPointerTypeSymbol;

    private static string RefKindText(RefKind refKind) => refKind switch
    {
        RefKind.Out => "out",
        RefKind.In => "in",
        RefKind.RefReadOnlyParameter => "ref readonly",
        _ => "ref"
    };

    /// <summary>
    /// Refuses a member of an override / implementation family: its return type cannot change
    /// alone.
    /// </summary>
    private static async Task CheckFamilyAsync(
        IMethodSymbol method,
        Compilation compilation,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var reason = method switch
        {
            { IsAbstract: true } => "it is abstract",
            { IsVirtual: true } => "it is virtual",
            { IsOverride: true } => $"it overrides '{method.OverriddenMethod?.ToDisplayString()}'",
            _ => null
        };

        if (reason == null && await MemberFamily.FindRelatedAsync(method, compilation, solution, cancellationToken) is { Count: > 0 } related)
        {
            reason = "it implements " + string.Join(", ", related.Select(m => $"'{m.ToDisplayString()}'").Order(StringComparer.Ordinal));
        }

        if (reason != null)
        {
            throw new RefactoringException(
                ErrorCodes.AsyncMemberInFamily,
                $"'{method.ToDisplayString()}' cannot be converted to async on its own: {reason}, and changing the return type " +
                "of one member of an override / implementation family breaks the others. Change the type of the whole family " +
                "with change_return_type instead, then make each body async.");
        }
    }

    /// <summary>Refuses a method with nothing to await: as async, it would only gain warning CS1998.</summary>
    private static void CheckSomethingToAwait(IMethodSymbol method, List<AwaitSite> sites)
    {
        if (sites.Any(s => s.Reason == null))
            return;

        var blocked = sites.Select(s => s.Reason!).Distinct().ToList();
        throw new RefactoringException(
            ErrorCodes.NoAsyncCalls,
            $"'{method.ToDisplayString()}' has nothing to await: no task is waited for with .Result, .Wait() or " +
            ".GetAwaiter().GetResult(), and no call returning a task is used as a statement (lambdas and local functions " +
            "don't count)." +
            (blocked.Count > 0 ? $" Its waits are where await is not allowed: {string.Join("; ", blocked)}." : ""));
    }

    /// <summary>
    /// Refuses a rename to a name that the method's type, or a base type it can see into, already
    /// uses: the renamed method would collide with that member or change which one calls bind to.
    /// </summary>
    private static void CheckNameIsFree(IMethodSymbol method, string newName, Compilation compilation)
    {
        for (var type = method.ContainingType; type != null; type = type.BaseType)
        {
            var existing = type.GetMembers(newName).FirstOrDefault(member =>
                SymbolEqualityComparer.Default.Equals(type, method.ContainingType) ||
                compilation.IsSymbolAccessibleWithin(member, method.ContainingType));
            if (existing != null)
            {
                throw new RefactoringException(
                    ErrorCodes.NameConflictScope,
                    $"'{method.ToDisplayString()}' cannot be renamed to '{newName}': '{existing.ToDisplayString()}' already exists. " +
                    "Pass renameToAsync: false to keep the name.");
            }
        }
    }

    /// <summary>
    /// Renames the method with Roslyn's renamer and finds its declaration in the renamed solution
    /// again by symbol: the only method of its type with the new name (<see cref="CheckNameIsFree"/>)
    /// and its parameters.
    /// </summary>
    private static async Task<(Solution Solution, MethodDeclarationSyntax Declaration)> RenameAsync(
        Solution solution,
        Document document,
        IMethodSymbol method,
        string newName,
        CancellationToken cancellationToken)
    {
        var renamed = await Renamer.RenameSymbolAsync(
            solution,
            method,
            new SymbolRenameOptions(RenameOverloads: false, RenameInStrings: false, RenameInComments: false, RenameFile: false),
            newName,
            cancellationToken);
        renamed = await LinkedDocuments.MergeAsync(solution, renamed, document.Id, cancellationToken);

        var renamedDocument = renamed.GetDocument(document.Id)!;
        var root = await renamedDocument.GetSyntaxRootAsync(cancellationToken);
        var model = await renamedDocument.GetSemanticModelAsync(cancellationToken);
        var typeId = method.ContainingType.GetDocumentationCommentId();
        var parameterTypes = method.Parameters.Select(p => p.Type.ToDisplayString()).ToList();

        return (renamed, root?.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == newName)
            .FirstOrDefault(m => model?.GetDeclaredSymbol(m, cancellationToken) is { } candidate &&
                candidate.ContainingType.GetDocumentationCommentId() == typeId &&
                candidate.Arity == method.Arity &&
                candidate.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(parameterTypes))
            ?? throw new RefactoringException(ErrorCodes.RoslynError, $"Could not find '{newName}' after renaming it."));
    }

    /// <summary>
    /// What the method's own body waits for: a task waited for with <c>.Result</c>, <c>.Wait()</c>
    /// or <c>.GetAwaiter().GetResult()</c>, and a call returning a task used as a statement (also
    /// as the expression body of a void method). Lambdas and local functions are not searched.
    /// A site where await is not allowed (a lock body, an exception filter, a query, unsafe code)
    /// has a <see cref="AwaitSite.Reason"/>.
    /// </summary>
    private static List<AwaitSite> FindBodyAwaits(
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        TaskTypes tasks,
        CancellationToken cancellationToken)
    {
        var sites = new List<AwaitSite>();
        var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;
        if (body == null)
            return sites;

        bool Awaitable(ExpressionSyntax expression) =>
            tasks.IsAwaitable(model.GetTypeInfo(expression, cancellationToken).Type) && !IsConditionalAccess(expression);
        ISymbol? Symbol(SyntaxNode node) => model.GetSymbolInfo(node, cancellationToken).Symbol;

        foreach (var node in body.DescendantNodesAndSelf(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            var site = node switch
            {
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" } access
                    when tasks.IsResult(Symbol(access)) && Awaitable(access.Expression) =>
                    new AwaitSite(access, r => ((MemberAccessExpressionSyntax)r).Expression, "Replace '.Result' with await"),

                InvocationExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 0,
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Wait" } wait
                } invocation when tasks.IsWait(Symbol(invocation)) && Awaitable(wait.Expression) =>
                    new AwaitSite(invocation, r => ReceiverOf(r), "Replace '.Wait()' with await"),

                InvocationExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 0,
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "GetResult",
                        Expression: InvocationExpressionSyntax
                        {
                            ArgumentList.Arguments.Count: 0,
                            Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetAwaiter" } getAwaiter
                        } awaiterCall
                    }
                } invocation when Symbol(invocation) is IMethodSymbol { Name: "GetResult", Parameters.Length: 0 } &&
                                  tasks.IsGetAwaiter(Symbol(awaiterCall)) && Awaitable(getAwaiter.Expression) =>
                    new AwaitSite(invocation, r => ReceiverOf(ReceiverOf(r)), "Replace '.GetAwaiter().GetResult()' with await"),

                ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call } when Awaitable(call) =>
                    new AwaitSite(call, r => r, $"Await the call to '{InvokedName(call)}'"),

                ArrowExpressionClauseSyntax { Expression: InvocationExpressionSyntax call } arrow
                    when arrow == declaration.ExpressionBody && IsVoid(declaration.ReturnType) && Awaitable(call) =>
                    new AwaitSite(call, r => r, $"Await the call to '{InvokedName(call)}'"),

                _ => null
            };

            if (site != null)
                sites.Add(site with { Reason = WhyAwaitIsNotAllowed(site.Node, declaration) });
        }

        return sites;
    }

    private static bool IsVoid(TypeSyntax type) => type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    /// <summary>The receiver of a call such as <c>task.Wait()</c>: <c>task</c>.</summary>
    private static ExpressionSyntax ReceiverOf(ExpressionSyntax invocation) =>
        ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)invocation).Expression).Expression;

    private static string InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        var expression => expression.ToString()
    };

    /// <summary>
    /// Whether <paramref name="expression"/> is evaluated only when a <c>?.</c> receiver is not
    /// null (<c>a?.Load()</c>, <c>a?.B.Task</c>): awaiting it would await null and throw, and
    /// <c>await</c> cannot be written inside the conditional access.
    /// </summary>
    private static bool IsConditionalAccess(ExpressionSyntax expression)
    {
        for (var current = expression; ;)
        {
            switch (current)
            {
                case MemberBindingExpressionSyntax or ElementBindingExpressionSyntax:
                    return true;
                case MemberAccessExpressionSyntax access:
                    current = access.Expression;
                    break;
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;
                case ElementAccessExpressionSyntax element:
                    current = element.Expression;
                    break;
                case PostfixUnaryExpressionSyntax postfix:
                    current = postfix.Operand;
                    break;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Why <c>await</c> cannot be written at <paramref name="node"/> inside
    /// <paramref name="function"/>, or null when it can.
    /// </summary>
    private static string? WhyAwaitIsNotAllowed(SyntaxNode node, SyntaxNode function)
    {
        for (SyntaxNode child = node, parent = node.Parent!; parent != null && child != function; child = parent, parent = parent.Parent!)
        {
            var reason = parent switch
            {
                LockStatementSyntax @lock when @lock.Statement == child => "await is not allowed in the body of a lock statement",
                CatchFilterClauseSyntax => "await is not allowed in an exception filter",
                UnsafeStatementSyntax => "await is not allowed in unsafe code",
                QueryBodySyntax => "await is not allowed in a query expression",
                _ => null
            };

            if (reason != null)
                return reason;
        }

        return null;
    }

    /// <summary>
    /// Why the call <paramref name="invocation"/> of the converted method is left as it is, or
    /// null when it gets awaited: its caller is not async (or cannot be), await is not allowed
    /// where it is, or it is a <c>?.</c> call.
    /// </summary>
    private static string? WhyCallIsNotAwaited(
        InvocationExpressionSyntax invocation,
        MethodDeclarationSyntax convertedMethod,
        string returns)
    {
        if (IsConditionalAccess(invocation.Expression))
            return $"it is called with '?.', where awaiting a null task would throw; the call returns '{returns}' now";

        var function = invocation.Ancestors().FirstOrDefault(a =>
            a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax);
        var notAsync = function switch
        {
            GlobalStatementSyntax => null,
            MethodDeclarationSyntax method when method.Modifiers.Any(SyntaxKind.AsyncKeyword) || IsSameDeclaration(method, convertedMethod) => null,
            MethodDeclarationSyntax method =>
                $"'{(method.Parent is BaseTypeDeclarationSyntax type ? type.Identifier.ValueText + "." : "")}{method.Identifier.ValueText}' is not async",
            LocalFunctionStatementSyntax local when local.Modifiers.Any(SyntaxKind.AsyncKeyword) => null,
            LocalFunctionStatementSyntax local => $"local function '{local.Identifier.ValueText}' is not async",
            AnonymousFunctionExpressionSyntax lambda when lambda.Modifiers.Any(SyntaxKind.AsyncKeyword) => null,
            AnonymousMethodExpressionSyntax => "the anonymous method around it is not async",
            AnonymousFunctionExpressionSyntax => "the lambda around it is not async",
            ConstructorDeclarationSyntax => "it is in a constructor, which cannot be async",
            AccessorDeclarationSyntax or BasePropertyDeclarationSyntax => "it is in a property, indexer or event accessor, which cannot be async",
            BaseFieldDeclarationSyntax => "it is in a field initializer, which cannot be async",
            _ => "it is not in a method that can be async"
        };

        if (notAsync != null)
            return $"{notAsync}, so the call returns '{returns}' without waiting for it";

        return WhyAwaitIsNotAllowed(invocation, function!);
    }

    /// <summary>Whether two declarations of the same solution's text are the same one.</summary>
    private static bool IsSameDeclaration(SyntaxNode left, SyntaxNode right) =>
        left.Span == right.Span && PathResolver.PathsEqual(left.SyntaxTree.FilePath, right.SyntaxTree.FilePath);

    /// <summary>
    /// <c>await</c> of <paramref name="operand"/> in place of <paramref name="replaced"/>
    /// (<paramref name="rewritten"/> is its edited form), with that node's outer trivia, and in
    /// parentheses where the expression around it binds tighter than await:
    /// <c>Load().Length</c> becomes <c>(await LoadAsync()).Length</c>.
    /// </summary>
    private static ExpressionSyntax Await(ExpressionSyntax operand, SyntaxNode rewritten, SyntaxNode replaced, string description)
    {
        ExpressionSyntax awaited = SyntaxFactory.AwaitExpression(
            SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            operand.WithoutTrivia());

        if (NeedsParentheses(replaced))
            awaited = SyntaxFactory.ParenthesizedExpression(awaited);

        return awaited
            .WithLeadingTrivia(rewritten.GetLeadingTrivia())
            .WithTrailingTrivia(rewritten.GetTrailingTrivia())
            .WithAdditionalAnnotations(new SyntaxAnnotation(AwaitAnnotationKind, description));
    }

    /// <summary>Whether <paramref name="node"/> is the operand of a primary expression (<c>.</c>, <c>?.</c>, <c>[]</c>, <c>()</c>, <c>!</c>).</summary>
    private static bool NeedsParentheses(SyntaxNode node) => node.Parent switch
    {
        MemberAccessExpressionSyntax access => access.Expression == node,
        ConditionalAccessExpressionSyntax conditional => conditional.Expression == node,
        ElementAccessExpressionSyntax element => element.Expression == node,
        InvocationExpressionSyntax invocation => invocation.Expression == node,
        PostfixUnaryExpressionSyntax => true,
        _ => false
    };

    /// <summary>
    /// <paramref name="method"/> with <c>async</c> after its other modifiers and
    /// <paramref name="returnType"/> in place of its return type.
    /// </summary>
    private static MethodDeclarationSyntax MakeAsync(MethodDeclarationSyntax method, TypeSyntax returnType)
    {
        // The keyword takes the place of the return type (its indentation, or its doc comment when
        // the method has no modifiers) and the new type follows it.
        var asyncKeyword = SyntaxFactory.Token(
            method.ReturnType.GetLeadingTrivia(), SyntaxKind.AsyncKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space));

        return method
            .WithModifiers(method.Modifiers.Add(asyncKeyword))
            .WithReturnType(returnType.WithTrailingTrivia(method.ReturnType.GetTrailingTrivia()));
    }

    /// <summary>
    /// The references to the method and its calls. A file compiled by several projects is read
    /// through one document only (the declaring one for the method's own file), so all edits of a
    /// file go to the same document; a call that document does not compile (another target
    /// framework's <c>#if</c> branch) is not edited but listed in <see cref="References.Hidden"/>.
    /// Calls in generated code are not edited.
    /// </summary>
    private static async Task<References> FindReferencesAsync(
        IMethodSymbol method,
        Document declaringDocument,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var locations = (await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken))
            .SelectMany(r => r.Locations)
            .ToList();
        var calls = new List<(Document Document, InvocationExpressionSyntax Invocation)>();
        var hidden = new List<HiddenCall>();
        var seen = new HashSet<(string, TextSpan)>();

        foreach (var location in locations
                     .Where(l => !l.IsImplicit)
                     .OrderBy(l => l.Document.FilePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(l => l.Location.SourceSpan.Start))
        {
            var path = location.Document.FilePath ?? location.Document.Name;
            var span = location.Location.SourceSpan;
            if (!seen.Add((path, span)) ||
                location.Location.SourceTree is not { } tree ||
                GeneratedCode.IsGenerated(tree, cancellationToken))
            {
                continue;
            }

            var canonical = location.Document.FilePath == null
                ? solution.GetDocument(location.Document.Id)
                : PathResolver.PathsEqual(path, declaringDocument.FilePath)
                    ? declaringDocument
                    : solution.GetDocumentIdsWithFilePath(location.Document.FilePath).Select(solution.GetDocument).OfType<Document>().FirstOrDefault();
            if (canonical == null || await canonical.GetSyntaxRootAsync(cancellationToken) is not { } root)
                continue;

            if (NameAt(root, span) is { } name)
            {
                if (CallOf(name) is { } invocation)
                    calls.Add((canonical, invocation));
            }
            else if (await location.Document.GetSyntaxRootAsync(cancellationToken) is { } ownRoot &&
                     NameAt(ownRoot, span) is { } ownName && CallOf(ownName) != null)
            {
                hidden.Add(new HiddenCall(canonical.Id, path, span.Start, location.Document.Project.Name));
            }
        }

        return new References(locations, calls, hidden, seen.Count);
    }

    /// <summary>The name whose identifier is at <paramref name="span"/>, or null when the text there is not code (an inactive <c>#if</c> branch).</summary>
    private static SimpleNameSyntax? NameAt(SyntaxNode root, TextSpan span) =>
        root.FindNode(span, findInsideTrivia: true, getInnermostNodeForTie: true) is SimpleNameSyntax name && name.Identifier.Span == span
            ? name
            : null;

    /// <summary>The call whose method <paramref name="name"/> names (<c>Load(1)</c>, <c>x.Load(1)</c>, <c>x?.Load(1)</c>), if any.</summary>
    private static InvocationExpressionSyntax? CallOf(SimpleNameSyntax name)
    {
        SyntaxNode callee = name.Parent is MemberAccessExpressionSyntax access && access.Name == name || name.Parent is MemberBindingExpressionSyntax
            ? name.Parent
            : name;
        return callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee ? invocation : null;
    }

    /// <summary>The documents whose text differs between the two solutions.</summary>
    private static IEnumerable<DocumentId> ChangedDocuments(Solution before, Solution after) =>
        after.GetChanges(before).GetProjectChanges().SelectMany(p => p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true));

    /// <summary>
    /// <paramref name="original"/> with the texts of the documents <paramref name="changed"/>
    /// changed, and nothing else: a document whose tree only gained annotations is not written.
    /// </summary>
    private static async Task<Solution> WithTextChangesOnlyAsync(Solution original, Solution changed, CancellationToken cancellationToken)
    {
        var result = original;
        foreach (var id in ChangedDocuments(original, changed))
        {
            var text = await changed.GetDocument(id)!.GetTextAsync(cancellationToken);
            if (original.GetDocument(id) is { } before && !(await before.GetTextAsync(cancellationToken)).ContentEquals(text))
                result = result.WithDocumentText(id, text);
        }

        return result;
    }

    /// <summary>
    /// A warning per call or wait left as it is, at its position in the new text
    /// (<c>path(line,column): not awaited: reason.</c>), ordered by file and position: the ones
    /// annotated in the edited documents, and the calls in <c>#if</c> branches the edited document
    /// of their file does not compile.
    /// </summary>
    private static async Task<List<string>> NotAwaitedWarningsAsync(
        Solution renamed,
        Solution changed,
        IEnumerable<DocumentId> edited,
        List<HiddenCall> hidden,
        string returns,
        CancellationToken cancellationToken)
    {
        var found = new List<(string Path, LinePosition Position, string Reason)>();
        foreach (var id in edited)
        {
            if (changed.GetDocument(id) is not { } document || await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
                continue;

            foreach (var node in root.GetAnnotatedNodes(NotAwaitedAnnotationKind))
            {
                var span = NameOf(node).GetLocation().GetLineSpan();
                found.Add((span.Path, span.StartLinePosition, node.GetAnnotations(NotAwaitedAnnotationKind).First().Data!));
            }
        }

        foreach (var calls in hidden.GroupBy(c => c.Document))
        {
            var before = renamed.GetDocument(calls.Key)!;
            var after = changed.GetDocument(calls.Key)!;
            var oldText = await before.GetTextAsync(cancellationToken);
            var runs = ChangedLines(oldText, await after.GetTextAsync(cancellationToken), await after.GetTextChangesAsync(before, cancellationToken));

            foreach (var call in calls)
            {
                // The call's own line is unchanged; the runs of changed lines above it may have added lines.
                var position = oldText.Lines.GetLinePosition(call.Position);
                var line = position.Line + runs
                    .Where(r => r.OldLast < position.Line)
                    .Sum(r => r.NewLast - r.NewFirst - (r.OldLast - r.OldFirst));
                found.Add((
                    call.Path,
                    new LinePosition(line, position.Character),
                    $"it is compiled only in '{call.Project}' (an #if branch), where it is renamed but not edited otherwise; " +
                    $"the call returns '{returns}' now"));
            }
        }

        return found
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Position)
            .Select(f => $"{f.Path}({f.Position.Line + 1},{f.Position.Character + 1}): not awaited: {f.Reason}.")
            .Distinct()
            .ToList();
    }

    /// <summary>The name a warning about <paramref name="node"/> points at: the called or accessed member.</summary>
    private static SyntaxNode NameOf(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name,
        InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax binding } => binding.Name,
        MemberAccessExpressionSyntax access => access.Name,
        _ => node
    };

    /// <summary>
    /// The changes a commit would write, one per run of changed lines of each file (a file
    /// compiled by several projects once): the old and new lines, described by what the
    /// operation did there.
    /// </summary>
    private static async Task<List<PendingChange>> CreatePendingChangesAsync(
        Solution before,
        Solution after,
        IReadOnlyCollection<DocumentId> edited,
        string declarationChange,
        string? renameChange,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        var paths = new HashSet<string>(PathResolver.PathComparer);

        // The edited document of a file carries the annotations; its linked documents only its text.
        var documents = ChangedDocuments(before, after)
            .Select(id => after.GetDocument(id)!)
            .OrderBy(d => d.FilePath ?? d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => edited.Contains(d.Id) ? 0 : 1);

        foreach (var document in documents)
        {
            var path = document.FilePath ?? document.Name;
            if (!paths.Add(path) || before.GetDocument(document.Id) is not { } oldDocument)
                continue;

            var oldText = await oldDocument.GetTextAsync(cancellationToken);
            var newText = await document.GetTextAsync(cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);

            foreach (var hunk in ChangedLines(oldText, newText, await document.GetTextChangesAsync(oldDocument, cancellationToken)))
            {
                var newSpan = TextSpan.FromBounds(newText.Lines[hunk.NewFirst].Start, newText.Lines[hunk.NewLast].End);
                changes.Add(new PendingChange
                {
                    File = path,
                    ChangeType = ChangeKind.Modify,
                    Description = Describe(root, newSpan, declarationChange, renameChange),
                    BeforeSnippet = Lines(oldText, hunk.OldFirst, hunk.OldLast),
                    AfterSnippet = Lines(newText, hunk.NewFirst, hunk.NewLast),
                    StartLine = hunk.OldFirst + 1,
                    EndLine = hunk.OldLast + 1
                });
            }
        }

        return changes;
    }

    /// <summary>
    /// The runs of changed lines: the first and last line of each run in the old and in the new
    /// text (0-based). The text changes (which a tree diff may make coarse) only say which lines to
    /// compare; within them, lines that stayed the same are matched (longest common subsequence),
    /// so each run holds only lines that changed. Lines that were only inserted or only removed are
    /// shown with the line before them.
    /// </summary>
    private static List<(int OldFirst, int OldLast, int NewFirst, int NewLast)> ChangedLines(
        SourceText oldText,
        SourceText newText,
        IEnumerable<TextChange> changes)
    {
        // The line ranges the changes touch, in both texts; changes on a shared line form one region.
        var regions = new List<(int OldFirst, int OldLast, int NewFirst, int NewLast)>();
        var delta = 0;
        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            var inserted = change.NewText ?? "";
            var newStart = change.Span.Start + delta;
            delta += inserted.Length - change.Span.Length;

            var (oldFirst, oldLast) = LineRange(oldText, change.Span.Start, change.Span.End);
            var (newFirst, newLast) = LineRange(newText, newStart, newStart + inserted.Length);
            if (regions.Count > 0 && regions[^1].OldLast >= oldFirst)
            {
                var last = regions[^1];
                regions[^1] = (last.OldFirst, Math.Max(last.OldLast, oldLast), last.NewFirst, Math.Max(last.NewLast, newLast));
            }
            else
            {
                regions.Add((oldFirst, oldLast, newFirst, newLast));
            }
        }

        var runs = new List<(int OldFirst, int OldLast, int NewFirst, int NewLast)>();
        foreach (var region in regions)
        {
            var oldLines = Enumerable.Range(region.OldFirst, region.OldLast - region.OldFirst + 1).Select(i => oldText.Lines[i].ToString()).ToList();
            var newLines = Enumerable.Range(region.NewFirst, region.NewLast - region.NewFirst + 1).Select(i => newText.Lines[i].ToString()).ToList();
            foreach (var (oldFirst, oldLast, newFirst, newLast) in DiffLines(oldLines, newLines))
            {
                var run = (OldFirst: region.OldFirst + oldFirst, OldLast: region.OldFirst + oldLast, NewFirst: region.NewFirst + newFirst, NewLast: region.NewFirst + newLast);
                if (run.OldLast < run.OldFirst || run.NewLast < run.NewFirst)
                {
                    // Only inserted or only removed lines: show the unchanged line before them (or after them at the top).
                    run = run.OldFirst > 0
                        ? (run.OldFirst - 1, Math.Max(run.OldLast, run.OldFirst - 1), run.NewFirst - 1, Math.Max(run.NewLast, run.NewFirst - 1))
                        : (0, run.OldLast + 1, 0, run.NewLast + 1);
                }

                runs.Add(run);
            }
        }

        return runs;
    }

    /// <summary>
    /// The runs of lines that differ between <paramref name="oldLines"/> and
    /// <paramref name="newLines"/>, as index ranges into each (a range is empty, last &lt; first,
    /// when that side has no lines in the run). Lines are matched along a longest common
    /// subsequence; regions too large for that are one run.
    /// </summary>
    private static List<(int OldFirst, int OldLast, int NewFirst, int NewLast)> DiffLines(List<string> oldLines, List<string> newLines)
    {
        int n = oldLines.Count, m = newLines.Count;
        if ((long)n * m > 1_000_000)
            return [(0, n - 1, 0, m - 1)];

        // common[i, j]: the length of the longest common subsequence of oldLines[i..] and newLines[j..].
        var common = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                common[i, j] = oldLines[i] == newLines[j]
                    ? common[i + 1, j + 1] + 1
                    : Math.Max(common[i + 1, j], common[i, j + 1]);
            }
        }

        var runs = new List<(int OldFirst, int OldLast, int NewFirst, int NewLast)>();
        int x = 0, y = 0;
        (int Old, int New)? open = null;
        while (x < n || y < m)
        {
            if (x < n && y < m && oldLines[x] == newLines[y])
            {
                if (open is { } start)
                    runs.Add((start.Old, x - 1, start.New, y - 1));
                open = null;
                x++;
                y++;
                continue;
            }

            open ??= (x, y);
            if (y < m && (x == n || common[x, y + 1] >= common[x + 1, y]))
                y++;
            else
                x++;
        }

        if (open is { } end)
            runs.Add((end.Old, n - 1, end.New, m - 1));

        return runs;
    }

    /// <summary>The lines <c>[start, end)</c> touches; an empty span touches the line it is on.</summary>
    private static (int First, int Last) LineRange(SourceText text, int start, int end) =>
        (text.Lines.GetLineFromPosition(start).LineNumber, text.Lines.GetLineFromPosition(end > start ? end - 1 : start).LineNumber);

    private static string Lines(SourceText text, int first, int last) =>
        string.Join("\n", Enumerable.Range(first, last - first + 1).Select(line => text.Lines[line].ToString())).Trim();

    /// <summary>What the operation did in <paramref name="span"/> of <paramref name="root"/>.</summary>
    private static string Describe(SyntaxNode? root, TextSpan span, string declarationChange, string? renameChange)
    {
        var parts = new List<string>();
        if (root != null)
        {
            if (root.GetAnnotatedNodes(MethodAnnotationKind).OfType<MethodDeclarationSyntax>().Any(m => HeaderSpan(m).IntersectsWith(span)))
                parts.Add(declarationChange);

            parts.AddRange(root.GetAnnotatedNodes(AwaitAnnotationKind)
                .Where(n => n.Span.IntersectsWith(span))
                .Select(n => n.GetAnnotations(AwaitAnnotationKind).First().Data!));

            if (root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .OfType<UsingDirectiveSyntax>()
                .Any(u => u.Span.IntersectsWith(span) && u.Name?.ToString() == TasksNamespace && u.GlobalKeyword.IsKind(SyntaxKind.None)))
            {
                parts.Add($"Add 'using {TasksNamespace};'");
            }
        }

        if (parts.Count == 0)
            parts.Add(renameChange ?? "Update");

        return string.Join("; ", parts.Distinct());
    }

    /// <summary>A method's header: from its first modifier (or its return type) to the end of its parameter list.</summary>
    private static TextSpan HeaderSpan(MethodDeclarationSyntax method) => TextSpan.FromBounds(
        method.Modifiers.Count > 0 ? method.Modifiers[0].SpanStart : method.ReturnType.SpanStart,
        method.ParameterList.Span.End);

    /// <summary>
    /// A place in the method's body to await: <see cref="Node"/> is replaced by
    /// <c>await</c> of <see cref="Operand"/> of its edited form, unless <see cref="Reason"/> says why
    /// await is not allowed there.
    /// </summary>
    private sealed record AwaitSite(
        ExpressionSyntax Node,
        Func<ExpressionSyntax, ExpressionSyntax> Operand,
        string Description,
        string? Reason = null);

    /// <summary>
    /// The references to the converted method (<see cref="Count"/> distinct ones in source), its
    /// calls, each with the document it is edited through, and the calls that document does not
    /// compile.
    /// </summary>
    private sealed record References(
        List<ReferenceLocation> Locations,
        List<(Document Document, InvocationExpressionSyntax Invocation)> Calls,
        List<HiddenCall> Hidden,
        int Count);

    /// <summary>
    /// A call in an <c>#if</c> branch that <see cref="Document"/>, the document its file is edited
    /// through, does not compile; <see cref="Position"/> is its name's start in that document's text.
    /// </summary>
    private sealed record HiddenCall(DocumentId Document, string Path, int Position, string Project);

    /// <summary>
    /// The task types of a compilation: <c>Task</c>, <c>Task&lt;T&gt;</c> (and types derived from
    /// them), <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c> and what their <c>ConfigureAwait</c>
    /// returns. Decided by symbol, not by name.
    /// </summary>
    private sealed class TaskTypes
    {
        private readonly INamedTypeSymbol? _task;
        private readonly INamedTypeSymbol? _taskOfT;
        private readonly INamedTypeSymbol? _valueTaskOfT;
        private readonly HashSet<INamedTypeSymbol> _structAwaitables;

        public TaskTypes(Compilation compilation)
        {
            _task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            _taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            _valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            _structAwaitables = new HashSet<INamedTypeSymbol>(
                new[]
                {
                    compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"),
                    _valueTaskOfT,
                    compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredTaskAwaitable"),
                    compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1"),
                    compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable"),
                    compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1")
                }.OfType<INamedTypeSymbol>(),
                SymbolEqualityComparer.Default);
        }

        /// <summary>Whether the compilation has <c>Task</c> and <c>Task&lt;T&gt;</c>.</summary>
        public bool Available => _task != null && _taskOfT != null;

        /// <summary>Whether a value of <paramref name="type"/> can be awaited.</summary>
        public bool IsAwaitable(ITypeSymbol? type)
        {
            if (type is not INamedTypeSymbol named)
                return false;

            if (_structAwaitables.Contains(named.OriginalDefinition))
                return true;

            for (var current = named; current != null; current = current.BaseType)
            {
                if (Is(current, _task) || Is(current, _taskOfT))
                    return true;
            }

            return false;
        }

        /// <summary>Whether <paramref name="symbol"/> is <c>Task&lt;T&gt;.Result</c> or <c>ValueTask&lt;T&gt;.Result</c>.</summary>
        public bool IsResult(ISymbol? symbol) =>
            symbol is IPropertySymbol { Name: "Result" } property &&
            (Is(property.ContainingType, _taskOfT) || Is(property.ContainingType, _valueTaskOfT));

        /// <summary>Whether <paramref name="symbol"/> is <c>Task.Wait()</c> (without a timeout or token).</summary>
        public bool IsWait(ISymbol? symbol) =>
            symbol is IMethodSymbol { Name: "Wait", Parameters.Length: 0 } method && Is(method.ContainingType, _task);

        /// <summary>Whether <paramref name="symbol"/> is the <c>GetAwaiter()</c> of a task type.</summary>
        public bool IsGetAwaiter(ISymbol? symbol) =>
            symbol is IMethodSymbol { Name: "GetAwaiter", Parameters.Length: 0 } method && IsAwaitable(method.ContainingType);

        private static bool Is(INamedTypeSymbol type, INamedTypeSymbol? definition) =>
            definition != null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, definition);
    }
}
