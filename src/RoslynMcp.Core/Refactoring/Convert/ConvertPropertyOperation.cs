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
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts properties between auto-property and full-property forms.
/// </summary>
public sealed class ConvertPropertyOperation : RefactoringOperationBase<ConvertPropertyParams>
{
    /// <summary>Marks the backing field to remove once a property became an auto-property.</summary>
    private const string RemovedFieldAnnotationKind = "RoslynMcp.ConvertProperty.RemovedField";

    /// <inheritdoc />
    public ConvertPropertyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertPropertyParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.PropertyName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either propertyName or line must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!Enum.TryParse<ConversionDirection>(@params.Direction, ignoreCase: true, out var dir) ||
            (dir != ConversionDirection.ToAutoProperty && dir != ConversionDirection.ToFullProperty))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "direction must be 'ToAutoProperty' or 'ToFullProperty'.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertPropertyParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var direction = Enum.Parse<ConversionDirection>(@params.Direction, ignoreCase: true);

        // Find the property
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>();

        if (!string.IsNullOrWhiteSpace(@params.PropertyName))
            properties = properties.Where(p => p.Identifier.Text == @params.PropertyName);

        if (@params.Line.HasValue)
            properties = properties.Where(p =>
                p.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value);

        var property = properties.FirstOrDefault();
        if (property == null)
            throw new RefactoringException(ErrorCodes.SymbolNotFound,
                $"Property '{@params.PropertyName ?? $"at line {@params.Line}"}' not found.");

        if (direction != ConversionDirection.ToFullProperty)
            return await ConvertToAutoPropertyAsync(operationId, @params, document, property, semanticModel, cancellationToken);

        var (newRoot, beforeSnippet, afterSnippet) = ConvertToFullProperty(root, property);

        // The generated property and field are laid out like their surroundings, with the
        // document's indentation and line ending.
        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = await GeneratedCodeFormatter.FormatAsync(newDocument.Project.Solution, document, cancellationToken);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Convert property to {direction}",
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = property.Identifier.Text, FullyQualifiedName = property.Identifier.Text, Kind = Contracts.Enums.SymbolKind.Property },
            0, 0);
    }

    private static (SyntaxNode newRoot, string before, string after) ConvertToFullProperty(
        SyntaxNode root, PropertyDeclarationSyntax property)
    {
        // Must be an auto-property
        if (property.AccessorList == null)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have accessors.");

        var isAutoProperty = property.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);
        if (!isAutoProperty)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property is already a full property.");

        // Generate backing field name
        var fieldName = "_" + char.ToLower(property.Identifier.Text[0]) + property.Identifier.Text.Substring(1);

        // Create backing field; it takes over the property's initializer, as written.
        var declarator = SyntaxFactory.VariableDeclarator(fieldName);
        if (property.Initializer?.Value is { } initialValue)
        {
            declarator = declarator.WithInitializer(SyntaxFactory.EqualsValueClause(
                GeneratedCodeFormatter.KeepLayout(initialValue.WithoutTrivia(), initialValue.WithoutTrivia(), initialValue)));
        }

        var backingField = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(property.Type.WithoutTrivia())
                .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .NormalizeWhitespace();

        // Create full property with getter and setter
        var accessors = new List<AccessorDeclarationSyntax>();

        var hasGetter = property.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var hasSetter = property.AccessorList.Accessors.Any(a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        if (hasGetter)
        {
            var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(fieldName))));
            accessors.Add(getter);
        }

        if (hasSetter)
        {
            var originalSetter = property.AccessorList.Accessors.First(a =>
                a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

            var setterKind = originalSetter.IsKind(SyntaxKind.InitAccessorDeclaration)
                ? SyntaxKind.InitAccessorDeclaration
                : SyntaxKind.SetAccessorDeclaration;

            var setter = SyntaxFactory.AccessorDeclaration(setterKind)
                .WithModifiers(originalSetter.Modifiers)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(fieldName),
                            SyntaxFactory.IdentifierName("value")))));
            accessors.Add(setter);
        }

        // Only the accessors are generated (and laid out by the formatter); the property's
        // attributes, modifiers, type and name stay as written.
        var generated = SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)).NormalizeWhitespace();
        var newProperty = WithoutTrailingWhitespace(property, property.AccessorList.OpenBraceToken.GetPreviousToken())
            .WithAccessorList(GeneratedCodeFormatter.Mark(generated
                .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
                .WithTrailingTrivia(property.GetTrailingTrivia())))
            .WithInitializer(null)
            .WithSemicolonToken(default);

        var before = property.NormalizeWhitespace().ToFullString();
        var after = backingField.NormalizeWhitespace().ToFullString() + "\n" + newProperty.NormalizeWhitespace().ToFullString();

        // Insert backing field before the property
        var parent = property.Parent;
        if (parent is TypeDeclarationSyntax typeDecl)
        {
            var propertyIndex = typeDecl.Members.IndexOf(property);
            var newMembers = typeDecl.Members
                .Insert(propertyIndex, GeneratedCodeFormatter.MarkInserted(backingField));
            var newTypeDecl = typeDecl.WithMembers(newMembers);

            // Now replace the original property (which shifted by 1) with the new full property
            var shiftedProperty = newTypeDecl.Members[propertyIndex + 1] as PropertyDeclarationSyntax;
            if (shiftedProperty != null)
            {
                newTypeDecl = (TypeDeclarationSyntax)newTypeDecl.ReplaceNode(shiftedProperty, newProperty);
            }

            return (root.ReplaceNode(typeDecl, newTypeDecl), before, after);
        }

        // Fallback: just replace the property
        return (root.ReplaceNode(property, newProperty), before, after);
    }

    /// <summary>
    /// Converts a property whose accessors only read and write a field of its type into an
    /// auto-property, removes the field and points every other use of it at the property; the
    /// field's initializer becomes the property's.
    /// </summary>
    /// <remarks>
    /// Refused when the accessors do more than that (a computed getter, validation or
    /// notification in the setter), and when the field cannot be replaced by the property: it is
    /// volatile or has attributes, it is used as a variable (passed by <c>ref</c>, <c>out</c> or
    /// <c>in</c>, its address taken, a member of a struct field assigned, a struct method called on
    /// it), its initializer is read by another initializer, the property is virtual or an explicit
    /// implementation (other uses would go through overrides), or a write the field allowed would
    /// need a setter the property does not have or that is not accessible there. A field written
    /// outside the constructors of a get-only property makes the setter <c>private set</c>.
    /// </remarks>
    private async Task<RefactoringResult> ConvertToAutoPropertyAsync(
        Guid operationId,
        ConvertPropertyParams @params,
        Document document,
        PropertyDeclarationSyntax property,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var propertySymbol = model.GetDeclaredSymbol(property, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve the property.");
        var (field, setter) = FindBackingField(property, propertySymbol, model, cancellationToken);

        if (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not VariableDeclaratorSyntax
            {
                Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax fieldDeclaration }
            } declarator)
        {
            throw new RefactoringException(ErrorCodes.CannotConvert, $"The field '{field.Name}' is not declared in source.");
        }

        if (field.IsVolatile)
            throw new RefactoringException(ErrorCodes.CannotConvert, $"The field '{field.Name}' is volatile, which an auto-property's field cannot be.");
        if (fieldDeclaration.AttributeLists.Count > 0)
            throw new RefactoringException(ErrorCodes.CannotConvert, $"The field '{field.Name}' has attributes, which would have to move to the property as [field: ...]; move them first.");

        // Every other use of the field, decided on the original trees.
        var solution = Context.Solution;
        var edits = new SyntaxEdits();
        var warnings = new List<string>();
        var updated = 0;
        var writesOutsideConstructors = new List<(SyntaxNode Site, SemanticModel Model)>();
        var references = (await SymbolFinder.FindReferencesAsync(field, solution, cancellationToken))
            .SelectMany(r => r.Locations)
            .Where(l => !l.IsImplicit)
            .ToList();

        foreach (var reference in references)
        {
            var referenceRoot = await reference.Document.GetSyntaxRootAsync(cancellationToken);
            var referenceModel = await reference.Document.GetSemanticModelAsync(cancellationToken);
            if (referenceRoot?.FindNode(reference.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true) is not SimpleNameSyntax name ||
                referenceModel == null)
            {
                continue;
            }

            // The property's own accessors go away.
            if (reference.Document.Id == document.Id && property.Span.Contains(name.Span))
                continue;

            if (GeneratedReferences.IsGenerated(reference, cancellationToken))
            {
                warnings.Add(GeneratedReferences.Warning(reference.Location, $"the use of '{field.Name}'", $"changed to '{propertySymbol.Name}'"));
                continue;
            }

            var where = Where(reference);
            if (!name.IsPartOfStructuredTrivia() && !IsInNameOf(name))
            {
                CheckUse(name, field, propertySymbol, referenceModel, where, writesOutsideConstructors, cancellationToken);
            }
            else if (IsInNameOf(name))
            {
                warnings.Add($"{where}: nameof({field.Name}) now names the property: \"{propertySymbol.Name}\" instead of \"{field.Name}\".");
            }

            edits.Add(reference.Document.Id, name, rewritten =>
                ((SimpleNameSyntax)rewritten).WithIdentifier(IdentifierNames.Token(propertySymbol.Name).WithTriviaFrom(name.Identifier)));
            updated++;
        }

        if (updated > 0 && (propertySymbol.IsVirtual || propertySymbol.IsOverride || propertySymbol.IsAbstract || propertySymbol.ExplicitInterfaceImplementations.Length > 0))
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"'{propertySymbol.Name}' is virtual, an override or an explicit implementation, and '{field.Name}' is used elsewhere: " +
                "going through the property there would change what those uses do.");
        }

        if (declarator.Initializer != null && references.Any(r => IsInInitializer(r, cancellationToken)))
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"Another initializer reads '{field.Name}', which is initialized in its own declaration: as the property's initializer it would run in another order.");
        }

        // A write outside the constructors needs a setter that reaches it.
        var accessors = AutoAccessors(property, setter);
        if (writesOutsideConstructors.Count > 0)
        {
            var setMethod = setter == null ? null : propertySymbol.SetMethod;
            if (setter == null)
            {
                if (writesOutsideConstructors.FirstOrDefault(w => !IsInsideType(w.Site, propertySymbol.ContainingType, w.Model, cancellationToken)) is { Site: { } outside })
                {
                    throw new RefactoringException(
                        ErrorCodes.CannotConvert,
                        $"'{field.Name}' is written outside '{propertySymbol.ContainingType.Name}' ({Where(outside)}), but '{propertySymbol.Name}' has no setter.");
                }

                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }
            else if (setter.IsKind(SyntaxKind.InitAccessorDeclaration))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"'{propertySymbol.Name}' is init-only, but '{field.Name}' is written outside a constructor ({Where(writesOutsideConstructors[0].Site)}).");
            }
            else if (writesOutsideConstructors.FirstOrDefault(w => !w.Model.IsAccessible(w.Site.SpanStart, setMethod!)) is { Site: { } blocked })
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"'{field.Name}' is written at {Where(blocked)}, where the setter of '{propertySymbol.Name}' is not accessible.");
            }
        }

        // The property: the accessors as `{ get; set; }` on the line of its name, the field's
        // initializer after them; the rest of it stays as written.
        var newProperty = AutoProperty(property, accessors, declarator.Initializer);
        edits.Add(document.Id, property, _ => newProperty);
        var removed = new SyntaxAnnotation(RemovedFieldAnnotationKind);
        SyntaxNode fieldNode = fieldDeclaration.Declaration.Variables.Count > 1 ? declarator : fieldDeclaration;
        var fieldDocument = solution.GetDocument(fieldNode.SyntaxTree)!;
        edits.Add(fieldDocument.Id, fieldNode, rewritten => rewritten.WithAdditionalAnnotations(removed));

        var newSolution = await edits.ApplyAsync(solution, cancellationToken);
        var withField = newSolution.GetDocument(fieldDocument.Id)!;
        var text = await withField.GetTextAsync(cancellationToken);
        var fieldRoot = await withField.GetSyntaxRootAsync(cancellationToken);
        newSolution = newSolution.WithDocumentText(fieldDocument.Id, DeclarationRemoval.Remove(
            text, DeclarationRemoval.Spans(text, fieldRoot!.GetAnnotatedNodes(RemovedFieldAnnotationKind))));

        warnings.AddRange(await IntroducedErrors.FindInProjectsAsync(
            solution, newSolution, edits.Documents.Append(fieldDocument.Id).Distinct().ToList(), cancellationToken, matchLines: false) ?? []);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges =
                [
                    new PendingChange
                    {
                        File = @params.SourceFile,
                        ChangeType = ChangeKind.Modify,
                        Description = $"Convert '{propertySymbol.Name}' to an auto-property; remove '{field.Name}' and use the property in its {updated} other place(s)",
                        BeforeSnippet = fieldDeclaration.ToString() + "\n" + property,
                        AfterSnippet = newProperty.ToString().Trim()
                    }
                ],
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
            Symbol = new Contracts.Models.SymbolInfo { Name = propertySymbol.Name, FullyQualifiedName = propertySymbol.ToDisplayString(), Kind = Contracts.Enums.SymbolKind.Property },
            ReferencesUpdated = updated,
            Warnings = warnings.Count > 0 ? warnings : null
        };
    }

    /// <summary>
    /// The field the accessors only read and write, and the setter (<c>set</c> or <c>init</c>) if
    /// any; refuses accessors that do anything else.
    /// </summary>
    private static (IFieldSymbol Field, AccessorDeclarationSyntax? Setter) FindBackingField(
        PropertyDeclarationSyntax property,
        IPropertySymbol propertySymbol,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        ExpressionSyntax? returned;
        AccessorDeclarationSyntax? setter = null;
        if (property.ExpressionBody != null)
        {
            returned = property.ExpressionBody.Expression;
        }
        else if (property.AccessorList != null)
        {
            if (property.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null))
                throw new RefactoringException(ErrorCodes.CannotConvert, "Property is already an auto-property.");

            var getter = property.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))
                ?? throw new RefactoringException(ErrorCodes.CannotConvert, $"'{propertySymbol.Name}' has no getter; an auto-property needs one.");
            setter = property.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));
            returned = getter switch
            {
                { ExpressionBody.Expression: { } expression } => expression,
                { Body.Statements: [ReturnStatementSyntax { Expression: { } expression }] } => expression,
                _ => null
            };
        }
        else
        {
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have accessors.");
        }

        if (returned == null || FieldOf(returned, model, cancellationToken) is not { } field)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"The getter of '{propertySymbol.Name}' computes its value{(returned == null ? "" : $" ('{returned}')")} instead of returning a field of the type; only a property over a field can become an auto-property.");
        }

        if (field.IsConst || !SymbolEqualityComparer.Default.Equals(field.ContainingType.OriginalDefinition, propertySymbol.ContainingType.OriginalDefinition) ||
            field.IsStatic != propertySymbol.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                $"The getter of '{propertySymbol.Name}' returns '{field.Name}', which is not a {(propertySymbol.IsStatic ? "static" : "instance")} field of '{propertySymbol.ContainingType.Name}'.");
        }

        if (!SymbolEqualityComparer.Default.Equals(field.Type, propertySymbol.Type))
            throw new RefactoringException(ErrorCodes.CannotConvert, $"The field '{field.Name}' is a '{field.Type.ToDisplayString()}', the property a '{propertySymbol.Type.ToDisplayString()}'.");

        if (setter != null)
        {
            var assignment = setter switch
            {
                { ExpressionBody.Expression: AssignmentExpressionSyntax expression } => expression,
                { Body.Statements: [ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax expression }] } => expression,
                _ => null
            };

            if (assignment is not { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } ||
                !SymbolEqualityComparer.Default.Equals(FieldOf(assignment.Left, model, cancellationToken), field) ||
                model.GetSymbolInfo(assignment.Right, cancellationToken).Symbol is not IParameterSymbol { Name: "value", ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.PropertySet } })
            {
                throw new RefactoringException(
                    ErrorCodes.CannotConvert,
                    $"The {setter.Keyword.ValueText} accessor of '{propertySymbol.Name}' does more than store value in '{field.Name}' (validation or notification, for example); an auto-property's would not.");
            }
        }

        return (field, setter);
    }

    /// <summary>The field an expression names: <c>_x</c> or <c>this._x</c> (or <c>Type._x</c>).</summary>
    private static IFieldSymbol? FieldOf(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken) => expression switch
    {
        IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or IdentifierNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax } =>
            model.GetSymbolInfo(expression, cancellationToken).Symbol as IFieldSymbol,
        _ => null
    };

    /// <summary>
    /// Refuses a use of the field a property cannot take over, and records a write outside the
    /// constructors (which needs a setter).
    /// </summary>
    private static void CheckUse(
        SimpleNameSyntax name,
        IFieldSymbol field,
        IPropertySymbol property,
        SemanticModel model,
        string where,
        List<(SyntaxNode Site, SemanticModel Model)> writesOutsideConstructors,
        CancellationToken cancellationToken)
    {
        var expression = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? (SyntaxNode)access : name;
        if (model.GetOperation(expression, cancellationToken) is not IFieldReferenceOperation reference)
            return;

        if (!model.IsAccessible(name.SpanStart, property))
            throw new RefactoringException(ErrorCodes.CannotConvert, $"'{field.Name}' is used at {where}, where '{property.Name}' is not accessible.");

        var current = (IOperation)reference;
        while (true)
        {
            switch (current.Parent)
            {
                case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out or RefKind.In } argument
                    when argument.Parameter.RefKind != RefKind.In || argument.Syntax is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.InKeyword }:
                    throw NeedsVariable(field, where, $"'{argument.Syntax}' passes it by reference");
                case IAddressOfOperation addressOf:
                    throw NeedsVariable(field, where, $"'{addressOf.Syntax}' takes its address");
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } }:
                case ISimpleAssignmentOperation { IsRef: true }:
                case IReturnOperation when current.Syntax.Parent is RefExpressionSyntax:
                    throw NeedsVariable(field, where, "a ref refers to it");
                case IMemberReferenceOperation member when member.Instance == current && current.Type?.IsValueType == true:
                    if (IsWritten(member))
                        throw NeedsVariable(field, where, $"'{member.Parent?.Syntax}' assigns a member of the struct it holds, which on a property would change a copy");
                    current = member;
                    continue;
                case IInvocationOperation { TargetMethod: { IsReadOnly: false, IsStatic: false } method } invocation
                    when invocation.Instance == current && current.Type is { IsValueType: true, IsReadOnly: false }:
                    throw NeedsVariable(field, where, $"'{method.Name}' is called on the struct it holds, which on a property would change a copy");
            }

            break;
        }

        if (IsWritten(reference) && !IsConstructorWrite(name, reference, property, model, cancellationToken))
            writesOutsideConstructors.Add((name, model));
    }

    private static RefactoringException NeedsVariable(IFieldSymbol field, string where, string reason) =>
        new(ErrorCodes.CannotConvert, $"'{field.Name}' is used as a variable at {where}: {reason}. A property cannot take its place.");

    /// <summary>Whether the operation is assigned to (also in an object initializer or a deconstruction), or incremented.</summary>
    private static bool IsWritten(IOperation operation) => operation.Parent switch
    {
        IAssignmentOperation assignment => assignment.Target == operation,
        IIncrementOrDecrementOperation => true,
        ITupleOperation tuple => IsWritten(tuple),
        _ => false
    };

    /// <summary>
    /// A write a get-only auto-property allows: in a constructor of its type (not in a lambda or
    /// local function there), to the instance being constructed.
    /// </summary>
    private static bool IsConstructorWrite(SyntaxNode site, IFieldReferenceOperation reference, IPropertySymbol property, SemanticModel model, CancellationToken cancellationToken)
    {
        var function = site.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        return function is ConstructorDeclarationSyntax constructor &&
            model.GetDeclaredSymbol(constructor, cancellationToken) is { } symbol &&
            symbol.IsStatic == property.IsStatic &&
            SymbolIdentity.SameDeclaration(symbol.ContainingType, property.ContainingType) &&
            (property.IsStatic || reference.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance });
    }

    private static bool IsInsideType(SyntaxNode site, INamedTypeSymbol type, SemanticModel model, CancellationToken cancellationToken) =>
        site.Ancestors().OfType<TypeDeclarationSyntax>().Any(declaration =>
            model.GetDeclaredSymbol(declaration, cancellationToken) is { } declared && SymbolIdentity.SameDeclaration(declared, type));

    /// <summary>Whether a reference is in the initializer of a field or property.</summary>
    private static bool IsInInitializer(ReferenceLocation reference, CancellationToken cancellationToken) =>
        reference.Location.SourceTree?.GetRoot(cancellationToken).FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true)
            .Ancestors().Any(a => a is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } or PropertyDeclarationSyntax }) == true;

    private static bool IsInNameOf(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    private static string Where(ReferenceLocation reference)
    {
        var span = reference.Location.GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }

    private static string Where(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }

    /// <summary>The auto accessors for the property's accessors: <c>get;</c>, and <c>set;</c> / <c>init;</c> with their modifiers and attributes.</summary>
    private static List<AccessorDeclarationSyntax> AutoAccessors(PropertyDeclarationSyntax property, AccessorDeclarationSyntax? setter)
    {
        var accessors = new List<AccessorDeclarationSyntax>
        {
            property.AccessorList?.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) is { } getter
                ? SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, getter.AttributeLists, getter.Modifiers, getter.Keyword, null, null, SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                : SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        };

        if (setter != null)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(
                setter.Kind(), setter.AttributeLists, setter.Modifiers, setter.Keyword, null, null, SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        return accessors;
    }

    /// <summary>
    /// The property with <paramref name="accessors"/> as <c>{ get; set; }</c> on the line of its
    /// name and <paramref name="initializer"/> after them; the rest of it stays as written.
    /// </summary>
    private static PropertyDeclarationSyntax AutoProperty(
        PropertyDeclarationSyntax property,
        IReadOnlyList<AccessorDeclarationSyntax> accessors,
        EqualsValueClauseSyntax? initializer)
    {
        var space = SyntaxFactory.Space;
        var lastToken = property.GetLastToken();
        var autoList = SyntaxFactory.AccessorList(
            SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(space).WithTrailingTrivia(space),
            SyntaxFactory.List(accessors.Select(a => a
                .WithAttributeLists(SyntaxFactory.List(a.AttributeLists.Select(l => l.WithoutTrivia().WithTrailingTrivia(space))))
                .WithModifiers(SyntaxFactory.TokenList(a.Modifiers.Select(m => m.WithoutTrivia().WithTrailingTrivia(space))))
                .WithKeyword(a.Keyword.WithoutTrivia())
                .WithSemicolonToken(a.SemicolonToken.WithoutTrivia().WithTrailingTrivia(space)))),
            SyntaxFactory.Token(SyntaxKind.CloseBraceToken));

        var signatureEnd = property.AccessorList?.OpenBraceToken.GetPreviousToken() ?? property.ExpressionBody!.ArrowToken.GetPreviousToken();
        var result = WithoutTrailingWhitespace(property, signatureEnd)
            .WithExpressionBody(null)
            .WithAccessorList(autoList)
            .WithSemicolonToken(default);

        if (initializer == null)
            return result.WithAccessorList(autoList.WithCloseBraceToken(autoList.CloseBraceToken.WithTrailingTrivia(lastToken.TrailingTrivia)));

        return result
            .WithAccessorList(autoList.WithCloseBraceToken(autoList.CloseBraceToken.WithTrailingTrivia(space)))
            .WithInitializer(SyntaxFactory.EqualsValueClause(
                SyntaxFactory.Token(SyntaxKind.EqualsToken).WithTrailingTrivia(space),
                initializer.Value.WithoutTrivia()))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(lastToken.TrailingTrivia));
    }

    /// <summary>The property with the whitespace (and line break) after <paramref name="token"/> removed.</summary>
    private static PropertyDeclarationSyntax WithoutTrailingWhitespace(PropertyDeclarationSyntax property, SyntaxToken token) =>
        property.ReplaceToken(token, token.WithTrailingTrivia(
            token.TrailingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia))));
}
