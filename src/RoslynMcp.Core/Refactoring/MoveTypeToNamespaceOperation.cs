using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Changes the namespace of a type, updating all references across the solution, and with
/// <c>updateFileLocation</c> moves its file into the matching folder when that is safe
/// (see <see cref="NamespaceFolderMove.Plan(Document, SyntaxNode, string, Solution)"/>).
/// </summary>
public sealed class MoveTypeToNamespaceOperation : RefactoringOperationBase<MoveTypeToNamespaceParams>
{
    /// <summary>
    /// Creates a new move type to namespace operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public MoveTypeToNamespaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        MoveTypeToNamespaceParams @params,
        CancellationToken cancellationToken)
    {
        // Resolve symbol
        var resolution = await TypeResolver.FindTypeInFileAsync(
            @params.SourceFile,
            @params.SymbolName,
            @params.Line,
            cancellationToken);

        // Validate namespace change
        await ValidateNamespaceChangeAsync(@params, resolution, cancellationToken);

        // Find all references
        var references = await ReferenceTracker.FindAllReferencesAsync(
            resolution.Symbol,
            cancellationToken);

        // Compute changes
        var (newSolution, changeStats) = await ComputeChangesAsync(
            @params,
            resolution,
            references,
            cancellationToken);

        var move = @params.UpdateFileLocation
            ? NamespaceFolderMove.Plan(resolution.Document, resolution.Declaration, @params.TargetNamespace, Context.Solution)
            : null;

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, resolution, changeStats, move);
        }

        // Commit changes. The namespace change is written in place, so the file keeps its
        // encoding; the move below is a plain file system move.
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        var sourcePath = resolution.Document.FilePath!;
        var filesModified = commitResult.FilesModified.ToList();
        var filesCreated = commitResult.FilesCreated.ToList();
        var filesDeleted = commitResult.FilesDeleted.ToList();
        var warning = FileNotMovedWarning(move?.SkipReason);
        string? movedTo = null;
        if (move?.TargetPath != null)
        {
            var outcome = (await NamespaceFolderMove.MoveAsync(
                Context, [new PlannedMove(resolution.Document.Project.FilePath!, sourcePath, move)], cancellationToken))[0];
            (movedTo, var problem) = (outcome.MovedTo, outcome.Problem);
            warning = problem == null ? null : RefactoringError.Create("PARTIAL_SUCCESS", problem);
        }

        if (movedTo != null)
        {
            filesModified.RemoveAll(f => PathResolver.PathsEqual(f, sourcePath));
            filesCreated.Add(movedTo);
            filesDeleted.Add(sourcePath);
        }

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted
            },
            Symbol = CreateSymbolInfo(resolution, @params.TargetNamespace, movedTo),
            ReferencesUpdated = references.TotalReferenceCount,
            UsingDirectivesAdded = changeStats.UsingsAdded,
            UsingDirectivesRemoved = changeStats.UsingsRemoved,
            Error = warning
        };
    }

    /// <inheritdoc />
    protected override void ValidateParams(MoveTypeToNamespaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.TargetNamespace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "targetNamespace is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        IdentifierNames.ValidateNamespace(@params.TargetNamespace, "targetNamespace");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
    }

    private async Task ValidateNamespaceChangeAsync(
        MoveTypeToNamespaceParams @params,
        SymbolResolutionResult resolution,
        CancellationToken cancellationToken)
    {
        var currentNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();

        if (currentNamespace == @params.TargetNamespace)
        {
            throw new RefactoringException(
                ErrorCodes.SameLocation,
                $"Type is already in namespace '{@params.TargetNamespace}'.");
        }

        // Check for name collision in target namespace
        var targetType = await TypeResolver.FindTypeByNameAsync(
            $"{@params.TargetNamespace}.{resolution.Symbol.Name}",
            cancellationToken);

        if (targetType != null && !SymbolEqualityComparer.Default.Equals(targetType, resolution.Symbol))
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"A type named '{resolution.Symbol.Name}' already exists in namespace '{@params.TargetNamespace}'.",
                suggestions: ["Rename the type before moving", "Choose a different target namespace"]);
        }
    }

    private async Task<(Solution, ChangeStats)> ComputeChangesAsync(
        MoveTypeToNamespaceParams @params,
        SymbolResolutionResult resolution,
        ReferenceSearchResult references,
        CancellationToken cancellationToken)
    {
        var solution = Context.Solution;
        var oldNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();
        var newNamespace = @params.TargetNamespace;
        var stats = new ChangeStats();

        // Update the type's namespace declaration
        solution = await UpdateTypeNamespaceAsync(
            solution,
            resolution,
            newNamespace,
            cancellationToken);

        // Update using directives in all referencing documents
        foreach (var (docId, _) in references.ReferencesByDocument)
        {
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;

            var (newDoc, added, removed) = await UpdateUsingDirectivesAsync(
                doc,
                oldNamespace,
                newNamespace,
                cancellationToken);

            solution = newDoc.Project.Solution;
            stats.UsingsAdded += added;
            stats.UsingsRemoved += removed;
        }

        // When the type was the last one in its namespace, the namespace is gone: imports of it
        // stop binding (CS0234) and are removed, and static usings and aliases of the type (or of
        // the namespace) follow it to the new one. Imports that still bind stay.
        if (!resolution.Symbol.ContainingNamespace.IsGlobalNamespace)
        {
            var stale = await StaleUsingDirectives.FixAsync(
                Context.Solution,
                solution,
                oldNamespace,
                symbol => NewName(symbol, resolution.Symbol, oldNamespace, newNamespace),
                cancellationToken);
            solution = stale.Solution;
            stats.UsingsRemoved += stale.Removed;
        }

        return (solution, stats);
    }

    /// <summary>
    /// Where a namespace or type a static using or alias named is after the move: the old namespace
    /// itself is the new one, the moved type (or a type nested in it, constructed or not) is in the
    /// new namespace; anything else did not move (null).
    /// </summary>
    private static string? NewName(ISymbol symbol, INamedTypeSymbol moved, string oldNamespace, string newNamespace)
    {
        if (symbol is INamespaceSymbol ns)
            return ns.ToDisplayString() == oldNamespace ? newNamespace : null;
        if (symbol is not INamedTypeSymbol type)
            return null;

        var outermost = type.OriginalDefinition;
        while (outermost.ContainingType != null)
            outermost = outermost.ContainingType;
        if (outermost.MetadataName != moved.MetadataName || outermost.ContainingNamespace.ToDisplayString() != oldNamespace)
            return null;

        var full = type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        return newNamespace + full[oldNamespace.Length..];
    }

    /// <summary>
    /// Puts the type's declaration in <paramref name="newNamespace"/>, as text changes that leave
    /// the rest of the file as it is:
    /// <list type="bullet">
    ///   <item>in one namespace declaration (file-scoped or a block at file level): its name changes;</item>
    ///   <item>in nested blocks that hold nothing else (<c>namespace A { namespace B { T } }</c>): they
    ///     become one block of the target namespace;</item>
    ///   <item>in nested blocks where an outer one holds more (<c>namespace A { X; namespace B { T } }</c>):
    ///     the blocks around the type leave it and a block of the target namespace after the outermost
    ///     one takes the type;</item>
    ///   <item>in the global namespace: a file-scoped declaration when the type is the file's only
    ///     member and the language version has them, else a block around the type.</item>
    /// </list>
    /// A namespace declaration moved or renamed this way keeps the using directives that applied to
    /// the type, written fully qualified where they relied on the old enclosing namespace. Refused
    /// (<see cref="ErrorCodes.NamespaceBlockShared"/>) when the type's own declaration declares
    /// anything else, which would change namespace with it.
    /// </summary>
    private async Task<Solution> UpdateTypeNamespaceAsync(
        Solution solution,
        SymbolResolutionResult resolution,
        string newNamespace,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(resolution.Document.Id);
        if (document == null) return solution;

        var text = await document.GetTextAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze the source file.");
        var lineEnding = await LineEndings.GetAsync(document, cancellationToken);
        var type = resolution.Declaration;

        // The namespace declarations around the type, innermost first.
        var chain = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().ToList();
        if (chain.Count == 0)
        {
            var fileScoped = type.Parent is CompilationUnitSyntax { Members.Count: 1 } &&
                document.Project.ParseOptions is CSharpParseOptions { LanguageVersion: >= LanguageVersion.CSharp10 };
            return solution.WithDocumentText(document.Id, text.WithChanges(WrapInNamespace(text, type, newNamespace, lineEnding, fileScoped)));
        }

        // The namespace changes by renaming or moving the declaration around the type, which
        // changes it for everything that declaration declares: another type, an enum, a delegate
        // or a nested namespace would move along, and the code using them would no longer compile.
        var others = chain[0].Members.Where(m => !ReferenceEquals(m, type)).ToList();
        if (others.Count > 0)
        {
            var names = string.Join(", ", others.Take(3).Select(DescribeMember)) + (others.Count > 3 ? ", ..." : "");
            throw new RefactoringException(
                ErrorCodes.NamespaceBlockShared,
                $"'{resolution.Symbol.Name}' shares its namespace declaration in '{Path.GetFileName(document.FilePath)}' " +
                $"with {names}, which would change namespace with it. Move '{resolution.Symbol.Name}' to a file of its own " +
                "with move_type_to_file first, then change its namespace.",
                suggestions: [$"Run move_type_to_file for '{resolution.Symbol.Name}', then move_type_to_namespace on the new file"]);
        }

        // The declarations from the type's outwards that hold only the next one in.
        var exclusive = 1;
        while (exclusive < chain.Count && chain[exclusive].Members.Count == 1)
            exclusive++;

        IEnumerable<TextChange> changes;
        if (chain.Count == 1)
        {
            changes = [new TextChange(chain[0].Name.Span, newNamespace), .. QualifyUsings(chain[0], model)];
        }
        else
        {
            var header = HeaderLines(chain, model, lineEnding);
            changes = exclusive == chain.Count
                ? MergeIntoOutermost(text, chain, type, newNamespace, header, lineEnding)
                : MoveOutOfOuterBlock(text, chain, exclusive, type, newNamespace, header, lineEnding);
        }

        return solution.WithDocumentText(document.Id, text.WithChanges(changes));
    }

    /// <summary>
    /// <c>namespace A { namespace B { T } }</c> → <c>namespace Target { T }</c>: the outermost
    /// block takes the target's name, and its body becomes the using directives of every level
    /// (<paramref name="header"/>) and the type, re-indented one level in.
    /// </summary>
    private static IEnumerable<TextChange> MergeIntoOutermost(
        SourceText text,
        List<BaseNamespaceDeclarationSyntax> chain,
        TypeDeclarationSyntax type,
        string newNamespace,
        string header,
        string lineEnding)
    {
        var outermost = (NamespaceDeclarationSyntax)chain[^1];
        var memberIndentation = GeneratedCodeFormatter.IndentationOf(outermost.Members[0]);
        var body = lineEnding +
            Indent(header, memberIndentation, lineEnding) +
            TypeText(text, type, memberIndentation, lineEnding) +
            GeneratedCodeFormatter.IndentationOf(outermost);

        return
        [
            new TextChange(outermost.Name.Span, newNamespace),
            new TextChange(TextSpan.FromBounds(outermost.OpenBraceToken.Span.End, outermost.CloseBraceToken.SpanStart), body),
        ];
    }

    /// <summary>
    /// <c>namespace A { X; namespace B { T } }</c> → <c>namespace A { X; }</c> and, after it,
    /// <c>namespace Target { T }</c>: the blocks that hold only the type leave their parent (with the
    /// comment lines above them), and a block of the target namespace after the outermost
    /// declaration takes the type and the using directives of every level (<paramref name="header"/>).
    /// </summary>
    private static IEnumerable<TextChange> MoveOutOfOuterBlock(
        SourceText text,
        List<BaseNamespaceDeclarationSyntax> chain,
        int exclusive,
        TypeDeclarationSyntax type,
        string newNamespace,
        string header,
        string lineEnding)
    {
        var leaving = chain[exclusive - 1];
        var outermost = chain[^1];
        var removal = DeclarationRemoval.Span(text, leaving);

        // The comment lines above the leaving block go with it.
        var leavingLine = text.Lines.GetLineFromPosition(leaving.SpanStart);
        var comments = new StringBuilder();
        var leavingIndentation = GeneratedCodeFormatter.IndentationOf(leaving);
        foreach (var line in text.Lines.Where(l => l.Start >= removal.Start && l.Start < leavingLine.Start))
        {
            var content = line.ToString();
            if (content.Trim().Length > 0)
                comments.Append(content.StartsWith(leavingIndentation, StringComparison.Ordinal) ? content[leavingIndentation.Length..] : content.TrimStart()).Append(lineEnding);
        }

        var indentation = GeneratedCodeFormatter.IndentationOf(outermost);
        var memberIndentation = indentation + IndentUnit(outermost, indentation);
        var outermostLine = text.Lines.GetLineFromPosition(outermost.Span.End);
        var atEndOfFile = outermostLine.EndIncludingLineBreak == outermostLine.End;
        var block = (atEndOfFile ? lineEnding : "") + lineEnding +
            Indent(comments.ToString(), indentation, lineEnding, blankLineAfter: false) +
            indentation + "namespace " + newNamespace + lineEnding +
            indentation + "{" + lineEnding +
            Indent(header, memberIndentation, lineEnding) +
            TypeText(text, type, memberIndentation, lineEnding) +
            indentation + "}" + lineEnding;

        return
        [
            new TextChange(removal, ""),
            new TextChange(new TextSpan(outermostLine.EndIncludingLineBreak, 0), block),
        ];
    }

    /// <summary>
    /// A type in the global namespace gets a declaration of <paramref name="newNamespace"/>: a
    /// file-scoped one above it (<paramref name="fileScoped"/>), or a block around it, re-indented
    /// one level in, that leaves the file's other members where they are.
    /// </summary>
    private static IEnumerable<TextChange> WrapInNamespace(
        SourceText text,
        TypeDeclarationSyntax type,
        string newNamespace,
        string lineEnding,
        bool fileScoped)
    {
        // From the line the type's comments (or the type) start on; the blank lines before stay.
        var contentStart = ContentStart(type);
        var line = text.Lines.GetLineFromPosition(contentStart);
        var startsLine = text.ToString(TextSpan.FromBounds(line.Start, contentStart)).Trim().Length == 0;
        var start = startsLine ? line.Start : contentStart;
        var before = startsLine ? "" : lineEnding;

        if (fileScoped)
            return [new TextChange(new TextSpan(start, 0), before + "namespace " + newNamespace + ";" + lineEnding + lineEnding)];

        var indentation = GeneratedCodeFormatter.IndentationOf(type);
        var memberIndentation = indentation + IndentUnit(type, indentation);
        var block = before +
            indentation + "namespace " + newNamespace + lineEnding +
            indentation + "{" + lineEnding +
            TypeText(text, type, memberIndentation, lineEnding) +
            indentation + "}" + lineEnding;

        return [new TextChange(TextSpan.FromBounds(start, type.FullSpan.End), block)];
    }

    /// <summary>
    /// The using directives (and extern aliases) that applied to the type from every namespace
    /// declaration around it, outermost first, one per line without indentation: they go into the
    /// declaration that takes the type, where they resolve against the new namespace, so a name
    /// that relied on the old enclosing namespace (<c>using Models;</c> for <c>App.Models</c>) is
    /// written fully qualified.
    /// </summary>
    private static string HeaderLines(List<BaseNamespaceDeclarationSyntax> chain, SemanticModel model, string lineEnding)
    {
        var lines = new List<string>();
        foreach (var declaration in Enumerable.Reverse(chain))
        {
            lines.AddRange(declaration.Externs.Select(e => e.WithoutTrivia().ToString()));
            foreach (var directive in declaration.Usings)
            {
                var qualified = QualifiedName(directive, model);
                var written = qualified == null
                    ? directive.WithoutTrivia().ToString()
                    : directive.WithNamespaceOrType(SyntaxFactory.ParseTypeName(qualified).WithTriviaFrom(directive.NamespaceOrType)).WithoutTrivia().ToString();
                if (!lines.Contains(written))
                    lines.Add(written);
            }
        }

        return string.Concat(lines.Select(l => l + lineEnding));
    }

    /// <summary>
    /// The using directives of a declaration whose name changes, each written fully qualified
    /// where it relied on the old enclosing namespace.
    /// </summary>
    private static IEnumerable<TextChange> QualifyUsings(BaseNamespaceDeclarationSyntax declaration, SemanticModel model) =>
        declaration.Usings
            .Select(directive => (directive, qualified: QualifiedName(directive, model)))
            .Where(d => d.qualified != null)
            .Select(d => new TextChange(d.directive.NamespaceOrType.Span, d.qualified!));

    /// <summary>
    /// What a using directive names, fully qualified, when it is written otherwise (relative to an
    /// enclosing namespace); null when it is written so already, is alias-qualified or does not bind.
    /// </summary>
    private static string? QualifiedName(UsingDirectiveSyntax directive, SemanticModel model)
    {
        var written = directive.NamespaceOrType.ToString();
        if (written.Contains("::", StringComparison.Ordinal))
            return null;

        var symbol = model.GetSymbolInfo(directive.NamespaceOrType).Symbol;
        if (symbol is not (INamespaceSymbol or ITypeSymbol))
            return null;

        var full = symbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        return full == written ? null : full;
    }

    /// <summary>
    /// The type as written, from the line its comments start on (so without the blank lines before
    /// it) to the end of its line, ending in a line break. Its lines move from the indentation of
    /// its first line to <paramref name="indentation"/> (lines indented less stay as they are);
    /// lines inside a multi-line string are not touched.
    /// </summary>
    private static string TypeText(SourceText text, TypeDeclarationSyntax type, string indentation, string lineEnding)
    {
        var root = type.SyntaxTree.GetRoot();
        var from = GeneratedCodeFormatter.IndentationOf(type);
        var contentStart = ContentStart(type);
        var start = text.Lines.GetLineFromPosition(contentStart).Start;
        if (text.ToString(TextSpan.FromBounds(start, contentStart)).Trim().Length > 0)
            start = contentStart;
        var end = type.FullSpan.End;

        var moved = new StringBuilder();
        var first = text.Lines.GetLineFromPosition(start).LineNumber;
        var last = text.Lines.GetLineFromPosition(Math.Max(start, end - 1)).LineNumber;
        for (var number = first; number <= last; number++)
        {
            var line = text.Lines[number];
            var piece = text.ToString(TextSpan.FromBounds(Math.Max(line.Start, start), Math.Min(line.EndIncludingLineBreak, end)));
            var token = root.FindToken(line.Start);
            var insideToken = token.SpanStart < line.Start && line.Start < token.Span.End;
            if (!insideToken && piece.Trim().Length > 0 && piece.StartsWith(from, StringComparison.Ordinal))
                piece = indentation + piece[from.Length..];
            moved.Append(piece);
        }

        return moved.Length > 0 && moved[^1] == '\n' ? moved.ToString() : moved.Append(lineEnding).ToString();
    }

    /// <summary>Where the type's comments start, or the type itself when there are none.</summary>
    private static int ContentStart(TypeDeclarationSyntax type)
    {
        // FullSpan: a documentation comment's Span starts after its `///`.
        var content = type.GetLeadingTrivia().FirstOrDefault(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia));
        return content == default ? type.SpanStart : content.FullSpan.Start;
    }

    /// <summary>
    /// Each line of <paramref name="lines"/> prefixed with <paramref name="indentation"/>, and a
    /// blank line after them when there are any and <paramref name="blankLineAfter"/>.
    /// </summary>
    private static string Indent(string lines, string indentation, string lineEnding, bool blankLineAfter = true)
    {
        if (lines.Length == 0)
            return "";

        var split = lines.Split(lineEnding, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(split.Select(l => indentation + l + lineEnding)) + (blankLineAfter ? lineEnding : "");
    }

    /// <summary>
    /// One level of indentation as the file writes it below <paramref name="container"/>: the
    /// indentation of its first member's line beyond its own; four spaces when that tells nothing.
    /// </summary>
    private static string IndentUnit(SyntaxNode container, string indentation)
    {
        var first = container switch
        {
            BaseNamespaceDeclarationSyntax ns => ns.Members.FirstOrDefault() as SyntaxNode,
            TypeDeclarationSyntax t => t.Members.FirstOrDefault(),
            _ => null,
        };
        if (first != null)
        {
            var memberIndentation = GeneratedCodeFormatter.IndentationOf(first);
            if (memberIndentation.Length > indentation.Length && memberIndentation.StartsWith(indentation, StringComparison.Ordinal))
                return memberIndentation[indentation.Length..];
        }

        return "    ";
    }

    /// <summary>How a member sharing the moved type's namespace declaration is named in the refusal.</summary>
    private static string DescribeMember(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax type => $"'{type.Identifier.Text}'",
        DelegateDeclarationSyntax @delegate => $"'{@delegate.Identifier.Text}'",
        BaseNamespaceDeclarationSyntax nested => $"namespace '{nested.Name}'",
        _ => member.Kind().ToString(),
    };

    private async Task<(Document, int added, int removed)> UpdateUsingDirectivesAsync(
        Document document,
        string oldNamespace,
        string newNamespace,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return (document, 0, 0);

        var compilationUnit = root as CompilationUnitSyntax;
        if (compilationUnit == null) return (document, 0, 0);

        var lineEnding = await LineEndings.GetAsync(document, cancellationToken);

        var usings = compilationUnit.Usings.ToList();
        var hasOldUsing = usings.Any(u => u.Name?.ToString() == oldNamespace);
        var hasNewUsing = usings.Any(u => u.Name?.ToString() == newNamespace);

        var added = 0;
        var removed = 0;

        // Add using for new namespace if not present, ending in the file's line ending; a
        // file without usings gets a blank line between the new using and its code.
        if (!hasNewUsing)
        {
            var newUsing = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName(newNamespace).WithLeadingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(Enumerable.Repeat(SyntaxFactory.EndOfLine(lineEnding), usings.Count == 0 ? 2 : 1));
            usings.Add(newUsing);
            added = 1;
        }

        // Optionally remove old using if no other types from that namespace are used
        // For simplicity, we keep the old using directive (it's harmless)

        var newCompilationUnit = compilationUnit.WithUsings(SyntaxFactory.List(usings));
        var newDoc = document.WithSyntaxRoot(newCompilationUnit);

        return (newDoc, added, removed);
    }

    private static RefactoringError? FileNotMovedWarning(string? skipReason) =>
        skipReason == null ? null : RefactoringError.Create("PARTIAL_SUCCESS", NotMoved(skipReason));

    private static string NotMoved(string reason) => NamespaceFolderMove.NotMoved(reason);

    private Contracts.Models.SymbolInfo CreateSymbolInfo(SymbolResolutionResult resolution, string newNamespace, string? movedTo = null)
    {
        var oldNamespace = resolution.Symbol.ContainingNamespace.ToDisplayString();
        var location = resolution.Declaration.GetLocation().GetLineSpan();

        return new Contracts.Models.SymbolInfo
        {
            Name = resolution.Symbol.Name,
            FullyQualifiedName = $"{newNamespace}.{resolution.Symbol.Name}",
            Kind = MapSymbolKind(resolution.Symbol),
            PreviousNamespace = oldNamespace,
            NewNamespace = newNamespace,
            PreviousLocation = new SymbolLocation
            {
                File = resolution.Document.FilePath!,
                Line = location.StartLinePosition.Line + 1,
                Column = location.StartLinePosition.Character + 1
            },
            NewLocation = movedTo == null
                ? null
                : new SymbolLocation
                {
                    File = movedTo,
                    Line = location.StartLinePosition.Line + 1,
                    Column = location.StartLinePosition.Character + 1
                }
        };
    }

    private static Contracts.Enums.SymbolKind MapSymbolKind(INamedTypeSymbol symbol)
    {
        return symbol.TypeKind switch
        {
            TypeKind.Class => Contracts.Enums.SymbolKind.Class,
            TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
            TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
            TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
            TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
            _ when symbol.IsRecord => Contracts.Enums.SymbolKind.Record,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }

    private RefactoringResult CreatePreviewResult(
        Guid operationId,
        MoveTypeToNamespaceParams @params,
        SymbolResolutionResult resolution,
        ChangeStats stats,
        NamespaceFolderMove? move)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = resolution.Document.FilePath!,
                ChangeType = ChangeKind.Modify,
                Description = $"Change namespace from {resolution.Symbol.ContainingNamespace.ToDisplayString()} to {@params.TargetNamespace}"
            }
        };

        if (stats.UsingsAdded > 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = "(multiple files)",
                ChangeType = ChangeKind.Modify,
                Description = $"Add using directive for {@params.TargetNamespace} in {stats.UsingsAdded} file(s)"
            });
        }

        if (move?.TargetPath != null)
        {
            pendingChanges.Add(new PendingChange
            {
                File = move.TargetPath,
                ChangeType = ChangeKind.Create,
                Description = $"Move {Path.GetFileName(resolution.Document.FilePath)} here (namespace folder)"
            });
            pendingChanges.Add(new PendingChange
            {
                File = resolution.Document.FilePath!,
                ChangeType = ChangeKind.Delete,
                Description = $"Moved to {move.TargetPath}"
            });
        }

        var preview = RefactoringResult.PreviewResult(operationId, pendingChanges);
        return move?.SkipReason == null
            ? preview
            : new RefactoringResult
            {
                Success = true,
                OperationId = preview.OperationId,
                Preview = true,
                PendingChanges = preview.PendingChanges,
                Error = FileNotMovedWarning(move.SkipReason)
            };
    }

    private sealed class ChangeStats
    {
        public int UsingsAdded { get; set; }
        public int UsingsRemoved { get; set; }
    }
}
