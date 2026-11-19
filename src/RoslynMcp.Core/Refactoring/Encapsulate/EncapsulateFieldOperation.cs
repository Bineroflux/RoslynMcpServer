using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Encapsulate;

/// <summary>
/// Encapsulates a field by converting it to a property.
/// </summary>
/// <remarks>
/// <para>
/// The field becomes private, and a property with the field's accessibility (public for a
/// private field, never more visible than the field's type) is inserted after it. Code outside
/// the declaring type (other types, derived types, other projects) uses the property, object
/// initializers, patterns and <c>nameof</c> included; code inside it, in every part of a
/// partial type and in nested types, keeps using the field. Documentation <c>cref</c>s name the
/// property wherever they are. When the property would
/// have the field's name but for its case (<c>name</c> / <c>Name</c>), the field is renamed to
/// <c>_name</c>, everywhere it stays. A field declared with others (<c>int a, b;</c>) is split
/// off its declaration.
/// </para>
/// <para>
/// A use outside the type that needs the field as a variable (passed by <c>ref</c> / <c>out</c>,
/// its address taken, a member of a struct field assigned) keeps the field and is listed in the
/// result's <c>warnings</c>, as are calls of a struct field's methods, which now work on a copy,
/// and any compiler errors left. All edits of a document are made in one pass on its
/// original tree.
/// </para>
/// </remarks>
public sealed class EncapsulateFieldOperation : RefactoringOperationBase<EncapsulateFieldParams>
{
    /// <summary>
    /// Creates a new encapsulate field operation.
    /// </summary>
    public EncapsulateFieldOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(EncapsulateFieldParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.FieldName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "fieldName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.PropertyName != null)
            IdentifierNames.Validate(@params.PropertyName, NameKind.Member, "propertyName", ErrorCodes.InvalidSymbolName);
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        EncapsulateFieldParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find field declaration
        var fieldName = IdentifierNames.ValueText(@params.FieldName);
        var fieldDeclarator = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == fieldName && v.Parent?.Parent is FieldDeclarationSyntax);

        if (fieldDeclarator == null)
        {
            throw new RefactoringException(
                ErrorCodes.FieldNotFound,
                $"Field '{@params.FieldName}' not found.");
        }

        var fieldDeclaration = (FieldDeclarationSyntax)fieldDeclarator.Parent!.Parent!;
        if (semanticModel.GetDeclaredSymbol(fieldDeclarator, cancellationToken) is not IFieldSymbol fieldSymbol)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve field symbol.");
        }

        if (fieldSymbol.IsConst)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                "Cannot encapsulate const field.");
        }

        if (fieldSymbol.IsFixedSizeBuffer || fieldSymbol.RefKind != RefKind.None)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSelection,
                $"'{fieldName}' is a fixed-size buffer or a ref field, which a property cannot expose.");
        }

        if (fieldDeclaration.Parent is not TypeDeclarationSyntax containingType)
        {
            throw new RefactoringException(ErrorCodes.InvalidSelection, $"'{fieldName}' is not declared in a class, struct or record.");
        }

        var type = fieldSymbol.ContainingType;
        var propertyName = @params.PropertyName ?? DerivePropertyName(fieldName);
        var propertyValue = IdentifierNames.ValueText(propertyName);
        if (!SyntaxFacts.IsValidIdentifier(propertyValue))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolName,
                $"No property name can be derived from '{fieldName}'; pass propertyName.");
        }

        if (propertyValue == type.Name)
            throw new RefactoringException(ErrorCodes.NameCollision, $"A property cannot have the name of its type '{type.Name}' (CS0542).");

        if (type.GetMembers(propertyValue).FirstOrDefault(m => !SymbolEqualityComparer.Default.Equals(m, fieldSymbol)) is { } existing)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                existing is IPropertySymbol
                    ? $"Property '{propertyName}' already exists."
                    : $"'{type.Name}' already has a {existing.Kind.ToString().ToLowerInvariant()} named '{propertyName}'.");
        }

        // The field is renamed when only the case would tell it from the property (name / Name).
        var newFieldName = propertyValue.Equals(fieldName, StringComparison.OrdinalIgnoreCase)
            ? UniqueName(type, "_" + char.ToLowerInvariant(fieldName[0]) + fieldName[1..])
            : fieldName;

        var solution = Context.Solution;
        var lineEnding = await LineEndings.GetAsync(document, cancellationToken);
        var edits = new SyntaxEdits();
        var warnings = new List<string>();
        var updated = new Dictionary<DocumentId, int>();

        // The property, after the field (split off its declaration if it shares one).
        var property = CreateProperty(fieldSymbol, fieldDeclaration, propertyName, newFieldName, @params.ReadOnly, semanticModel);
        var fieldIndex = containingType.Members.IndexOf(fieldDeclaration);
        var declaratorIndex = fieldDeclaration.Declaration.Variables.IndexOf(fieldDeclarator);
        if (newFieldName != fieldName)
        {
            edits.Add(document.Id, fieldDeclarator, rewritten =>
            {
                var declarator = (VariableDeclaratorSyntax)rewritten;
                return declarator.WithIdentifier(IdentifierNames.Token(newFieldName).WithTriviaFrom(declarator.Identifier));
            });
        }

        edits.Add(document.Id, containingType, rewritten =>
        {
            var typeDeclaration = (TypeDeclarationSyntax)rewritten;
            var parts = SplitAndMakePrivate((FieldDeclarationSyntax)typeDeclaration.Members[fieldIndex], declaratorIndex, fieldSymbol, lineEnding);
            var members = typeDeclaration.Members.RemoveAt(fieldIndex)
                .InsertRange(fieldIndex, parts)
                .Insert(fieldIndex + parts.Count, GeneratedCodeFormatter.MarkInserted(property));
            return typeDeclaration.WithMembers(members);
        });

        // The references, each decided on the original trees.
        var references = (await SymbolFinder.FindReferencesAsync(fieldSymbol, solution, cancellationToken))
            .SelectMany(r => r.Locations)
            .Where(l => !l.IsImplicit)
            .OrderBy(l => l.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Location.SourceSpan.Start)
            .ToList();

        foreach (var reference in references)
        {
            var referenceRoot = await reference.Document.GetSyntaxRootAsync(cancellationToken);
            var model = await reference.Document.GetSemanticModelAsync(cancellationToken);
            if (referenceRoot == null || model == null ||
                referenceRoot.FindNode(reference.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true) is not SimpleNameSyntax name)
            {
                continue;
            }

            // A cref documents the member, which is now the property; other uses inside the
            // type keep the field.
            string newName;
            if (name.IsPartOfStructuredTrivia())
            {
                newName = propertyName;
            }
            else if (IsInside(name, type, model, cancellationToken))
            {
                newName = newFieldName;
            }
            else if (NeedsVariable(name, model, cancellationToken) is { } reason)
            {
                newName = newFieldName;
                warnings.Add($"{Where(reference)}: {reason}; a property cannot take the field's place there, and the field is now private.");
            }
            else
            {
                newName = propertyName;
                if (CallsOnACopy(name, fieldSymbol, model, cancellationToken) is { } method)
                    warnings.Add($"{Where(reference)}: '{method}' is now called on a copy of the struct the property returns, so what it changes is lost.");
            }

            if (IdentifierNames.ValueText(newName) == name.Identifier.ValueText)
                continue;

            if (GeneratedReferences.IsGenerated(reference, cancellationToken))
            {
                warnings.Add(GeneratedReferences.Warning(reference.Location, $"the use of '{fieldName}'", $"changed to '{IdentifierNames.ValueText(newName)}'"));
                continue;
            }

            edits.Add(reference.Document.Id, name, rewritten =>
                ((SimpleNameSyntax)rewritten).WithIdentifier(IdentifierNames.Token(newName).WithTriviaFrom(name.Identifier)));
            updated[reference.Document.Id] = updated.GetValueOrDefault(reference.Document.Id) + 1;
        }

        var newSolution = await edits.ApplyAsync(solution, cancellationToken);
        warnings.AddRange(await IntroducedErrors.FindAsync(solution, newSolution, edits.Documents, cancellationToken, matchLines: false) ?? []);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return await CreatePreviewResultAsync(
                operationId, @params, document, fieldDeclaration, propertyName, newSolution, updated, warnings, cancellationToken);
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
                Name = propertyValue,
                FullyQualifiedName = $"{type.ToDisplayString()}.{propertyValue}",
                Kind = Contracts.Enums.SymbolKind.Property
            },
            ReferencesUpdated = updated.Values.Sum(),
            Warnings = warnings.Count > 0 ? warnings : null
        };
    }

    /// <summary>
    /// The property: the field's accessibility (public when the field is private), at most as
    /// visible as the field's type; static and <c>required</c> like the field; read-only for a
    /// readonly field or when asked.
    /// </summary>
    private static PropertyDeclarationSyntax CreateProperty(
        IFieldSymbol field,
        FieldDeclarationSyntax declaration,
        string propertyName,
        string fieldName,
        bool readOnly,
        SemanticModel model)
    {
        var property = SyntaxGenerationHelper.CreatePropertyFromField(
            field,
            propertyName,
            readOnly,
            model.GetNullableContext(declaration.SpanStart).AnnotationsEnabled(),
            fieldName);

        var accessibility = field.DeclaredAccessibility is Accessibility.Private or Accessibility.NotApplicable
            ? Accessibility.Public
            : field.DeclaredAccessibility;
        accessibility = Minimum(accessibility, Visibility(field.Type));

        var modifiers = AccessibilityTokens(accessibility);
        if (field.IsStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        if (field.IsRequired)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.RequiredKeyword));

        return property.WithModifiers(SyntaxFactory.TokenList(modifiers)).NormalizeWhitespace();
    }

    /// <summary>
    /// The field's declaration as it becomes: private (and no longer <c>required</c>, which the
    /// property takes over), split into the declarators before it, itself and those after it
    /// when it shares the declaration, so the fields are still initialized in the same order.
    /// The parts keep the declaration's layout (no formatting, so initializers stay as written):
    /// the first takes its comments, the others its indentation, each on a line of its own.
    /// </summary>
    private static List<MemberDeclarationSyntax> SplitAndMakePrivate(
        FieldDeclarationSyntax declaration, int index, IFieldSymbol field, string lineEnding)
    {
        var variables = declaration.Declaration.Variables;
        var modifiers = MakePrivate(declaration.Modifiers, field);
        if (variables.Count == 1)
            return [declaration.WithModifiers(modifiers)];

        // The declarators from start to end, with the separators (and their spacing) between them.
        FieldDeclarationSyntax Part(int start, int end, SyntaxTokenList tokens) =>
            declaration
                .WithModifiers(tokens)
                .WithDeclaration(declaration.Declaration.WithVariables(SyntaxFactory.SeparatedList(
                    variables.Skip(start).Take(end - start).Select((d, i) => i == 0 ? d.WithoutLeadingTrivia() : d),
                    Enumerable.Range(start, end - start - 1).Select(variables.GetSeparator))));

        var parts = new List<FieldDeclarationSyntax>();
        if (index > 0)
            parts.Add(Part(0, index, declaration.Modifiers));
        parts.Add(Part(index, index + 1, modifiers));
        if (index < variables.Count - 1)
            parts.Add(Part(index + 1, variables.Count, declaration.Modifiers));

        var indentation = declaration.GetLeadingTrivia().LastOrDefault();
        var lineBreak = SyntaxFactory.EndOfLine(lineEnding);
        return parts.Select((part, i) => (MemberDeclarationSyntax)part
                .WithLeadingTrivia(i == 0
                    ? declaration.GetLeadingTrivia()
                    : indentation.IsKind(SyntaxKind.WhitespaceTrivia) ? [indentation] : [])
                .WithTrailingTrivia(i == parts.Count - 1 ? declaration.GetTrailingTrivia() : [lineBreak]))
            .ToList();
    }

    /// <summary>
    /// Replaces the accessibility modifiers with <c>private</c> and drops <c>required</c>. The
    /// new keyword takes over the trivia of the first modifier it replaces, so neither the
    /// indentation before it nor the space after it is lost (which used to produce
    /// <c>privatestring Name;</c>).
    /// </summary>
    private static SyntaxTokenList MakePrivate(SyntaxTokenList modifiers, IFieldSymbol field)
    {
        if (field.DeclaredAccessibility == Accessibility.Private && !field.IsRequired)
            return modifiers;

        var result = new List<SyntaxToken>();
        var replaced = false;
        var pendingTrivia = default(SyntaxTriviaList);

        foreach (var modifier in modifiers)
        {
            if (modifier.IsKind(SyntaxKind.RequiredKeyword))
            {
                if (result.Count == 0)
                    pendingTrivia = modifier.LeadingTrivia;
                continue;
            }

            if (!modifier.IsKind(SyntaxKind.PublicKeyword) &&
                !modifier.IsKind(SyntaxKind.ProtectedKeyword) &&
                !modifier.IsKind(SyntaxKind.InternalKeyword) &&
                !modifier.IsKind(SyntaxKind.PrivateKeyword))
            {
                result.Add(result.Count == 0 && pendingTrivia.Count > 0 ? modifier.WithLeadingTrivia(pendingTrivia) : modifier);
            }
            else if (!replaced)
            {
                result.Add(SyntaxFactory.Token(
                    result.Count == 0 && pendingTrivia.Count > 0 ? pendingTrivia : modifier.LeadingTrivia,
                    SyntaxKind.PrivateKeyword,
                    modifier.TrailingTrivia));
                replaced = true;
            }
        }

        if (!replaced)
            result.Insert(0, SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space));

        return SyntaxFactory.TokenList(result);
    }

    /// <summary>
    /// Whether the reference is in a declaration of the field's type (any part of a partial type,
    /// or a type nested in it), also in another target framework's document of its file.
    /// </summary>
    private static bool IsInside(SyntaxNode reference, INamedTypeSymbol type, SemanticModel model, CancellationToken cancellationToken) =>
        reference.Ancestors().OfType<TypeDeclarationSyntax>().Any(declaration =>
            model.GetDeclaredSymbol(declaration, cancellationToken) is { } declared && SymbolIdentity.SameDeclaration(declared, type));

    /// <summary>
    /// Why a reference needs the field itself rather than a value: passed by reference, its
    /// address taken, a ref to it, or a member of the (struct) field assigned. Null when a
    /// property can take its place.
    /// </summary>
    private static string? NeedsVariable(SimpleNameSyntax name, SemanticModel model, CancellationToken cancellationToken)
    {
        if (name.IsPartOfStructuredTrivia() || IsInNameOf(name))
            return null;

        var reference = model.GetOperation(name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name, cancellationToken);
        if (reference is not IFieldReferenceOperation)
            return null;

        // Walk up through members of a struct field: s.Point.X = 1 assigns into the field.
        var current = reference;
        while (true)
        {
            var parent = current.Parent;
            switch (parent)
            {
                case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument:
                    return $"'{argument.Syntax}' passes the field by reference";
                case IArgumentOperation { Syntax: ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.InKeyword } } argument:
                    return $"'{argument.Syntax}' passes the field by reference";
                case IAddressOfOperation addressOf:
                    return $"'{addressOf.Syntax}' takes the field's address";
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } declarator }:
                    return $"'{declarator.Syntax}' refers to the field by reference";
                case IReturnOperation when current.Syntax.Parent is RefExpressionSyntax:
                case ISimpleAssignmentOperation { IsRef: true }:
                    return $"'{current.Syntax.Parent}' refers to the field by reference";
                case IMemberReferenceOperation member when member.Instance == current && current.Type?.IsValueType == true:
                    if (IsWrite(member))
                        return $"'{member.Parent?.Syntax ?? member.Syntax}' assigns a member of the field, which on a property would change a copy";
                    current = member;
                    continue;
                default:
                    return null;
            }
        }
    }

    /// <summary>Whether the operation is assigned to, incremented, or passed by reference.</summary>
    private static bool IsWrite(IOperation operation) => operation.Parent switch
    {
        IAssignmentOperation assignment => assignment.Target == operation,
        IIncrementOrDecrementOperation => true,
        IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } => true,
        _ => false
    };

    /// <summary>The method a reference calls on the field's value when that is a mutable struct, which the property returns a copy of.</summary>
    private static string? CallsOnACopy(SimpleNameSyntax name, IFieldSymbol field, SemanticModel model, CancellationToken cancellationToken)
    {
        if (field.Type is not { IsValueType: true, IsReadOnly: false } || name.IsPartOfStructuredTrivia())
            return null;

        var reference = model.GetOperation(name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name, cancellationToken);
        return reference?.Parent is IInvocationOperation { TargetMethod: { IsReadOnly: false, IsStatic: false } method } invocation &&
               invocation.Instance == reference
            ? method.Name
            : null;
    }

    private static bool IsInNameOf(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    private static string Where(ReferenceLocation reference)
    {
        var span = reference.Location.GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }

    /// <summary><paramref name="name"/>, or with a number appended until no member of the type has it.</summary>
    private static string UniqueName(INamedTypeSymbol type, string name)
    {
        var candidate = name;
        for (var i = 1; type.GetMembers(candidate).Length > 0 || candidate == type.Name; i++)
            candidate = name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return candidate;
    }

    /// <summary>How visible a type is: the least visible of it, its containing types and type arguments.</summary>
    private static Accessibility Visibility(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => Visibility(array.ElementType),
        IPointerTypeSymbol pointer => Visibility(pointer.PointedAtType),
        ITypeParameterSymbol => Accessibility.Public,
        INamedTypeSymbol named => named.TypeArguments.Select(Visibility)
            .Append(named.DeclaredAccessibility is Accessibility.NotApplicable ? Accessibility.Public : named.DeclaredAccessibility)
            .Append(named.ContainingType is { } containing ? Visibility(containing) : Accessibility.Public)
            .Aggregate(Minimum),
        _ => Accessibility.Public
    };

    /// <summary>The less visible of two accessibilities; protected and internal meet at private protected.</summary>
    private static Accessibility Minimum(Accessibility left, Accessibility right)
    {
        static int Rank(Accessibility accessibility) => accessibility switch
        {
            Accessibility.Private => 0,
            Accessibility.ProtectedAndInternal => 1,
            Accessibility.Protected or Accessibility.Internal => 2,
            Accessibility.ProtectedOrInternal => 3,
            _ => 4
        };

        if (left is Accessibility.Protected && right is Accessibility.Internal ||
            left is Accessibility.Internal && right is Accessibility.Protected)
        {
            return Accessibility.ProtectedAndInternal;
        }

        return Rank(left) <= Rank(right) ? left : right;
    }

    private static List<SyntaxToken> AccessibilityTokens(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => [SyntaxFactory.Token(SyntaxKind.PrivateKeyword)],
        Accessibility.ProtectedAndInternal => [SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)],
        Accessibility.Protected => [SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)],
        Accessibility.Internal => [SyntaxFactory.Token(SyntaxKind.InternalKeyword)],
        Accessibility.ProtectedOrInternal => [SyntaxFactory.Token(SyntaxKind.ProtectedKeyword), SyntaxFactory.Token(SyntaxKind.InternalKeyword)],
        _ => [SyntaxFactory.Token(SyntaxKind.PublicKeyword)]
    };

    private static string DerivePropertyName(string fieldName)
    {
        // Remove leading underscore if present
        var name = fieldName.TrimStart('_');

        // Capitalize first letter
        if (name.Length > 0)
        {
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        return name;
    }

    private static async Task<RefactoringResult> CreatePreviewResultAsync(
        Guid operationId,
        EncapsulateFieldParams @params,
        Document document,
        FieldDeclarationSyntax fieldDeclaration,
        string propertyName,
        Solution newSolution,
        IReadOnlyDictionary<DocumentId, int> updated,
        IReadOnlyList<string> warnings,
        CancellationToken cancellationToken)
    {
        // The declaration's new text: from the field's position to the end of the property.
        var newRoot = await newSolution.GetDocument(document.Id)!.GetSyntaxRootAsync(cancellationToken);
        var propertyValue = IdentifierNames.ValueText(propertyName);
        var newProperty = newRoot!.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.ValueText == propertyValue && p.SpanStart >= fieldDeclaration.SpanStart);
        var after = newProperty == null
            ? ""
            : (await newSolution.GetDocument(document.Id)!.GetTextAsync(cancellationToken))
                .ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(fieldDeclaration.SpanStart, newProperty.Span.End));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Encapsulate field '{@params.FieldName}' as property '{propertyName}' ({updated.Values.Sum()} references to update)",
                BeforeSnippet = fieldDeclaration.ToString(),
                AfterSnippet = after
            }
        };

        pendingChanges.AddRange(updated
            .Where(entry => entry.Key != document.Id)
            .Select(entry => new PendingChange
            {
                File = newSolution.GetDocument(entry.Key)!.FilePath!,
                ChangeType = ChangeKind.Modify,
                Description = $"{entry.Value} reference(s) to '{@params.FieldName}' updated"
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
}
