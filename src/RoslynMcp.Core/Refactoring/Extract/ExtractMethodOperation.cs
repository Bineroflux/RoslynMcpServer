using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts the selected statements, or a selected expression, into a new method of the
/// containing type and replaces the selection with a call to it.
/// </summary>
/// <remarks>
/// <para>
/// The new method is an instance method when the selection uses instance members (implicitly
/// or through <c>this</c> / <c>base</c>) and uses them directly; it is <c>static</c> when the
/// selection uses no instance state or sits in a static context. <c>this</c> is never passed as
/// an argument, which in a struct would copy the value.
/// </para>
/// <para>
/// Roslyn's data-flow analysis of the selection decides the signature. Variables whose values
/// flow in become parameters. Variables the selection assigns and the code after it reads come
/// back through the return value: one is returned, several as a tuple that the call
/// deconstructs. A variable the selection assigns on some paths only is passed in as well.
/// Variables declared in the selection and used after it are declared at the call. A selection
/// that leaves the function on every path keeps its <c>return</c> statements, and the call is
/// returned. A selection with <c>await</c> becomes an <c>async</c> method that the call awaits.
/// An extracted expression is replaced by the call; the variables it assigns that are read
/// later are passed by <c>ref</c>, or <c>out</c>.
/// </para>
/// <para>
/// Refused are selections that cannot be moved as they are: <c>yield</c>; a <c>break</c>,
/// <c>continue</c> or <c>goto</c> that leaves the selection; a <c>return</c> on some paths only;
/// a label that a <c>goto</c> outside jumps to; a rethrow outside its <c>catch</c>; local
/// functions or query range variables declared outside it; a <c>using</c> declaration whose
/// scope goes on after it. Compiler errors the extraction introduces anyway are reported as
/// warnings.
/// </para>
/// </remarks>
public sealed class ExtractMethodOperation : RefactoringOperationBase<ExtractMethodParams>
{
    private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "private", "internal", "protected", "public", "private protected", "protected internal"
    };

    /// <summary>Marks the extracted method in the new tree.</summary>
    private const string MethodAnnotationKind = "RoslynMcp.ExtractMethod.Method";

    /// <summary>Marks the statements, or the expression, that call it.</summary>
    private const string CallAnnotationKind = "RoslynMcp.ExtractMethod.Call";

    /// <summary>
    /// Creates a new extract method operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public ExtractMethodOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractMethodParams @params)
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

        // "_" is a legal identifier, but a method named "_" captures every discard in the
        // containing type: `_ = Log(7)` and `out _` would bind to the method group.
        if (IdentifierNames.Validate(@params.MethodName, NameKind.Member, "methodName", ErrorCodes.InvalidNewName) == "_")
            throw new RefactoringException(ErrorCodes.InvalidNewName, "'_' is not a valid method name: existing discards (_ = ... / out _) would bind to the new method.");

        if (@params.StartLine < 1 || @params.EndLine < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line numbers must be >= 1.");

        if (@params.StartColumn < 1 || @params.EndColumn < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column numbers must be >= 1.");

        if (@params.StartLine > @params.EndLine ||
            (@params.StartLine == @params.EndLine && @params.StartColumn >= @params.EndColumn))
            throw new RefactoringException(ErrorCodes.InvalidSelectionRange, "Selection start must be before end.");

        if (!ValidVisibilities.Contains(@params.Visibility))
            throw new RefactoringException(ErrorCodes.InvalidVisibility, $"'{@params.Visibility}' is not a valid visibility modifier.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractMethodParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || model == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        var text = await document.GetTextAsync(cancellationToken);
        var start = TextPositionHelper.GetPosition(text, @params.StartLine, @params.StartColumn);
        var end = TextPositionHelper.GetPosition(text, @params.EndLine, @params.EndColumn);
        var selection = Select(root, Trim(text, TextSpan.FromBounds(start, end)), model, cancellationToken);
        var scope = Scope.Find(selection, model, cancellationToken);

        IdentifierNames.CheckNotAwaitInAsync(@params.MethodName, selection.First, "methodName");
        CheckSelection(selection, model, cancellationToken);
        CheckConstructorOnlyAssignments(selection, scope, model, cancellationToken);

        var flow = selection.Expression is { } expression
            ? AnalyzeExpression(expression, scope, model, cancellationToken)
            : AnalyzeStatements(selection.Statements, scope, model, cancellationToken);

        var isStatic = DecideStatic(@params.MakeStatic, flow, scope);
        var typeParameters = CollectTypeParameters(selection, flow, model, cancellationToken);
        CheckNameIsFree(IdentifierNames.ValueText(@params.MethodName), flow, scope, model, selection.First.SpanStart);

        var nullable = model.GetNullableContext(selection.First.SpanStart).AnnotationsEnabled();
        var method = BuildMethod(@params, selection, flow, scope, isStatic, typeParameters, nullable, model.Compilation);
        var call = BuildCall(@params.MethodName, flow, typeParameters);
        var languageVersion = ((CSharpParseOptions)root.SyntaxTree.Options).LanguageVersion;
        var newType = Replace(selection, scope, flow, call, method, nullable, languageVersion);

        var newSolution = await GeneratedCodeFormatter.FormatAsync(
            document.WithSyntaxRoot(root.ReplaceNode(scope.Type, newType)).Project.Solution, document, cancellationToken);
        var warnings = await IntroducedErrors.FindAsync(
            Context.Solution, newSolution, [document.Id], cancellationToken, matchLines: false);

        if (@params.Preview)
        {
            return await CreatePreviewAsync(operationId, @params, document, selection, newSolution, warnings, cancellationToken);
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
                Name = IdentifierNames.ValueText(@params.MethodName),
                FullyQualifiedName = $"{scope.TypeSymbol.ToDisplayString()}.{IdentifierNames.ValueText(@params.MethodName)}",
                Kind = Contracts.Enums.SymbolKind.Method
            },
            Warnings = warnings
        };
    }

    // ------------------------------------------------------------------------------------
    // Selection
    // ------------------------------------------------------------------------------------

    /// <summary>The selected code: statements of one statement list, or one expression.</summary>
    private sealed class Selection
    {
        public IReadOnlyList<StatementSyntax> Statements { get; init; } = [];

        public ExpressionSyntax? Expression { get; init; }

        public SyntaxNode First => (SyntaxNode?)Expression ?? Statements[0];

        public SyntaxNode Last => (SyntaxNode?)Expression ?? Statements[^1];

        public IEnumerable<SyntaxNode> Nodes => Expression != null ? [Expression] : Statements;

        public TextSpan Span => TextSpan.FromBounds(First.SpanStart, Last.Span.End);
    }

    /// <summary>The selection without the whitespace (indentation, line breaks) around it.</summary>
    private static TextSpan Trim(SourceText text, TextSpan span)
    {
        var start = span.Start;
        var end = span.End;
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;
        return TextSpan.FromBounds(start, end);
    }

    /// <summary>
    /// The statements or the expression the span selects. A span inside a block or a switch
    /// section selects the statements it touches; a whole function body selects its
    /// statements; an expression that is a statement on its own selects that statement.
    /// </summary>
    private static Selection Select(SyntaxNode root, TextSpan span, SemanticModel model, CancellationToken cancellationToken)
    {
        if (span.IsEmpty)
            throw new RefactoringException(ErrorCodes.EmptySelection, "No code selected for extraction.");

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        switch (node)
        {
            case BlockSyntax block when block.OpenBraceToken.Span.End <= span.Start && span.End <= block.CloseBraceToken.SpanStart:
                return StatementsOf(block.Statements, span);

            case SwitchSectionSyntax section:
                return StatementsOf(section.Statements, span);

            case BlockSyntax block when !IsStatementInList(block):
                return StatementsOf(block.Statements, block.Span);

            case ExpressionSyntax expression when expression.Parent is ExpressionStatementSyntax statement && statement.Expression == expression:
                return new Selection { Statements = [statement] };

            case ExpressionSyntax expression:
                CheckExpression(expression, model, cancellationToken);
                return new Selection { Expression = expression };

            case StatementSyntax statement:
                return new Selection { Statements = [statement] };

            case ArgumentSyntax argument:
                if (!argument.RefKindKeyword.IsKind(SyntaxKind.None))
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"'{argument}' passes a variable by reference; select the expression of an argument that is passed by value, or whole statements.");
                }

                CheckExpression(argument.Expression, model, cancellationToken);
                return new Selection { Expression = argument.Expression };

            case GlobalStatementSyntax:
            case CompilationUnitSyntax unit when unit.Members.OfType<GlobalStatementSyntax>().Any(g => g.Span.OverlapsWith(span)):
                throw new RefactoringException(
                    ErrorCodes.InvalidSelection,
                    "Top-level statements cannot be extracted into a method; move them into a method of a type first.");

            default:
                if (node is not MemberDeclarationSyntax and not CompilationUnitSyntax &&
                    node.FirstAncestorOrSelf<StatementSyntax>() is { } enclosing)
                {
                    return new Selection { Statements = [enclosing] };
                }

                throw new RefactoringException(
                    ErrorCodes.InvalidSelection,
                    "Select whole statements or an expression inside a method, accessor, constructor or lambda.");
        }
    }

    private static Selection StatementsOf(SyntaxList<StatementSyntax> statements, TextSpan span)
    {
        var selected = statements.Where(s => s.Span.OverlapsWith(span)).ToList();
        return selected.Count == 0
            ? throw new RefactoringException(ErrorCodes.EmptySelection, "The selection contains no statement.")
            : new Selection { Statements = selected };
    }

    /// <summary>Whether a block is a statement of its own rather than the body of a function or clause.</summary>
    private static bool IsStatementInList(BlockSyntax block) => block.Parent switch
    {
        BlockSyntax or SwitchSectionSyntax or GlobalStatementSyntax or LabeledStatementSyntax => true,
        IfStatementSyntax s => s.Statement == block,
        ElseClauseSyntax s => s.Statement == block,
        WhileStatementSyntax s => s.Statement == block,
        DoStatementSyntax s => s.Statement == block,
        ForStatementSyntax s => s.Statement == block,
        CommonForEachStatementSyntax s => s.Statement == block,
        UsingStatementSyntax s => s.Statement == block,
        LockStatementSyntax s => s.Statement == block,
        FixedStatementSyntax s => s.Statement == block,
        _ => false
    };

    /// <summary>Refuses an expression that is no value on its own or cannot become a call.</summary>
    private static void CheckExpression(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
    {
        if (expression is ThrowExpressionSyntax)
            throw new RefactoringException(ErrorCodes.InvalidSelection, "A throw expression cannot be extracted on its own; select the expression around it or whole statements.");

        if (IsWrittenTo(expression))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"'{expression}' is assigned or passed by reference here, so it cannot be replaced by a call; select the whole statement instead.");
        }

        if (IsInConstantContext(expression))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"'{expression}' must stay a constant here (attribute argument, case label, pattern, default value or const), and a method call is not one.");
        }

        if (expression.DescendantNodesAndSelf().Any(n =>
                n is MemberBindingExpressionSyntax or ElementBindingExpressionSyntax &&
                n.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } access &&
                !expression.Span.Contains(access.Span)))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"'{expression}' is part of a '?.' access; select the whole conditional access.");
        }

        if (model.GetOperation(expression, cancellationToken) is null or IDeclarationExpressionOperation or IDiscardOperation)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"'{expression}' is not a value of its own (a type, a member name, a method group or a declaration); select a whole expression or whole statements.");
        }
    }

    /// <summary>Whether the expression is the target of an assignment, increment, <c>ref</c> or address-of.</summary>
    private static bool IsWrittenTo(ExpressionSyntax expression)
    {
        SyntaxNode node = expression;
        while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
            node = parenthesized;

        return node.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == node,
            PrefixUnaryExpressionSyntax unary => unary.IsKind(SyntaxKind.PreIncrementExpression) ||
                unary.IsKind(SyntaxKind.PreDecrementExpression) || unary.IsKind(SyntaxKind.AddressOfExpression),
            PostfixUnaryExpressionSyntax unary => unary.IsKind(SyntaxKind.PostIncrementExpression) ||
                unary.IsKind(SyntaxKind.PostDecrementExpression),
            ArgumentSyntax { Parent: TupleExpressionSyntax tuple } argument =>
                !argument.RefKindKeyword.IsKind(SyntaxKind.None) || IsWrittenTo(tuple),
            ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None),
            RefExpressionSyntax => true,
            _ => false
        };
    }

    /// <summary>Whether the expression is in a place that requires a constant.</summary>
    private static bool IsInConstantContext(ExpressionSyntax expression)
    {
        foreach (var ancestor in expression.Ancestors())
        {
            switch (ancestor)
            {
                case AttributeArgumentSyntax:
                case CaseSwitchLabelSyntax:
                case ConstantPatternSyntax:
                case RelationalPatternSyntax:
                case ParameterSyntax:
                case EnumMemberDeclarationSyntax:
                    return true;
                case VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax local }:
                    return local.IsConst;
                case VariableDeclarationSyntax { Parent: FieldDeclarationSyntax field }:
                    return field.Modifiers.Any(SyntaxKind.ConstKeyword);
                case StatementSyntax:
                case MemberDeclarationSyntax:
                case AnonymousFunctionExpressionSyntax:
                    return false;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------------------------
    // Where the selection is
    // ------------------------------------------------------------------------------------

    /// <summary>The member, type and function around the selection.</summary>
    private sealed class Scope
    {
        /// <summary>The member of the type the selection is in; the new method goes after it.</summary>
        public required MemberDeclarationSyntax Member { get; init; }

        public required TypeDeclarationSyntax Type { get; init; }

        public required INamedTypeSymbol TypeSymbol { get; init; }

        /// <summary>The innermost function (method, accessor, local function, lambda); null in an initializer.</summary>
        public IMethodSymbol? Function { get; init; }

        public SyntaxNode? FunctionNode { get; init; }

        /// <summary>No <c>this</c> is available: a static member, a static lambda or local function, an initializer.</summary>
        public bool IsStaticContext { get; init; }

        /// <summary>A <c>readonly</c> member of a struct that is not itself <c>readonly</c>.</summary>
        public bool IsReadOnlyMember { get; init; }

        public bool IsUnsafe { get; init; }

        /// <summary><see cref="SyntaxKind.CheckedStatement"/> or <see cref="SyntaxKind.UncheckedStatement"/> around the selection, if any.</summary>
        public SyntaxKind CheckedKind { get; init; }

        public static Scope Find(Selection selection, SemanticModel model, CancellationToken cancellationToken)
        {
            SyntaxNode? functionNode = null;
            IMethodSymbol? function = null;
            IMethodSymbol? memberFunction = null;
            var staticContext = false;
            var unsafeContext = false;
            var checkedKind = SyntaxKind.None;

            foreach (var ancestor in selection.First.Ancestors())
            {
                switch (ancestor)
                {
                    case GlobalStatementSyntax:
                        throw new RefactoringException(
                            ErrorCodes.InvalidSelection,
                            "Top-level statements cannot be extracted into a method; move them into a method of a type first.");

                    case CheckedStatementSyntax statement when checkedKind == SyntaxKind.None:
                        checkedKind = statement.Kind();
                        break;

                    case CheckedExpressionSyntax checkedExpression when checkedKind == SyntaxKind.None:
                        checkedKind = checkedExpression.IsKind(SyntaxKind.CheckedExpression)
                            ? SyntaxKind.CheckedStatement
                            : SyntaxKind.UncheckedStatement;
                        break;

                    case UnsafeStatementSyntax:
                        unsafeContext = true;
                        break;

                    case AnonymousFunctionExpressionSyntax lambda:
                        staticContext |= lambda.Modifiers.Any(SyntaxKind.StaticKeyword);
                        if (functionNode == null)
                            (functionNode, function) = (lambda, model.GetSymbolInfo(lambda, cancellationToken).Symbol as IMethodSymbol);
                        break;

                    case LocalFunctionStatementSyntax local:
                        staticContext |= local.Modifiers.Any(SyntaxKind.StaticKeyword);
                        unsafeContext |= local.Modifiers.Any(SyntaxKind.UnsafeKeyword);
                        if (functionNode == null)
                            (functionNode, function) = (local, model.GetDeclaredSymbol(local, cancellationToken) as IMethodSymbol);
                        break;

                    case AccessorDeclarationSyntax accessor:
                        memberFunction = model.GetDeclaredSymbol(accessor, cancellationToken) as IMethodSymbol;
                        if (functionNode == null)
                            (functionNode, function) = (accessor, memberFunction);
                        break;

                    case ArrowExpressionClauseSyntax { Parent: BasePropertyDeclarationSyntax property }:
                        memberFunction = (model.GetDeclaredSymbol(property, cancellationToken) as IPropertySymbol)?.GetMethod;
                        if (functionNode == null)
                            (functionNode, function) = (ancestor, memberFunction);
                        break;

                    case ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax:
                        // Arguments to a base or this constructor: no instance exists yet.
                        staticContext = true;
                        break;

                    case BaseMethodDeclarationSyntax method:
                        memberFunction = model.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol;
                        if (functionNode == null)
                            (functionNode, function) = (method, memberFunction);
                        break;
                }

                if (ancestor is MemberDeclarationSyntax { Parent: TypeDeclarationSyntax type } member)
                {
                    var typeSymbol = model.GetDeclaredSymbol(type, cancellationToken)
                        ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve the containing type.");
                    var isStaticMember = member.Modifiers.Any(SyntaxKind.StaticKeyword) || member.Modifiers.Any(SyntaxKind.ConstKeyword);
                    var typeIsUnsafe = type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Any(t => t.Modifiers.Any(SyntaxKind.UnsafeKeyword));

                    return new Scope
                    {
                        Member = member,
                        Type = type,
                        TypeSymbol = typeSymbol,
                        Function = function,
                        FunctionNode = functionNode,
                        // An initializer of an instance field or property cannot use this either.
                        IsStaticContext = staticContext || isStaticMember || functionNode == null || typeSymbol.IsStatic,
                        IsReadOnlyMember = typeSymbol.IsValueType && !typeSymbol.IsReadOnly && memberFunction is { IsReadOnly: true, IsStatic: false },
                        IsUnsafe = (unsafeContext || member.Modifiers.Any(SyntaxKind.UnsafeKeyword)) && !typeIsUnsafe,
                        CheckedKind = checkedKind
                    };
                }
            }

            throw new RefactoringException(ErrorCodes.InvalidSelection, "Selection must be inside a member of a class, struct, record or interface.");
        }
    }

    // ------------------------------------------------------------------------------------
    // What cannot be moved
    // ------------------------------------------------------------------------------------

    private static void CheckSelection(Selection selection, SemanticModel model, CancellationToken cancellationToken)
    {
        var span = selection.Span;
        foreach (var node in selection.Nodes.SelectMany(OwnNodes))
        {
            switch (node)
            {
                case YieldStatementSyntax:
                    throw new RefactoringException(ErrorCodes.ContainsYield, "Cannot extract code containing yield statements.");

                case ThrowStatementSyntax { Expression: null } rethrow
                    when rethrow.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault() is not { } catchClause || !span.Contains(catchClause.Span):
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"'throw;' at line {Line(rethrow)} rethrows the exception of the catch clause around the selection, which the new method would not be in.");
            }
        }

        foreach (var name in selection.Nodes.SelectMany(n => n.DescendantNodesAndSelf()).OfType<SimpleNameSyntax>())
        {
            switch (model.GetSymbolInfo(name, cancellationToken).Symbol)
            {
                case IMethodSymbol { MethodKind: MethodKind.LocalFunction } local when !IsDeclaredIn(local, span):
                    throw new RefactoringException(
                        ErrorCodes.SelectionCrossesScopes,
                        $"The selection uses the local function '{local.Name}', which is declared outside it; the new method could not call it. Include the local function or make it a method first.");

                case IRangeVariableSymbol range when !IsDeclaredIn(range, span):
                    throw new RefactoringException(
                        ErrorCodes.SelectionCrossesScopes,
                        $"The selection uses the query range variable '{range.Name}', which is declared outside it; select the whole query instead.");
            }
        }

        if (selection.Statements.Count == 0 || selection.Statements[0].Parent is not { } list)
            return;

        var after = list.ChildNodes().OfType<StatementSyntax>().Where(s => s.SpanStart >= span.End).ToList();
        foreach (var local in selection.Statements.OfType<LocalFunctionStatementSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(local, cancellationToken);
            var before = list.ChildNodes().OfType<StatementSyntax>().Where(s => s.Span.End <= span.Start);
            if (after.Concat(before).SelectMany(s => s.DescendantNodesAndSelf()).OfType<SimpleNameSyntax>()
                .Any(n => n.Identifier.ValueText == local.Identifier.ValueText &&
                          SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, cancellationToken).Symbol, symbol)))
            {
                throw new RefactoringException(
                    ErrorCodes.SelectionCrossesScopes,
                    $"The local function '{local.Identifier.ValueText}' declared in the selection is used outside it; the new method would take it away.");
            }
        }

        if (after.Count > 0 && selection.Statements.FirstOrDefault(s => s is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 }) is { } usingDeclaration)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"The 'using' declaration at line {Line(usingDeclaration)} would dispose its object at the end of the new method instead of at the end of the block; extend the selection to the end of the block.");
        }
    }

    /// <summary>
    /// Refuses a selection in a constructor that assigns what only a constructor may assign: a
    /// <c>readonly</c> field or a get-only (or <c>init</c>) property of the type (CS0191, CS0200).
    /// </summary>
    private static void CheckConstructorOnlyAssignments(Selection selection, Scope scope, SemanticModel model, CancellationToken cancellationToken)
    {
        if (scope.FunctionNode is not ConstructorDeclarationSyntax)
            return;

        foreach (var operation in selection.Nodes.SelectMany(n => model.GetOperation(n, cancellationToken)?.DescendantsAndSelf() ?? []))
        {
            var targets = operation switch
            {
                IAssignmentOperation { Target: ITupleOperation tuple } => tuple.Elements,
                IAssignmentOperation assignment => [assignment.Target],
                IIncrementOrDecrementOperation increment => [increment.Target],
                IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument => [argument.Value],
                _ => []
            };

            foreach (var target in targets)
            {
                var member = target switch
                {
                    IFieldReferenceOperation { Field.IsReadOnly: true } field => field.Field,
                    IPropertyReferenceOperation { Property: { SetMethod: null or { IsInitOnly: true }, IsIndexer: false } property } => property,
                    _ => (ISymbol?)null
                };

                if (member != null && SymbolEqualityComparer.Default.Equals(member.ContainingType, scope.TypeSymbol))
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"The selection assigns '{member.Name}' at line {Line(target.Syntax)}, which only a constructor may assign (it is read-only); a method could not.");
                }
            }
        }
    }

    /// <summary>The node and its descendants that belong to the same function: nested lambdas and local functions are not entered.</summary>
    private static IEnumerable<SyntaxNode> OwnNodes(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax
            ? [node]
            : node.DescendantNodesAndSelf(n => n == node || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    private static bool IsDeclaredIn(ISymbol symbol, TextSpan span) =>
        symbol.DeclaringSyntaxReferences.Any(r => span.Contains(r.Span));

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // ------------------------------------------------------------------------------------
    // Data flow
    // ------------------------------------------------------------------------------------

    /// <summary>A variable the new method takes, returns, or declares.</summary>
    private sealed class Variable(ISymbol symbol, ITypeSymbol type)
    {
        public ISymbol Symbol { get; } = symbol;

        public string Name => Symbol.Name;

        public ITypeSymbol Type { get; } = type;

        /// <summary>How a parameter is passed.</summary>
        public RefKind PassBy { get; set; }

        public bool Scoped { get; set; }

        /// <summary>An output declared in the selection, so the call declares it.</summary>
        public bool DeclaredInside { get; init; }

        /// <summary>An <c>out</c> parameter for an <c>out var</c> of the extracted expression; the call declares it.</summary>
        public bool DeclaredByOutVar { get; init; }
    }

    /// <summary>The signature the selection needs.</summary>
    private sealed class Flow
    {
        public List<Variable> Parameters { get; } = [];

        /// <summary>Variables returned to the call (statements only): one, or a tuple.</summary>
        public List<Variable> Outputs { get; } = [];

        /// <summary>Variables declared before the selection that it assigns before reading: locals of the new method.</summary>
        public List<Variable> BodyLocals { get; } = [];

        /// <summary>Variables declared in the selection that are used after it but whose value is not: declared at the call.</summary>
        public List<Variable> CallSiteLocals { get; } = [];

        /// <summary>The statements leave the function on every path (return or throw), so the call is returned.</summary>
        public bool AlwaysLeaves { get; init; }

        /// <summary>What the method returns before an async wrap; null for nothing.</summary>
        public ITypeSymbol? ValueType { get; set; }

        public bool UsesInstance { get; init; }

        public bool ContainsAwait { get; init; }
    }

    private static Flow AnalyzeStatements(
        IReadOnlyList<StatementSyntax> statements,
        Scope scope,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var control = model.AnalyzeControlFlow(statements[0], statements[^1]);
        var data = model.AnalyzeDataFlow(statements[0], statements[^1]);
        if (control is not { Succeeded: true } || data is not { Succeeded: true })
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "The selected statements cannot be analyzed; select whole statements of one block.");
        }

        if (control.EntryPoints.FirstOrDefault() is { } entry)
        {
            throw new RefactoringException(
                ErrorCodes.MultipleEntryPoints,
                $"A goto outside the selection jumps to the label '{(entry as LabeledStatementSyntax)?.Identifier.ValueText}' at line {Line(entry)} inside it.");
        }

        foreach (var exit in control.ExitPoints.Where(e => e is not ReturnStatementSyntax))
        {
            var what = exit switch
            {
                BreakStatementSyntax => "break",
                ContinueStatementSyntax => "continue",
                GotoStatementSyntax => "goto",
                _ => exit.GetFirstToken().Text
            };
            throw new RefactoringException(
                ErrorCodes.UnresolvableControlFlow,
                $"'{what}' at line {Line(exit)} jumps out of the selection, which a method cannot do; include its target in the selection or leave the statement out.");
        }

        var alwaysLeaves = !control.EndPointIsReachable;
        if (!alwaysLeaves && control.ExitPoints.FirstOrDefault() is { } partialReturn)
        {
            throw new RefactoringException(
                ErrorCodes.MultipleExitPoints,
                $"The selection returns on some paths only (the return at line {Line(partialReturn)}), and continues on others; " +
                "select statements that return on every path or on none.");
        }

        var flow = new Flow
        {
            AlwaysLeaves = alwaysLeaves,
            UsesInstance = UsesInstance(statements, data, scope, model, cancellationToken),
            ContainsAwait = statements.Any(ContainsAwait)
        };

        var comparer = SymbolEqualityComparer.Default;
        bool IsVariable(ISymbol symbol) => IsMovableVariable(symbol, scope);
        var declaredInside = new HashSet<ISymbol>(data.VariablesDeclared.Where(IsVariable), comparer);
        var inputs = new HashSet<ISymbol>(data.DataFlowsIn.Where(IsVariable), comparer);

        // Declared before the selection, assigned in it, needed after it: returned and assigned at the call.
        var outputsBefore = data.DataFlowsOut
            .Concat(data.WrittenInside.Where(v => data.CapturedOutside.Contains(v, comparer)))
            .Where(v => IsVariable(v) && !declaredInside.Contains(v) && !IsRefLocal(v))
            .Distinct(comparer);
        foreach (var variable in outputsBefore)
        {
            if (data.DataFlowsIn.Contains(variable, comparer) || !data.AlwaysAssigned.Contains(variable, comparer))
            {
                if (!data.DefinitelyAssignedOnEntry.Contains(variable, comparer))
                {
                    throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"'{variable.Name}' is assigned on some paths of the selection only and has no value before it, so the new method could neither take nor return it.");
                }

                inputs.Add(variable);
            }

            flow.Outputs.Add(new Variable(variable, EffectiveType(variable, scope, model, cancellationToken)));
        }

        // Declared in the selection and used after it: declared at the call.
        foreach (var variable in declaredInside.Where(v => data.ReadOutside.Contains(v, comparer) || data.WrittenOutside.Contains(v, comparer)))
        {
            if (IsRefLocal(variable))
            {
                throw new RefactoringException(
                    ErrorCodes.CapturesRefLocal,
                    $"The ref local '{variable.Name}' is declared in the selection and used after it; a method cannot return it.");
            }

            if (!data.DataFlowsOut.Contains(variable, comparer))
            {
                flow.CallSiteLocals.Add(new Variable(variable, VariableType(variable)) { DeclaredInside = true });
                continue;
            }

            if (!data.DefinitelyAssignedOnExit.Contains(variable, comparer))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidSelection,
                    $"'{variable.Name}' is declared in the selection and assigned on some of its paths only, but read after it; the new method could not return it.");
            }

            flow.Outputs.Add(new Variable(variable, EffectiveType(variable, scope, model, cancellationToken)) { DeclaredInside = true });
        }

        if (alwaysLeaves && flow.Outputs.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MultipleExitPoints,
                $"The selection returns on every path but assigns '{flow.Outputs[0].Name}', which is read after it; the new method could not return both.");
        }

        AddParameters(flow, inputs, data, declaredInside, statements, scope, model, cancellationToken);

        flow.Outputs.Sort(ByDeclaration);
        flow.ValueType = alwaysLeaves
            ? FunctionValueType(scope)
            : flow.Outputs.Count switch
            {
                0 => null,
                1 => flow.Outputs[0].Type,
                _ => model.Compilation.CreateTupleTypeSymbol(
                    [.. flow.Outputs.Select(o => o.Type)],
                    [.. TupleElementNames(flow.Outputs)],
                    elementNullableAnnotations: [.. flow.Outputs.Select(o => o.Type.NullableAnnotation)])
            };

        return flow;
    }

    private static Flow AnalyzeExpression(ExpressionSyntax expression, Scope scope, SemanticModel model, CancellationToken cancellationToken)
    {
        var data = model.AnalyzeDataFlow(expression);
        if (data is not { Succeeded: true })
            throw new RefactoringException(ErrorCodes.InvalidSelection, $"'{expression}' cannot be analyzed; select a whole expression or whole statements.");

        var typeInfo = model.GetTypeInfo(expression, cancellationToken);
        var type = typeInfo.ConvertedType ?? typeInfo.Type;
        if (type is null or { TypeKind: TypeKind.Error })
            throw new RefactoringException(ErrorCodes.InvalidSelection, $"The type of '{expression}' cannot be determined; select a whole expression or whole statements.");

        if (!CanBeNamed(type))
            throw new RefactoringException(ErrorCodes.InvalidSelection, $"'{expression}' has a type that cannot be written ({type.ToDisplayString()}), so no method can return it.");

        var flow = new Flow
        {
            UsesInstance = UsesInstance([expression], data, scope, model, cancellationToken),
            ContainsAwait = ContainsAwait(expression),
            ValueType = type.SpecialType == SpecialType.System_Void ? null : type
        };

        var comparer = SymbolEqualityComparer.Default;
        bool IsVariable(ISymbol symbol) => IsMovableVariable(symbol, scope);
        var declaredInside = new HashSet<ISymbol>(data.VariablesDeclared.Where(IsVariable), comparer);
        var inputs = new HashSet<ISymbol>(data.DataFlowsIn.Where(IsVariable), comparer);
        var byReference = new Dictionary<ISymbol, RefKind>(comparer);

        // Variables declared before that the expression assigns and that are read later go by reference.
        foreach (var variable in data.WrittenInside.Where(v => IsVariable(v) && !declaredInside.Contains(v) && !IsRefLocal(v)))
        {
            if (!data.DataFlowsOut.Contains(variable, comparer) && !data.CapturedOutside.Contains(variable, comparer))
                continue;

            byReference[variable] = data.DefinitelyAssignedOnEntry.Contains(variable, comparer)
                ? RefKind.Ref
                : data.AlwaysAssigned.Contains(variable, comparer)
                    ? RefKind.Out
                    : throw new RefactoringException(
                        ErrorCodes.InvalidSelection,
                        $"'{expression}' assigns '{variable.Name}' on some paths only, and it has no value before; the new method could not pass it back.");
            inputs.Add(variable);
        }

        // Declared in the expression (out var) and used after it: an out parameter the call declares.
        var outVars = new List<Variable>();
        foreach (var variable in declaredInside.Where(v => data.ReadOutside.Contains(v, comparer) || data.WrittenOutside.Contains(v, comparer)))
        {
            if (variable.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not SingleVariableDesignationSyntax
                {
                    Parent: DeclarationExpressionSyntax { Parent: ArgumentSyntax argument }
                } || !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                throw new RefactoringException(
                    ErrorCodes.InvalidSelection,
                    $"'{variable.Name}' is declared in the selected expression and used after it; select whole statements instead.");
            }

            outVars.Add(new Variable(variable, VariableType(variable)) { PassBy = RefKind.Out, DeclaredByOutVar = true });
        }

        AddParameters(flow, inputs, data, declaredInside, [expression], scope, model, cancellationToken);
        foreach (var parameter in flow.Parameters.Where(p => byReference.ContainsKey(p.Symbol)))
            parameter.PassBy = byReference[parameter.Symbol];

        flow.Parameters.AddRange(outVars);
        flow.Parameters.Sort(ByDeclaration);
        CheckAsyncParameters(flow);
        return flow;
    }

    /// <summary>
    /// Fills in the parameters (the inputs, plus the ref locals the selection uses) and the
    /// locals the new method declares for itself (variables from before that the selection
    /// assigns before it reads them).
    /// </summary>
    private static void AddParameters(
        Flow flow,
        HashSet<ISymbol> inputs,
        DataFlowAnalysis data,
        HashSet<ISymbol> declaredInside,
        IReadOnlyList<SyntaxNode> selection,
        Scope scope,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var comparer = SymbolEqualityComparer.Default;
        // nameof(x) neither reads nor writes x, but the new method needs it in scope as well.
        var named = selection.SelectMany(n => n.DescendantNodesAndSelf())
            .OfType<IdentifierNameSyntax>()
            .Select(n => model.GetSymbolInfo(n, cancellationToken).Symbol)
            .OfType<ISymbol>()
            .Where(v => IsMovableVariable(v, scope) && !IsDeclaredIn(v, TextSpan.FromBounds(selection[0].SpanStart, selection[^1].Span.End)));
        var used = data.ReadInside.Concat(data.WrittenInside).Concat(named)
            .Where(v => IsMovableVariable(v, scope) && !declaredInside.Contains(v))
            .Distinct(comparer)
            .ToList();
        foreach (var variable in used.Where(v => !data.ReadInside.Contains(v, comparer) && !data.WrittenInside.Contains(v, comparer)))
        {
            if (data.DefinitelyAssignedOnEntry.Contains(variable, comparer))
                inputs.Add(variable);
        }
        var outputs = new HashSet<ISymbol>(flow.Outputs.Select(o => o.Symbol), comparer);

        foreach (var variable in used)
        {
            if (IsRefLocal(variable))
            {
                var refLocal = (ILocalSymbol)variable;
                flow.Parameters.Add(new Variable(variable, refLocal.Type)
                {
                    PassBy = refLocal.RefKind == RefKind.RefReadOnly ? RefKind.In : RefKind.Ref,
                    Scoped = refLocal.ScopedKind == ScopedKind.ScopedRef
                });
                continue;
            }

            if (!inputs.Contains(variable))
            {
                flow.BodyLocals.Add(new Variable(variable, VariableType(variable)));
                continue;
            }

            var parameter = new Variable(variable, EffectiveType(variable, scope, model, cancellationToken));
            if (variable is IParameterSymbol { RefKind: RefKind.In or RefKind.RefReadOnlyParameter } && !outputs.Contains(variable) && !flow.ContainsAwait)
                parameter.PassBy = RefKind.In;

            var scopedKind = variable switch
            {
                ILocalSymbol local => local.ScopedKind,
                IParameterSymbol p => p.ScopedKind,
                _ => ScopedKind.None
            };
            parameter.Scoped = parameter.PassBy == RefKind.None
                ? scopedKind == ScopedKind.ScopedValue && parameter.Type.IsRefLikeType
                : scopedKind == ScopedKind.ScopedRef;
            flow.Parameters.Add(parameter);
        }

        flow.Parameters.Sort(ByDeclaration);
        flow.BodyLocals.Sort(ByDeclaration);
        CheckAsyncParameters(flow);
    }

    private static void CheckAsyncParameters(Flow flow)
    {
        if (flow.ContainsAwait && flow.Parameters.FirstOrDefault(p => p.PassBy is RefKind.Ref or RefKind.Out) is { } byRef)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"The selection contains 'await', so the new method is async, but it would have to take '{byRef.Name}' by reference, which an async method cannot (CS1988).");
        }
    }

    /// <summary>A local or parameter the new method can take, return or declare; not <c>this</c>, not a captured primary-constructor parameter.</summary>
    private static bool IsMovableVariable(ISymbol symbol, Scope scope) => symbol switch
    {
        ILocalSymbol => true,
        IParameterSymbol { IsThis: true } => false,
        IParameterSymbol parameter => !IsCapturedPrimaryConstructorParameter(parameter, scope),
        _ => false
    };

    /// <summary>
    /// A primary-constructor parameter used in a member body: it is state of the instance, which
    /// the new method uses directly instead of taking a copy.
    /// </summary>
    private static bool IsCapturedPrimaryConstructorParameter(IParameterSymbol parameter, Scope scope) =>
        scope.Function != null &&
        parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor &&
        constructor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax) &&
        !SymbolEqualityComparer.Default.Equals(scope.Function, constructor);

    private static bool IsRefLocal(ISymbol symbol) => symbol is ILocalSymbol { RefKind: not RefKind.None };

    /// <summary>Whether the selection uses the instance: <c>this</c> or <c>base</c>, written or implied, or a captured primary-constructor parameter.</summary>
    private static bool UsesInstance(
        IEnumerable<SyntaxNode> nodes,
        DataFlowAnalysis data,
        Scope scope,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (data.ReadInside.Concat(data.WrittenInside).Any(s =>
                s is IParameterSymbol { IsThis: true } || s is IParameterSymbol p && IsCapturedPrimaryConstructorParameter(p, scope)))
        {
            return true;
        }

        return nodes.Any(node => model.GetOperation(node, cancellationToken)?.DescendantsAndSelf()
            .Any(o => o is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) == true);
    }

    /// <summary>Whether the node awaits (<c>await</c>, <c>await foreach</c>, <c>await using</c>) outside nested functions.</summary>
    private static bool ContainsAwait(SyntaxNode node) => OwnNodes(node).Any(n => n switch
    {
        AwaitExpressionSyntax => true,
        CommonForEachStatementSyntax statement => statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
        UsingStatementSyntax statement => statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
        LocalDeclarationStatementSyntax statement => statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
        _ => false
    });

    /// <summary>What the function returns to a <c>return</c> statement: the result type of an async function, nothing for an iterator.</summary>
    private static ITypeSymbol? FunctionValueType(Scope scope)
    {
        if (scope.Function is not { } function || function.IsIterator || function.ReturnsVoid)
            return null;

        if (!function.IsAsync)
            return function.ReturnType;

        return function.ReturnType is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } task ? task.TypeArguments[0] : null;
    }

    private static ITypeSymbol VariableType(ISymbol variable) => variable switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        _ => throw new InvalidOperationException($"'{variable.Name}' is not a variable.")
    };

    /// <summary>
    /// The type a variable is written with in the new signature. A <c>var</c> local is always
    /// declared nullable; it is written without <c>?</c> when it is never maybe-null where the
    /// function reads it (and it is read, or initialized with a not-null value), so the new
    /// method does not warn where the old code did not.
    /// </summary>
    private static ITypeSymbol EffectiveType(ISymbol variable, Scope scope, SemanticModel model, CancellationToken cancellationToken)
    {
        var type = VariableType(variable);
        if (variable is not ILocalSymbol local || !type.IsReferenceType || type.NullableAnnotation != NullableAnnotation.Annotated ||
            !IsDeclaredWithVar(local, cancellationToken) || (scope.FunctionNode ?? scope.Member) is not { } area)
        {
            return type;
        }

        var states = area.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(n => n.Identifier.ValueText == local.Name &&
                        !(n.Parent is AssignmentExpressionSyntax assignment && assignment.Left == n) &&
                        SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, cancellationToken).Symbol, local))
            .Select(n => model.GetTypeInfo(n, cancellationToken).Nullability.FlowState)
            .ToList();
        if (states.Count == 0 &&
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
        {
            states.Add(model.GetTypeInfo(initializer, cancellationToken).Nullability.FlowState);
        }

        return states.Count > 0 && states.All(s => s == NullableFlowState.NotNull)
            ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            : type;
    }

    private static bool IsDeclaredWithVar(ILocalSymbol local, CancellationToken cancellationToken) =>
        local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken).AncestorsAndSelf().FirstOrDefault(n =>
            n is VariableDeclarationSyntax or DeclarationExpressionSyntax or CommonForEachStatementSyntax or StatementSyntax) switch
        {
            VariableDeclarationSyntax declaration => declaration.Type.IsVar,
            DeclarationExpressionSyntax declaration => declaration.Type.IsVar,
            ForEachStatementSyntax loop => loop.Type.IsVar,
            _ => false
        };

    /// <summary>Whether a type can be written in a signature: no anonymous types in it.</summary>
    private static bool CanBeNamed(ITypeSymbol type) => type switch
    {
        { IsAnonymousType: true } => false,
        INamedTypeSymbol { IsImplicitlyDeclared: true, TypeKind: TypeKind.Delegate } => false,
        IArrayTypeSymbol array => CanBeNamed(array.ElementType),
        IPointerTypeSymbol pointer => CanBeNamed(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.All(CanBeNamed),
        _ => true
    };

    /// <summary>Tuple element names: the variables' names, except those a tuple element cannot have.</summary>
    private static IEnumerable<string?> TupleElementNames(IReadOnlyList<Variable> outputs) =>
        outputs.Select((output, index) =>
        {
            var name = output.Name;
            var reserved = name is "Rest" or "ToString" or "GetHashCode" or "Equals" or "GetType" or "CompareTo" ||
                (name.StartsWith("Item", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4), out var position) && position != index + 1);
            return reserved ? null : name;
        });

    private static int ByDeclaration(Variable left, Variable right) =>
        DeclarationPosition(left.Symbol).CompareTo(DeclarationPosition(right.Symbol));

    private static int DeclarationPosition(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue;

    // ------------------------------------------------------------------------------------
    // Signature
    // ------------------------------------------------------------------------------------

    private static bool DecideStatic(bool? makeStatic, Flow flow, Scope scope)
    {
        if (scope.IsStaticContext)
            return true;

        if (makeStatic == true && flow.UsesInstance)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterValue,
                "makeStatic is true, but the selection uses instance members; a static method could only reach them through a copy of 'this'. " +
                "Leave makeStatic out to get an instance method.");
        }

        return makeStatic ?? !flow.UsesInstance;
    }

    /// <summary>
    /// The type parameters of the enclosing method or local functions that the new method
    /// needs: those its signature or the selection mentions, and those their constraints do.
    /// </summary>
    private static List<ITypeParameterSymbol> CollectTypeParameters(Selection selection, Flow flow, SemanticModel model, CancellationToken cancellationToken)
    {
        var found = new List<ITypeParameterSymbol>();
        var span = selection.Span;

        void Add(ITypeSymbol? type)
        {
            switch (type)
            {
                case ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } typeParameter
                    when !found.Contains(typeParameter, SymbolEqualityComparer.Default) && !IsDeclaredIn(typeParameter, span):
                    found.Add(typeParameter);
                    foreach (var constraint in typeParameter.ConstraintTypes)
                        Add(constraint);
                    break;
                case IArrayTypeSymbol array:
                    Add(array.ElementType);
                    break;
                case IPointerTypeSymbol pointer:
                    Add(pointer.PointedAtType);
                    break;
                case INamedTypeSymbol named:
                    foreach (var argument in named.TypeArguments)
                        Add(argument);
                    Add(named.ContainingType);
                    break;
            }
        }

        foreach (var variable in flow.Parameters.Concat(flow.Outputs).Concat(flow.BodyLocals).Concat(flow.CallSiteLocals))
            Add(variable.Type);
        Add(flow.ValueType);

        foreach (var name in selection.Nodes.SelectMany(n => n.DescendantNodesAndSelf()).OfType<SimpleNameSyntax>())
        {
            if (model.GetSymbolInfo(name, cancellationToken).Symbol is ITypeParameterSymbol typeParameter)
                Add(typeParameter);
        }

        return found
            .OrderBy(t => t.DeclaringMethod is { } method ? DeclarationPosition(method) : int.MaxValue)
            .ThenBy(t => t.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Refuses a name that would clash: another member of the type with that name (other than
    /// an overload with different parameters), the type's own name, or a local, parameter or
    /// local function at the call that would hide the method.
    /// </summary>
    private static void CheckNameIsFree(string name, Flow flow, Scope scope, SemanticModel model, int callPosition)
    {
        if (scope.TypeSymbol.Name == name)
            throw new RefactoringException(ErrorCodes.NameCollision, $"A method cannot have the name of its type '{name}' (CS0542).");

        var parameterTypes = flow.Parameters.Select(p => (p.Type.ToDisplayString(), p.PassBy == RefKind.None ? RefKind.None : RefKind.Ref)).ToList();
        foreach (var member in scope.TypeSymbol.GetMembers(name))
        {
            if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
            {
                throw new RefactoringException(
                    ErrorCodes.NameCollision,
                    $"'{scope.TypeSymbol.Name}' already has a {member.Kind.ToString().ToLowerInvariant()} named '{name}'.");
            }

            if (method.Parameters.Select(p => (p.Type.ToDisplayString(), p.RefKind == RefKind.None ? RefKind.None : RefKind.Ref)).SequenceEqual(parameterTypes))
                throw new RefactoringException(ErrorCodes.NameCollision, $"'{scope.TypeSymbol.Name}' already has a method '{method.ToDisplayString()}' with these parameters.");
        }

        if (model.LookupSymbols(callPosition, name: name).FirstOrDefault(s =>
                s is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or IMethodSymbol { MethodKind: MethodKind.LocalFunction }) is { } hiding)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"The {hiding.Kind.ToString().ToLowerInvariant()} '{name}' is in scope where the method would be called and would hide it; choose another name.");
        }
    }

    private static MethodDeclarationSyntax BuildMethod(
        ExtractMethodParams @params,
        Selection selection,
        Flow flow,
        Scope scope,
        bool isStatic,
        IReadOnlyList<ITypeParameterSymbol> typeParameters,
        bool nullable,
        Compilation compilation)
    {
        // Types are written with `?` only where nullable annotations are enabled (CS8632) and
        // simplified to what the file's usings allow; Task is System.Threading.Tasks.Task
        // where no using allows the short name.
        var valueType = flow.ValueType ?? compilation.GetSpecialType(SpecialType.System_Void);
        var returnType = flow.ContainsAwait
            ? SyntaxGenerationHelper.ToAsyncReturnType(valueType, compilation, nullable)
            : SyntaxGenerationHelper.CreateTypeSyntax(valueType, nullable);

        var modifiers = VisibilityTokens(@params.Visibility);
        if (isStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        else if (scope.IsReadOnlyMember)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
        if (scope.IsUnsafe)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
        if (flow.ContainsAwait)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        var parameters = flow.Parameters.Select(p =>
        {
            var parameterModifiers = new List<SyntaxToken>();
            if (p.Scoped)
                parameterModifiers.Add(SyntaxFactory.Token(SyntaxKind.ScopedKeyword));
            if (RefKeyword(p.PassBy) is { } keyword)
                parameterModifiers.Add(SyntaxFactory.Token(keyword));
            return SyntaxFactory.Parameter(IdentifierNames.Token(p.Name))
                .WithModifiers(SyntaxFactory.TokenList(parameterModifiers))
                .WithType(SyntaxGenerationHelper.CreateTypeSyntax(p.Type, nullable));
        });

        var method = SyntaxFactory.MethodDeclaration(returnType, IdentifierNames.Token(@params.MethodName))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(BuildBody(selection, flow, scope, nullable));

        if (typeParameters.Count > 0)
        {
            method = method
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(
                    typeParameters.Select(t => SyntaxFactory.TypeParameter(IdentifierNames.Token(t.Name))))))
                .WithConstraintClauses(SyntaxGenerationHelper.CreateConstraintClauses(typeParameters, nullable));
        }

        return method.NormalizeWhitespace().WithAdditionalAnnotations(new SyntaxAnnotation(MethodAnnotationKind));
    }

    private static List<SyntaxToken> VisibilityTokens(string visibility) => visibility.ToLowerInvariant() switch
    {
        "private protected" => [SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)],
        "protected internal" => [SyntaxFactory.Token(SyntaxKind.ProtectedKeyword), SyntaxFactory.Token(SyntaxKind.InternalKeyword)],
        "internal" => [SyntaxFactory.Token(SyntaxKind.InternalKeyword)],
        "protected" => [SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)],
        "public" => [SyntaxFactory.Token(SyntaxKind.PublicKeyword)],
        _ => [SyntaxFactory.Token(SyntaxKind.PrivateKeyword)]
    };

    private static SyntaxKind? RefKeyword(RefKind refKind) => refKind switch
    {
        RefKind.Ref => SyntaxKind.RefKeyword,
        RefKind.Out => SyntaxKind.OutKeyword,
        RefKind.In => SyntaxKind.InKeyword,
        _ => null
    };

    /// <summary>
    /// The new method's body: its own locals, the selected statements (or the expression,
    /// returned), and the return of the outputs; inside the checked or unchecked block the
    /// selection was in.
    /// </summary>
    private static BlockSyntax BuildBody(Selection selection, Flow flow, Scope scope, bool nullable)
    {
        var statements = new List<StatementSyntax>();
        statements.AddRange(flow.BodyLocals.Select(local => DeclareLocal(local, nullable)));

        if (selection.Expression is { } expression)
        {
            // An out var whose variable the caller uses is now the out parameter.
            var outVars = flow.Parameters.Where(p => p.DeclaredByOutVar).Select(p => p.Symbol).ToList();
            var body = expression.ReplaceNodes(
                expression.DescendantNodesAndSelf().OfType<DeclarationExpressionSyntax>().Where(d =>
                    d.Designation is SingleVariableDesignationSyntax designation &&
                    outVars.Any(v => v.DeclaringSyntaxReferences.Any(r => r.Span == designation.Span))),
                (original, _) => IdentifierNames.Name(((SingleVariableDesignationSyntax)original.Designation).Identifier.ValueText)
                    .WithTriviaFrom(original));

            statements.Add(flow.ValueType == null
                ? SyntaxFactory.ExpressionStatement(body.WithoutTrivia())
                : SyntaxFactory.ReturnStatement(body.WithoutTrivia()));
        }
        else
        {
            statements.AddRange(selection.Statements.Select(PreserveInnerTrivia));
            if (!flow.AlwaysLeaves && flow.Outputs.Count > 0)
            {
                statements.Add(SyntaxFactory.ReturnStatement(flow.Outputs.Count == 1
                    ? IdentifierNames.Name(flow.Outputs[0].Name)
                    : SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(
                        flow.Outputs.Select(o => SyntaxFactory.Argument(IdentifierNames.Name(o.Name)))))));
            }
        }

        if (scope.CheckedKind != SyntaxKind.None)
            statements = [SyntaxFactory.CheckedStatement(scope.CheckedKind, SyntaxFactory.Block(statements))];

        return SyntaxFactory.Block(statements);
    }

    private static LocalDeclarationStatementSyntax DeclareLocal(Variable local, bool nullable) =>
        SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            SyntaxGenerationHelper.CreateTypeSyntax(local.Type, nullable),
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(IdentifierNames.Token(local.Name)))));

    // ------------------------------------------------------------------------------------
    // Call site
    // ------------------------------------------------------------------------------------

    /// <summary>The call, awaited when the method is async: <c>Build(prefix, ref count)</c>, <c>await LoadAsync()</c>.</summary>
    private static ExpressionSyntax BuildCall(string methodName, Flow flow, IReadOnlyList<ITypeParameterSymbol> typeParameters)
    {
        SimpleNameSyntax target = typeParameters.Count == 0
            ? IdentifierNames.Name(methodName)
            : SyntaxFactory.GenericName(IdentifierNames.Token(methodName), SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SeparatedList<TypeSyntax>(typeParameters.Select(t => IdentifierNames.Name(t.Name)))));

        var arguments = flow.Parameters.Select(p =>
        {
            ExpressionSyntax value = p.DeclaredByOutVar
                ? SyntaxFactory.DeclarationExpression(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingleVariableDesignation(IdentifierNames.Token(p.Name)))
                : IdentifierNames.Name(p.Name);
            var argument = SyntaxFactory.Argument(value);
            return p.PassBy is RefKind.Ref or RefKind.Out
                ? argument.WithRefKindKeyword(SyntaxFactory.Token(RefKeyword(p.PassBy)!.Value))
                : argument;
        });

        ExpressionSyntax call = SyntaxFactory.InvocationExpression(target, SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        if (flow.ContainsAwait)
            call = SyntaxFactory.AwaitExpression(call);

        return call.NormalizeWhitespace();
    }

    /// <summary>
    /// The containing type with the selection replaced by the call and the new method inserted
    /// after the member the selection is in.
    /// </summary>
    private static TypeDeclarationSyntax Replace(
        Selection selection,
        Scope scope,
        Flow flow,
        ExpressionSyntax call,
        MethodDeclarationSyntax method,
        bool nullable,
        LanguageVersion languageVersion)
    {
        var callAnnotation = new SyntaxAnnotation(CallAnnotationKind);
        SyntaxNode target;
        SyntaxNode replacement;

        if (selection.Expression is { } expression)
        {
            // An awaited call goes in parentheses, which the simplifier removes where they are not needed.
            var value = flow.ContainsAwait
                ? SyntaxFactory.ParenthesizedExpression(call).WithAdditionalAnnotations(Simplifier.Annotation)
                : call;
            target = expression;
            replacement = GeneratedCodeFormatter.Mark(value.WithTriviaFrom(expression)).WithAdditionalAnnotations(callAnnotation);
        }
        else
        {
            var statements = selection.Statements;
            var generated = BuildCallStatements(selection, scope, flow, call, nullable, languageVersion)
                .Select(s => GeneratedCodeFormatter.Mark(s).WithAdditionalAnnotations(callAnnotation))
                .ToList();

            // In a statement list the call takes the place (and the indentation and comments)
            // of the selected statements.
            var callStatements = generated.ToList();
            callStatements[0] = callStatements[0].WithLeadingTrivia(statements[0].GetLeadingTrivia());
            callStatements[^1] = callStatements[^1].WithTrailingTrivia(statements[^1].GetTrailingTrivia());

            switch (statements[0].Parent)
            {
                case BlockSyntax block:
                    target = block;
                    replacement = block.WithStatements(Splice(block.Statements, statements, callStatements));
                    break;
                case SwitchSectionSyntax section:
                    target = section;
                    replacement = section.WithStatements(Splice(section.Statements, statements, callStatements));
                    break;
                default:
                    // An embedded statement (the body of an if or a loop) takes several as a block.
                    target = statements[0];
                    replacement = generated.Count == 1
                        ? callStatements[0]
                        : GeneratedCodeFormatter.Mark(SyntaxFactory.Block(generated).WithTriviaFrom(statements[0]));
                    break;
            }
        }

        var memberIndex = scope.Type.Members.IndexOf(scope.Member);
        var newType = scope.Type.ReplaceNode(target, replacement);
        return newType.WithMembers(newType.Members.Insert(memberIndex + 1, GeneratedCodeFormatter.MarkInserted(method)));
    }

    private static SyntaxList<StatementSyntax> Splice(
        SyntaxList<StatementSyntax> list,
        IReadOnlyList<StatementSyntax> selected,
        IReadOnlyList<StatementSyntax> replacement)
    {
        var index = list.IndexOf(selected[0]);
        var result = list.ToList();
        result.RemoveRange(index, selected.Count);
        result.InsertRange(index, replacement);
        return SyntaxFactory.List(result);
    }

    /// <summary>
    /// The statements that replace the selection: declarations of the variables the code after
    /// it uses, then the call — returned, assigned, deconstructed or on its own.
    /// </summary>
    private static List<StatementSyntax> BuildCallStatements(
        Selection selection,
        Scope scope,
        Flow flow,
        ExpressionSyntax call,
        bool nullable,
        LanguageVersion languageVersion)
    {
        var statements = new List<StatementSyntax>();
        statements.AddRange(flow.CallSiteLocals.Select(local => DeclareLocal(local, nullable)));

        if (flow.AlwaysLeaves)
        {
            if (flow.ValueType != null)
            {
                statements.Add(SyntaxFactory.ReturnStatement(call));
            }
            else
            {
                statements.Add(SyntaxFactory.ExpressionStatement(call));
                if (!EndsFunctionBody(selection, scope))
                {
                    statements.Add(scope.Function is { IsIterator: true }
                        ? SyntaxFactory.YieldStatement(SyntaxKind.YieldBreakStatement)
                        : SyntaxFactory.ReturnStatement());
                }
            }
        }
        else if (flow.Outputs.Count == 0)
        {
            statements.Add(SyntaxFactory.ExpressionStatement(call));
        }
        else if (flow.Outputs.Count == 1)
        {
            var output = flow.Outputs[0];
            statements.Add(output.DeclaredInside
                ? SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(IdentifierNames.Token(output.Name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(call)))))
                : SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, IdentifierNames.Name(output.Name), call)));
        }
        else
        {
            ExpressionSyntax target;
            if (flow.Outputs.All(o => o.DeclaredInside))
            {
                target = SyntaxFactory.DeclarationExpression(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.ParenthesizedVariableDesignation(SyntaxFactory.SeparatedList<VariableDesignationSyntax>(
                        flow.Outputs.Select(o => SyntaxFactory.SingleVariableDesignation(IdentifierNames.Token(o.Name))))));
            }
            else
            {
                // Mixing declarations and assignments in one deconstruction needs C# 10;
                // before that the new variables are declared first.
                var mixed = languageVersion >= LanguageVersion.CSharp10;
                if (!mixed)
                    statements.AddRange(flow.Outputs.Where(o => o.DeclaredInside).Select(o => DeclareLocal(o, nullable)));

                target = SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(flow.Outputs.Select(o => SyntaxFactory.Argument(
                    o.DeclaredInside && mixed
                        ? SyntaxFactory.DeclarationExpression(SyntaxFactory.IdentifierName("var"), SyntaxFactory.SingleVariableDesignation(IdentifierNames.Token(o.Name)))
                        : IdentifierNames.Name(o.Name)))));
            }

            statements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, target, call)));
        }

        return statements.Select(s => s.NormalizeWhitespace()
                .WithLeadingTrivia(SyntaxFactory.ElasticMarker)
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed))
            .ToList();
    }

    /// <summary>Whether the selection ends the body of its function, so a void call needs no <c>return;</c> after it.</summary>
    private static bool EndsFunctionBody(Selection selection, Scope scope) =>
        selection.Statements[^1].Parent is BlockSyntax block &&
        block.Parent == scope.FunctionNode &&
        block.Statements.Last() == selection.Statements[^1];

    // ------------------------------------------------------------------------------------
    // Preview
    // ------------------------------------------------------------------------------------

    private static async Task<RefactoringResult> CreatePreviewAsync(
        Guid operationId,
        ExtractMethodParams @params,
        Document document,
        Selection selection,
        Solution newSolution,
        IReadOnlyList<string>? warnings,
        CancellationToken cancellationToken)
    {
        var newline = await LineEndings.GetAsync(document, cancellationToken);
        var root = await newSolution.GetDocument(document.Id)!.GetSyntaxRootAsync(cancellationToken);
        var call = string.Join(newline, root!.GetAnnotatedNodes(CallAnnotationKind).Select(n => n.ToString().Trim()));
        var method = root.GetAnnotatedNodes(MethodAnnotationKind).FirstOrDefault() is { } node
            ? Dedent(node.ToString(), node.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToString())
            : "";

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Preview = true,
            PendingChanges =
            [
                new PendingChange
                {
                    File = document.FilePath!,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = $"Extract method '{IdentifierNames.ValueText(@params.MethodName)}' from lines {@params.StartLine}-{@params.EndLine}",
                    StartLine = @params.StartLine,
                    EndLine = @params.EndLine,
                    BeforeSnippet = string.Join(newline, selection.Nodes.Select(n => n.ToString().Trim())),
                    AfterSnippet = $"// Call site replacement:{newline}{call}{newline}{newline}// New extracted method:{newline}{method}"
                }
            ],
            Warnings = warnings
        };
    }

    /// <summary>The text with <paramref name="indentation"/> removed from the start of each line that has it.</summary>
    private static string Dedent(string text, string indentation) =>
        indentation.Length == 0
            ? text
            : string.Join('\n', text.Split('\n').Select(line => line.StartsWith(indentation, StringComparison.Ordinal) ? line[indentation.Length..] : line));

    /// <summary>
    /// Preserves inner trivia (comments, etc.) within a statement while stripping only
    /// the outermost leading whitespace/newlines for formatting in the new method.
    /// </summary>
    /// <param name="statement">The statement to process.</param>
    /// <returns>The statement with preserved inner trivia and normalized outer formatting.</returns>
    /// <remarks>
    /// This method:
    /// <list type="bullet">
    ///   <item>Strips leading whitespace/newlines but preserves leading comments</item>
    ///   <item>Strips trailing whitespace/newlines but preserves trailing comments</item>
    ///   <item>Leaves all inner trivia (comments within the statement) intact</item>
    /// </list>
    /// </remarks>
    private static StatementSyntax PreserveInnerTrivia(StatementSyntax statement)
    {
        // Process leading trivia: preserve comments, strip pure whitespace at the start
        var leadingTrivia = statement.GetLeadingTrivia();
        var preservedLeading = new List<SyntaxTrivia>();
        var foundNonWhitespace = false;

        foreach (var trivia in leadingTrivia)
        {
            var kind = trivia.Kind();

            // Once we find a comment, preserve everything from there
            if (kind == SyntaxKind.SingleLineCommentTrivia ||
                kind == SyntaxKind.MultiLineCommentTrivia ||
                kind == SyntaxKind.SingleLineDocumentationCommentTrivia ||
                kind == SyntaxKind.MultiLineDocumentationCommentTrivia ||
                kind == SyntaxKind.RegionDirectiveTrivia ||
                kind == SyntaxKind.EndRegionDirectiveTrivia ||
                kind == SyntaxKind.PragmaWarningDirectiveTrivia)
            {
                foundNonWhitespace = true;
            }

            if (foundNonWhitespace)
            {
                preservedLeading.Add(trivia);
            }
            else if (kind != SyntaxKind.WhitespaceTrivia && kind != SyntaxKind.EndOfLineTrivia)
            {
                // Non-whitespace, non-comment: preserve it
                preservedLeading.Add(trivia);
                foundNonWhitespace = true;
            }
        }

        // Process trailing trivia: preserve comments, strip pure whitespace at the end
        var trailingTrivia = statement.GetTrailingTrivia();
        var preservedTrailing = new List<SyntaxTrivia>();

        foreach (var trivia in trailingTrivia)
        {
            var kind = trivia.Kind();

            if (kind == SyntaxKind.SingleLineCommentTrivia ||
                kind == SyntaxKind.MultiLineCommentTrivia)
            {
                preservedTrailing.Add(trivia);
            }
            else if (kind == SyntaxKind.EndOfLineTrivia && preservedTrailing.Count > 0)
            {
                // Keep newline after trailing comment
                preservedTrailing.Add(trivia);
            }
        }

        return statement
            .WithLeadingTrivia(preservedLeading)
            .WithTrailingTrivia(preservedTrailing);
    }
}
