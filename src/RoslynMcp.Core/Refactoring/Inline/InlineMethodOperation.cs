using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// Replaces calls to a method with its body: one call, or all of them, after which the method
/// can be deleted.
/// </summary>
/// <remarks>
/// <para>
/// A method whose body is an expression or a single <c>return</c> inlines into any expression;
/// a void method's statements replace a call that is a statement of its own. Arguments are
/// substituted for the parameters (parenthesized and cast where needed, left to the simplifier
/// to remove), defaults fill in left-out optional arguments, type arguments replace type
/// parameters, and an instance call's receiver replaces <c>this</c>. An argument (or receiver)
/// whose evaluation could change meaning when moved, repeated or dropped is stored in a local
/// first, in the original order; where no statement can be introduced, that call is refused.
/// Names the body declares are renamed when they would clash with names at the call site, and
/// the types and static members it names are written so they bind there.
/// </para>
/// <para>
/// Refused methods: recursive, virtual, abstract, override, interface, partial, extern, async,
/// iterator, unsafe and <c>[Conditional]</c> methods, <c>ref</c>/<c>out</c>/<c>in</c> parameters,
/// other block bodies, <c>return</c> in a void body. Refused calls (reported in warnings when
/// all calls are inlined): conditional access, expression trees, another checked context,
/// members the call site can't access, <c>base</c> or event access from another type, and calls
/// where a name of the body would bind to something else. Method groups, <c>nameof</c> and crefs
/// are left as they are and keep the method.
/// </para>
/// </remarks>
public sealed class InlineMethodOperation : RefactoringOperationBase<InlineMethodParams>
{
    private const string DeleteAnnotationKind = "RoslynMcp.InlineMethod.Delete";
    private const int MaxVerificationRounds = 3;

    /// <summary>
    /// Creates a new inline method operation.
    /// </summary>
    public InlineMethodOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(InlineMethodParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MethodName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "methodName is required.");

        ValidateFile(@params.SourceFile, "sourceFile");

        if (@params.Line is < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

        if (@params.CallLine is < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "callLine must be >= 1.");

        if (@params.CallColumn is < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "callColumn must be >= 1.");

        if (@params.CallLine == null && (@params.CallFile != null || @params.CallColumn != null))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "callFile and callColumn need callLine.");

        if (@params.CallFile != null)
            ValidateFile(@params.CallFile, "callFile");
    }

    private static void ValidateFile(string path, string name)
    {
        if (!PathResolver.IsAbsolutePath(path))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, $"{name} must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(path))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(path, name));

        if (!File.Exists(path))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"File not found: {path}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        InlineMethodParams @params,
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
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve the method.");

        CheckMethod(method, declaration, cancellationToken);
        var body = InlinedBody.Analyze(method, declaration, model, cancellationToken);
        var solution = Context.Solution;

        var (calls, others) = await FindReferencesAsync(method, document, declaration, solution, cancellationToken);
        (Document Document, InvocationExpressionSyntax Invocation)? singleCall = @params.CallLine is { } callLine
            ? SelectCall(calls, others, @params.CallFile ?? @params.SourceFile, callLine, @params.CallColumn, method)
            : null;
        var deleteMethod = @params.DeleteMethod ?? singleCall == null;
        if (singleCall == null && calls.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.CallSiteNotFound,
                $"'{method.ToDisplayString()}' has no calls to inline" +
                (others.Count > 0 ? $" ({others.Count} other reference(s): method groups, nameof or crefs)." : "."));
        }

        // Plan every call; refused ones stay and are reported.
        var planner = new InlineCallPlanner(body);
        var plans = new List<CallPlan>();
        var refused = new List<(Location Location, string Reason)>();
        foreach (var (callDocument, invocation) in singleCall is { } single ? [single] : calls)
        {
            var plan = await planner.PlanAsync(plans.Count + refused.Count, callDocument, invocation, cancellationToken);
            if (plan != null)
                plans.Add(plan);
            else
                refused.Add((CallPlan.NameToken(invocation).GetLocation(), planner.Refusal!));
        }

        // Apply, check that the names of every inlined body still bind as before, and drop the calls where they don't.
        Solution newSolution = solution;
        var deletes = false;
        var layoutNotes = new List<string>();
        for (var round = 0; ; round++)
        {
            if (plans.Count == 0)
                throw NotInlinable(method, refused, singleCall != null);

            var remaining = others.Count + calls.Count - plans.Count;
            deletes = deleteMethod && remaining == 0 && !await ImplementsInterfaceAsync(method, model.Compilation, solution, cancellationToken);
            layoutNotes.Clear();
            newSolution = await ApplyAsync(solution, plans, deletes ? (document, declaration) : null, layoutNotes, cancellationToken);

            var mismatches = await VerifyAsync(newSolution, plans, cancellationToken);
            if (mismatches.Count == 0 || round == MaxVerificationRounds)
                break;

            foreach (var (plan, name) in mismatches)
            {
                plans.Remove(plan);
                refused.Add((CallPlan.NameToken(plan.Invocation).GetLocation(), $"'{name}' in the body would refer to something else there"));
            }
        }

        // The pending changes are read from the annotated trees, before the deletion reparses a text.
        var annotated = newSolution;
        var deletion = deletes ? await DeleteAsync(newSolution, document.Id, cancellationToken) : null;
        if (deletion != null)
            newSolution = deletion.Value.Solution;

        var edited = plans.Select(p => p.Document.Id).Append(document.Id).Distinct().ToList();
        newSolution = await LinkedDocuments.SyncAsync(solution, newSolution, edited, cancellationToken);

        var warnings = new List<string>();
        warnings.AddRange(refused
            .Select(r => (Span: r.Location.GetLineSpan(), r.Reason))
            .OrderBy(r => r.Span.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Span.StartLinePosition)
            .Select(r => $"{r.Span.Path}({r.Span.StartLinePosition.Line + 1},{r.Span.StartLinePosition.Character + 1}): not inlined: {r.Reason}"));
        if (deleteMethod && !deletes)
            warnings.Add(await KeptReasonAsync(method, model.Compilation, solution, others.Count + calls.Count - plans.Count, cancellationToken));
        warnings.AddRange(layoutNotes);
        warnings.AddRange(await IntroducedErrors.FindAsync(solution, newSolution, edited, cancellationToken) ?? []);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(annotated, plans, deletion?.Change, cancellationToken),
                Warnings = warnings.Count == 0 ? null : warnings
            };
        }

        var commit = await CommitChangesAsync(newSolution, cancellationToken);
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
                Name = method.Name,
                FullyQualifiedName = method.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Method
            },
            ReferencesUpdated = plans.Count,
            Warnings = warnings.Count == 0 ? null : warnings
        };
    }

    /// <summary>Refuses methods that can't be inlined anywhere.</summary>
    private static void CheckMethod(IMethodSymbol method, MethodDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        var reason = method switch
        {
            _ when GeneratedCode.IsGenerated(declaration.SyntaxTree, cancellationToken) => "it is declared in generated code",
            { ContainingType.TypeKind: TypeKind.Interface } => "it is an interface member; inlining would bypass dispatch",
            { IsVirtual: true } or { IsAbstract: true } or { IsOverride: true } => "it is virtual, abstract or an override; inlining would bypass dispatch",
            { ExplicitInterfaceImplementations.Length: > 0 } => "it is an explicit interface implementation",
            { IsExtern: true } => "it is extern",
            { PartialDefinitionPart: not null } or { PartialImplementationPart: not null } => "it is partial",
            { IsAsync: true } => "it is async",
            { ReturnsByRef: true } or { ReturnsByRefReadonly: true } => "it returns by reference",
            { IsVararg: true } => "it uses __arglist",
            _ when method.Parameters.Any(p => p.RefKind != RefKind.None || p.ScopedKind != ScopedKind.None) => "it has ref, out or in parameters",
            _ when method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute") =>
                "it is [Conditional], so its calls depend on the build",
            _ when declaration.Modifiers.Any(SyntaxKind.UnsafeKeyword) || declaration.Ancestors().OfType<TypeDeclarationSyntax>()
                .Any(t => t.Modifiers.Any(SyntaxKind.UnsafeKeyword)) => "it is unsafe code",
            _ => null
        };

        if (reason != null)
            throw new RefactoringException(ErrorCodes.MethodNotInlinable, $"'{method.ToDisplayString()}' cannot be inlined: {reason}.");
    }

    /// <summary>
    /// The calls of the method and the other references that remain: method groups, <c>nameof</c>,
    /// crefs, and calls in generated code. Fails for a recursive method. A file compiled by several
    /// projects is read through one document only (the declaring one for the method's file), so all
    /// edits of a file go to the same document.
    /// </summary>
    private static async Task<(List<(Document Document, InvocationExpressionSyntax Invocation)> Calls, List<Location> Others)> FindReferencesAsync(
        IMethodSymbol method,
        Document declaringDocument,
        MethodDeclarationSyntax declaration,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var calls = new List<(Document, InvocationExpressionSyntax)>();
        var others = new List<Location>();
        var seen = new HashSet<(string, TextSpan)>();
        var locations = (await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken))
            .SelectMany(r => r.Locations)
            .OrderBy(l => l.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Location.SourceSpan.Start);

        foreach (var location in locations)
        {
            var path = location.Document.FilePath ?? location.Document.Name;
            if (!seen.Add((path, location.Location.SourceSpan)))
                continue;

            if (location.IsImplicit)
            {
                // A use the compiler makes (deconstruction, foreach, collection initializer) keeps the method.
                others.Add(location.Location);
                continue;
            }

            var canonical = location.Document.FilePath == null
                ? solution.GetDocument(location.Document.Id)
                : PathResolver.PathsEqual(path, declaringDocument.FilePath)
                    ? declaringDocument
                    : solution.GetDocumentIdsWithFilePath(location.Document.FilePath).Select(solution.GetDocument).OfType<Document>().FirstOrDefault();
            var root = canonical == null ? null : await canonical.GetSyntaxRootAsync(cancellationToken);
            var node = root?.FindNode(location.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);
            if (PathResolver.PathsEqual(path, declaration.SyntaxTree.FilePath) && declaration.FullSpan.Contains(location.Location.SourceSpan))
            {
                // A use in its own body is recursion, except in nameof; its own doc comment does not count.
                var inNameOf = node?.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }) == true;
                if (declaration.Span.Contains(location.Location.SourceSpan) && !inNameOf)
                {
                    throw new RefactoringException(
                        ErrorCodes.MethodNotInlinable,
                        $"'{method.ToDisplayString()}' cannot be inlined: it is recursive.");
                }

                continue;
            }

            var callee = node?.Parent is MemberAccessExpressionSyntax access && access.Name == node ||
                node?.Parent is MemberBindingExpressionSyntax
                    ? node.Parent
                    : node;

            if (callee?.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee &&
                canonical != null &&
                !GeneratedCode.IsGenerated(invocation.SyntaxTree, cancellationToken))
            {
                calls.Add((canonical, invocation));
            }
            else
            {
                others.Add(location.Location);
            }
        }

        return (calls, others);
    }

    private static (Document, InvocationExpressionSyntax) SelectCall(
        List<(Document Document, InvocationExpressionSyntax Invocation)> calls,
        List<Location> others,
        string file,
        int line,
        int? column,
        IMethodSymbol method)
    {
        bool Matches(Location location)
        {
            var span = location.GetLineSpan();
            return PathResolver.PathsEqual(span.Path, file) && span.StartLinePosition.Line + 1 == line &&
                (column == null || (span.StartLinePosition.Character + 1 <= column && column <= span.EndLinePosition.Character + 1));
        }

        var matching = calls.Where(c => Matches(CallPlan.NameToken(c.Invocation).GetLocation())).ToList();
        if (matching.Count == 1)
            return matching[0];

        if (matching.Count > 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Line {line} has {matching.Count} calls to '{method.Name}'. Give callColumn: " +
                string.Join(", ", matching.Select(c => CallPlan.NameToken(c.Invocation).GetLocation().GetLineSpan().StartLinePosition.Character + 1)));
        }

        var where = column == null ? $"{file}({line})" : $"{file}({line},{column})";
        throw new RefactoringException(
            ErrorCodes.CallSiteNotFound,
            others.Any(Matches)
                ? $"The reference to '{method.Name}' at {where} is not a call (a method group, nameof or cref); only calls are inlined."
                : $"No call to '{method.ToDisplayString()}' at {where}.");
    }

    private static RefactoringException NotInlinable(IMethodSymbol method, List<(Location Location, string Reason)> refused, bool single)
    {
        if (single)
            return new RefactoringException(ErrorCodes.CallNotInlinable, $"The call cannot be inlined: {refused[0].Reason}.");

        var reasons = refused.Select(r =>
        {
            var span = r.Location.GetLineSpan();
            return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): {r.Reason}";
        });
        return new RefactoringException(
            ErrorCodes.CallNotInlinable,
            $"No call to '{method.ToDisplayString()}' can be inlined: {string.Join("; ", reasons)}.");
    }

    /// <summary>Whether the method implements an interface member (also on behalf of a derived class), which deleting it would break.</summary>
    private static async Task<bool> ImplementsInterfaceAsync(IMethodSymbol method, Compilation compilation, Solution solution, CancellationToken cancellationToken) =>
        method.DeclaredAccessibility == Accessibility.Public &&
        (await MemberFamily.FindRelatedAsync(method, compilation, solution, cancellationToken)).Count > 0;

    private static async Task<string> KeptReasonAsync(IMethodSymbol method, Compilation compilation, Solution solution, int remaining, CancellationToken cancellationToken)
    {
        if (remaining > 0)
        {
            return $"'{method.ToDisplayString()}' was not deleted: {remaining} reference(s) remain " +
                "(calls not inlined, method groups, nameof or crefs).";
        }

        var related = await MemberFamily.FindRelatedAsync(method, compilation, solution, cancellationToken);
        return $"'{method.ToDisplayString()}' was not deleted: it implements {string.Join(", ", related.Select(r => $"'{r.ToDisplayString()}'"))}.";
    }

    /// <summary>
    /// The solution with every planned call replaced. Expression calls replace their invocation;
    /// their locals, and the replacement of statement calls, replace the statement with a block
    /// that is then spliced into the enclosing statements. The method's declaration is marked
    /// when it is to be deleted. A body inlined into another file gets that file's line ending
    /// (its string literals excepted, which <paramref name="layoutNotes"/> reports).
    /// </summary>
    private static async Task<Solution> ApplyAsync(
        Solution solution,
        List<CallPlan> plans,
        (Document Document, MethodDeclarationSyntax Declaration)? delete,
        ICollection<string> layoutNotes,
        CancellationToken cancellationToken)
    {
        var edits = new SyntaxEdits();
        var statements = new Dictionary<StatementSyntax, (DocumentId Document, CallPlan? Own, List<CallPlan> Nested)>();

        foreach (var plan in plans)
        {
            if (plan.Form == CallForm.Expression)
            {
                edits.Add(plan.Document.Id, plan.Invocation, rewritten => plan.BuildExpression((InvocationExpressionSyntax)rewritten));
                if (plan.HoistBefore is { } hoist)
                    Entry(hoist, plan.Document.Id).Nested.Add(plan);
            }
            else
            {
                var entry = Entry(plan.Statement!, plan.Document.Id);
                statements[plan.Statement!] = (entry.Document, plan, entry.Nested);
            }
        }

        foreach (var (statement, (documentId, own, nested)) in statements)
        {
            edits.Add(documentId, statement, rewritten =>
            {
                var replacement = nested.OrderBy(p => p.Invocation.Span.End).SelectMany(p => p.Temps).ToList();
                if (own != null)
                {
                    replacement.AddRange(own.BuildStatements((ExpressionStatementSyntax)rewritten));
                }
                else
                {
                    // The statement keeps its indentation after the locals, which take its leading comments.
                    var leading = statement.GetLeadingTrivia();
                    var indentation = leading.Count > 0 && leading[^1].IsKind(SyntaxKind.WhitespaceTrivia) ? [leading[^1]] : SyntaxTriviaList.Empty;
                    replacement.Add(((StatementSyntax)rewritten).WithLeadingTrivia(indentation));
                }

                return CallPlan.Replace(statement, replacement);
            });
        }

        if (delete is { } target)
            edits.Add(target.Document.Id, target.Declaration, node => node.WithAdditionalAnnotations(new SyntaxAnnotation(DeleteAnnotationKind)));

        // The namespaces of the body's extension methods are imported before the simplifier runs.
        var imports = new Dictionary<DocumentId, (IReadOnlyList<string> Namespaces, string LineEnding)>();
        foreach (var group in plans.Where(p => p.Namespaces.Count > 0).GroupBy(p => p.Document.Id))
            imports[group.Key] = (group.SelectMany(p => p.Namespaces).ToList(), await LineEndings.GetAsync(group.First().Document, cancellationToken));

        return await edits.ApplyAsync(
            solution,
            cancellationToken,
            (id, root) =>
            {
                root = CallPlan.Splice(root);
                return imports.TryGetValue(id, out var import) && root is CompilationUnitSyntax unit
                    ? UsingDirectives.Add(unit, import.Namespaces, import.LineEnding)
                    : root;
            },
            layoutNotes);

        (DocumentId Document, CallPlan? Own, List<CallPlan> Nested) Entry(StatementSyntax statement, DocumentId document)
        {
            if (!statements.TryGetValue(statement, out var entry))
                statements[statement] = entry = (document, null, []);
            return entry;
        }
    }

    /// <summary>
    /// The calls whose inlined body has a member name that no longer binds to the member it
    /// named in the method, with that name.
    /// </summary>
    private static async Task<List<(CallPlan Plan, string Name)>> VerifyAsync(Solution solution, List<CallPlan> plans, CancellationToken cancellationToken)
    {
        var bySite = plans.ToDictionary(p => p.Site.ToString(CultureInfo.InvariantCulture));
        var mismatches = new List<(CallPlan, string)>();
        foreach (var id in plans.Select(p => p.Document.Id).Distinct())
        {
            var document = solution.GetDocument(id)!;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                continue;

            foreach (var node in root.GetAnnotatedNodes(InlinedBody.SymbolAnnotationKind))
            {
                var expected = node.GetAnnotations(InlinedBody.SymbolAnnotationKind).First().Data;
                var info = model.GetSymbolInfo(node, cancellationToken);
                var actual = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
                if (actual != null && Corresponds(actual, expected!, model.Compilation))
                    continue;

                var site = node.AncestorsAndSelf().Select(n => n.GetAnnotations(CallPlan.SiteAnnotationKind).FirstOrDefault()?.Data).FirstOrDefault(d => d != null);
                if (site != null && bySite.TryGetValue(site, out var plan) && mismatches.All(m => m.Item1 != plan))
                    mismatches.Add((plan, node.ToString()));
            }
        }

        return mismatches;
    }

    /// <summary>
    /// Whether <paramref name="actual"/> is the member with the documentation ID
    /// <paramref name="expected"/>, or what it becomes once a type argument is known: an override
    /// of it, or the implementation of an interface member (a call through a type parameter's
    /// constraint binds to the type argument's own member).
    /// </summary>
    private static bool Corresponds(ISymbol actual, string expected, Compilation compilation)
    {
        for (var current = actual; current != null; current = Overridden(current))
        {
            if (InlinedBody.Identity(current) == expected)
                return true;
        }

        if (DocumentationCommentId.GetFirstSymbolForDeclarationId(expected, compilation) is not { ContainingType: { TypeKind: TypeKind.Interface } definingInterface } definition ||
            actual.ContainingType is not { } type)
        {
            return false;
        }

        return type.AllInterfaces
            .Where(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, definingInterface.OriginalDefinition))
            .SelectMany(i => i.GetMembers(definition.Name))
            .Any(member => InlinedBody.Identity(member) == expected &&
                SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, actual.OriginalDefinition));

        static ISymbol? Overridden(ISymbol symbol) => symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null
        };
    }

    /// <summary>Removes the marked method declaration from the text, with its comments.</summary>
    private static async Task<(Solution Solution, PendingChange Change)?> DeleteAsync(Solution solution, DocumentId documentId, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId)!;
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root?.GetAnnotatedNodes(DeleteAnnotationKind).FirstOrDefault() is not MethodDeclarationSyntax declaration)
            return null;

        var text = await document.GetTextAsync(cancellationToken);
        var span = DeclarationRemoval.Span(text, declaration);
        var lines = text.Lines.GetLinePositionSpan(span);
        var change = new PendingChange
        {
            File = document.FilePath ?? document.Name,
            ChangeType = ChangeKind.Modify,
            Description = $"Delete method '{declaration.Identifier.ValueText}'",
            BeforeSnippet = text.ToString(TextSpan.FromBounds(declaration.SpanStart, text.Lines.GetLineFromPosition(declaration.SpanStart).End)).Trim(),
            StartLine = lines.Start.Line + 1,
            EndLine = lines.End.Character == 0 ? lines.End.Line : lines.End.Line + 1
        };

        return (solution.WithDocumentText(documentId, DeclarationRemoval.Remove(text, [span])), change);
    }

    private static async Task<List<PendingChange>> CreatePendingChangesAsync(
        Solution solution,
        List<CallPlan> plans,
        PendingChange? deletion,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        foreach (var plan in plans.OrderBy(p => p.Document.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Invocation.SpanStart))
        {
            var root = await solution.GetDocument(plan.Document.Id)!.GetSyntaxRootAsync(cancellationToken);
            var site = plan.Site.ToString(CultureInfo.InvariantCulture);
            var after = root?.GetAnnotatedNodes(CallPlan.SiteAnnotationKind)
                .Where(n => n.GetAnnotations(CallPlan.SiteAnnotationKind).Any(a => a.Data == site))
                .Select(n => n.ToString().Trim());
            SyntaxNode before = plan.Form == CallForm.Expression ? plan.Invocation : plan.Statement!;
            var lines = before.GetLocation().GetLineSpan();

            changes.Add(new PendingChange
            {
                File = plan.Document.FilePath ?? plan.Document.Name,
                ChangeType = ChangeKind.Modify,
                Description = $"Inline the call to '{plan.Body.Method.Name}'" +
                    (plan.Namespaces.Count > 0 ? $" (adds using {string.Join(", ", plan.Namespaces)})" : ""),
                BeforeSnippet = before.ToString().Trim(),
                AfterSnippet = after == null ? null : string.Join("\n", after),
                StartLine = lines.StartLinePosition.Line + 1,
                EndLine = lines.EndLinePosition.Line + 1
            });
        }

        if (deletion != null)
            changes.Add(deletion);
        return changes;
    }
}
