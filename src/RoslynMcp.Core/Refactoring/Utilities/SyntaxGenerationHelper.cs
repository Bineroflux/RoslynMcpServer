using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Helper methods for generating C# syntax nodes.
/// </summary>
/// <remarks>
/// The member stubs implement either an interface member or override a base class member.
/// Interface members are implemented as <c>public</c> members without <c>override</c>
/// (CS0115); overrides keep the overridden member's accessibility (CS0507). Parameters keep
/// their <c>ref</c>/<c>out</c>/<c>in</c>/<c>ref readonly</c> and <c>scoped</c> modifiers,
/// without which the stub would not implement or override anything.
/// <para>
/// Every <c>nullableAnnotations</c> parameter says whether nullable annotations are enabled
/// where the generated code goes. Only then are reference types written with <c>?</c>;
/// otherwise a member declared in annotated code (e.g. <c>object.Equals(object?)</c>) would
/// put annotations into a nullable-disabled file (CS8632).
/// </para>
/// </remarks>
public static class SyntaxGenerationHelper
{
    private static readonly SymbolDisplayFormat AnnotatedTypeFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    private static readonly SymbolDisplayFormat ObliviousTypeFormat =
        AnnotatedTypeFormat.RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// Creates a method stub for interface implementation or override.
    /// </summary>
    /// <param name="method">The method to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="callBase">If true, adds base.Method() call for overrides.</param>
    /// <param name="throwNotImplemented">If true, throws NotImplementedException.</param>
    /// <param name="emittingType">
    /// Type that will declare the stub. An override of a <c>protected internal</c> member from
    /// another assembly is emitted as <c>protected</c> (CS0507).
    /// </param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the stub goes.</param>
    /// <returns>Method declaration syntax.</returns>
    public static MethodDeclarationSyntax CreateMethodStub(
        IMethodSymbol method,
        bool explicitInterface = false,
        bool callBase = false,
        bool throwNotImplemented = true,
        INamedTypeSymbol? emittingType = null,
        bool nullableAnnotations = true)
    {
        BlockSyntax body;
        if (callBase && !method.IsAbstract)
        {
            body = CreateBaseCallBody(method);
        }
        else if (throwNotImplemented || method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            body = CreateThrowNotImplementedBody();
        }
        else
        {
            body = CreateDefaultMethodBody(method, nullableAnnotations);
        }

        var methodDecl = SyntaxFactory.MethodDeclaration(
                CreateReturnType(method.ReturnType, method.ReturnsByRef, method.ReturnsByRefReadonly, nullableAnnotations),
                IdentifierNames.Token(method.Name))
            .WithModifiers(CreateModifiers(method, explicitInterface, emittingType))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(method, explicitInterface, nullableAnnotations))
            .WithParameterList(CreateParameterList(method.Parameters, nullableAnnotations))
            .WithBody(body);

        // Add type parameters if generic. Overrides and explicit implementations inherit
        // their constraints; an implicit implementation has to restate them.
        if (method.TypeParameters.Length > 0)
        {
            var typeParams = method.TypeParameters.Select(tp =>
                SyntaxFactory.TypeParameter(IdentifierNames.Token(tp.Name)));
            var inheritsConstraints = IsExplicit(method, explicitInterface)
                || method.ContainingType?.TypeKind != TypeKind.Interface;
            methodDecl = methodDecl
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)))
                .WithConstraintClauses(CreateConstraintClauses(method, inheritsConstraints, nullableAnnotations));
        }

        return methodDecl.NormalizeWhitespace();
    }

    /// <summary>
    /// Creates a property stub for interface implementation or override.
    /// </summary>
    /// <param name="property">The property to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="throwNotImplemented">If true, throws NotImplementedException in accessors.</param>
    /// <param name="callBase">If true, the accessors of a non-abstract property delegate to <c>base.Property</c>.</param>
    /// <param name="emittingType">Type that will declare the stub (see <see cref="CreateMethodStub"/>).</param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the stub goes.</param>
    /// <returns>Property declaration syntax.</returns>
    public static PropertyDeclarationSyntax CreatePropertyStub(
        IPropertySymbol property,
        bool explicitInterface = false,
        bool throwNotImplemented = true,
        bool callBase = false,
        INamedTypeSymbol? emittingType = null,
        bool nullableAnnotations = true)
    {
        return SyntaxFactory.PropertyDeclaration(
                CreateReturnType(property.Type, property.ReturnsByRef, property.ReturnsByRefReadonly, nullableAnnotations),
                IdentifierNames.Token(property.Name))
            .WithModifiers(CreateModifiers(property, explicitInterface, emittingType))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(property, explicitInterface, nullableAnnotations))
            .WithAccessorList(CreateAccessorList(property, explicitInterface, throwNotImplemented, callBase, emittingType, nullableAnnotations))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an indexer stub for interface implementation or override.
    /// </summary>
    /// <param name="indexer">The indexer to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="throwNotImplemented">If true, throws NotImplementedException in accessors.</param>
    /// <param name="callBase">If true, the accessors of a non-abstract indexer delegate to <c>base[...]</c>.</param>
    /// <param name="emittingType">Type that will declare the stub (see <see cref="CreateMethodStub"/>).</param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the stub goes.</param>
    /// <returns>Indexer declaration syntax.</returns>
    public static IndexerDeclarationSyntax CreateIndexerStub(
        IPropertySymbol indexer,
        bool explicitInterface = false,
        bool throwNotImplemented = true,
        bool callBase = false,
        INamedTypeSymbol? emittingType = null,
        bool nullableAnnotations = true)
    {
        return SyntaxFactory.IndexerDeclaration(
                CreateReturnType(indexer.Type, indexer.ReturnsByRef, indexer.ReturnsByRefReadonly, nullableAnnotations))
            .WithModifiers(CreateModifiers(indexer, explicitInterface, emittingType))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(indexer, explicitInterface, nullableAnnotations))
            .WithParameterList(CreateBracketedParameterList(indexer.Parameters, nullableAnnotations))
            .WithAccessorList(CreateAccessorList(indexer, explicitInterface, throwNotImplemented, callBase, emittingType, nullableAnnotations))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an event stub for interface implementation.
    /// </summary>
    /// <param name="eventSymbol">The event to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the stub goes.</param>
    /// <returns>Event declaration syntax.</returns>
    public static EventDeclarationSyntax CreateEventStub(
        IEventSymbol eventSymbol,
        bool explicitInterface = false,
        bool nullableAnnotations = true)
    {
        var addAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.AddAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());
        var removeAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.RemoveAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());

        return SyntaxFactory.EventDeclaration(CreateTypeSyntax(eventSymbol.Type, nullableAnnotations), IdentifierNames.Token(eventSymbol.Name))
            .WithModifiers(CreateModifiers(eventSymbol, explicitInterface, emittingType: null))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(eventSymbol, explicitInterface, nullableAnnotations))
            .WithAccessorList(SyntaxFactory.AccessorList(
                SyntaxFactory.List(new[] { addAccessor, removeAccessor })))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// The async counterpart of a return type: <c>Task</c> for <c>void</c>, otherwise
    /// <c>Task&lt;T&gt;</c>, written like <see cref="CreateTypeSyntax"/> (fully qualified and
    /// marked for the simplifier, <c>?</c> only when <paramref name="nullableAnnotations"/> is set).
    /// </summary>
    /// <param name="returnType">The original return type.</param>
    /// <param name="compilation">The compilation whose <c>System.Threading.Tasks.Task</c> types are used.</param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the type goes.</param>
    /// <returns>The async return type.</returns>
    /// <exception cref="InvalidOperationException">The compilation has no <c>Task</c> types.</exception>
    public static TypeSyntax ToAsyncReturnType(ITypeSymbol returnType, Compilation compilation, bool nullableAnnotations) =>
        CreateTypeSyntax(ToAsyncReturnTypeSymbol(returnType, compilation), nullableAnnotations);

    /// <summary>The type <see cref="ToAsyncReturnType"/> writes.</summary>
    internal static INamedTypeSymbol ToAsyncReturnTypeSymbol(ITypeSymbol returnType, Compilation compilation)
    {
        var task = returnType.SpecialType == SpecialType.System_Void
            ? compilation.GetTypeByMetadataName("System.Threading.Tasks.Task")
            : compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1")?.Construct(returnType);

        return task ?? throw new InvalidOperationException("The compilation does not reference System.Threading.Tasks.Task.");
    }

    /// <summary>
    /// Creates a field declaration from a property.
    /// </summary>
    /// <param name="property">The property to back with a field.</param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the field goes.</param>
    /// <returns>Field declaration syntax.</returns>
    public static FieldDeclarationSyntax CreateBackingField(IPropertySymbol property, bool nullableAnnotations = true)
    {
        var fieldName = "_" + char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);

        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(CreateTypeSyntax(property.Type, nullableAnnotations))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(IdentifierNames.Token(fieldName)))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates a property from a field (encapsulation).
    /// </summary>
    /// <param name="field">The field to encapsulate.</param>
    /// <param name="propertyName">Name for the property.</param>
    /// <param name="readOnly">If true, creates read-only property.</param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the property goes.</param>
    /// <returns>Property declaration syntax.</returns>
    public static PropertyDeclarationSyntax CreatePropertyFromField(
        IFieldSymbol field,
        string propertyName,
        bool readOnly = false,
        bool nullableAnnotations = true)
    {
        var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(IdentifierNames.Name(field.Name))));

        var accessors = new List<AccessorDeclarationSyntax> { getAccessor };

        if (!readOnly && !field.IsReadOnly)
        {
            var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            IdentifierNames.Name(field.Name),
                            SyntaxFactory.IdentifierName("value")))));
            accessors.Add(setAccessor);
        }

        return SyntaxFactory.PropertyDeclaration(CreateTypeSyntax(field.Type, nullableAnnotations), IdentifierNames.Token(propertyName))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an interface declaration with extracted members.
    /// </summary>
    /// <param name="interfaceName">Name of the interface.</param>
    /// <param name="members">Members to include in interface.</param>
    /// <param name="nullableAnnotations">Whether nullable annotations are enabled where the interface goes.</param>
    /// <returns>Interface declaration syntax.</returns>
    public static InterfaceDeclarationSyntax CreateInterfaceDeclaration(
        string interfaceName,
        IEnumerable<ISymbol> members,
        bool nullableAnnotations = true)
    {
        var memberSyntax = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? syntax = member switch
            {
                IMethodSymbol method => CreateInterfaceMethod(method, nullableAnnotations),
                IPropertySymbol prop => CreateInterfaceProperty(prop, nullableAnnotations),
                IEventSymbol evt => CreateInterfaceEvent(evt, nullableAnnotations),
                _ => null
            };

            if (syntax != null)
            {
                memberSyntax.Add(syntax);
            }
        }

        return SyntaxFactory.InterfaceDeclaration(IdentifierNames.Token(interfaceName))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List(memberSyntax))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Whether an override emitted in <paramref name="emittingType"/> can override
    /// <paramref name="member"/>: private members never can, and internal members only
    /// when their assembly grants the emitting assembly access.
    /// </summary>
    internal static bool IsOverridableFrom(ISymbol member, INamedTypeSymbol? emittingType) =>
        member.DeclaredAccessibility switch
        {
            Accessibility.Private => false,
            Accessibility.Internal or Accessibility.ProtectedAndInternal =>
                emittingType == null || IsSameAssemblyOrFriend(member, emittingType),
            _ => true
        };

    /// <summary>
    /// Writes <paramref name="type"/> as a type name, with <c>?</c> on annotated reference
    /// types only when <paramref name="nullableAnnotations"/> is set. The name is fully
    /// qualified and marked for <see cref="Simplifier"/>, which shortens it to what the
    /// usings in scope allow (see <see cref="GeneratedCodeFormatter.FormatAsync"/>).
    /// </summary>
    internal static TypeSyntax CreateTypeSyntax(ITypeSymbol type, bool nullableAnnotations) =>
        type.SpecialType == SpecialType.System_Void
            // Parsed on its own, void carries CS1547 into the tree.
            ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
            : SyntaxFactory.ParseTypeName(type.ToDisplayString(nullableAnnotations ? AnnotatedTypeFormat : ObliviousTypeFormat))
                .WithAdditionalAnnotations(Simplifier.Annotation);

    private static MethodDeclarationSyntax CreateInterfaceMethod(IMethodSymbol method, bool nullableAnnotations)
    {
        var methodDecl = SyntaxFactory.MethodDeclaration(
                CreateReturnType(method.ReturnType, method.ReturnsByRef, method.ReturnsByRefReadonly, nullableAnnotations),
                IdentifierNames.Token(method.Name))
            .WithParameterList(CreateParameterList(method.Parameters, nullableAnnotations))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        if (method.TypeParameters.Length > 0)
        {
            var typeParams = method.TypeParameters.Select(tp =>
                SyntaxFactory.TypeParameter(IdentifierNames.Token(tp.Name)));
            methodDecl = methodDecl
                .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)))
                .WithConstraintClauses(CreateConstraintClauses(method, inheritsConstraints: false, nullableAnnotations));
        }

        return methodDecl;
    }

    /// <summary>
    /// Creates the interface counterpart of a property or indexer. Only public accessors are
    /// included: a <c>private set</c> would otherwise become an interface setter the class
    /// cannot implement. Init-only setters stay <c>init</c>.
    /// </summary>
    private static BasePropertyDeclarationSyntax? CreateInterfaceProperty(IPropertySymbol property, bool nullableAnnotations)
    {
        var accessors = new List<AccessorDeclarationSyntax>();

        if (property.GetMethod is { DeclaredAccessibility: Accessibility.Public })
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (property.SetMethod is { DeclaredAccessibility: Accessibility.Public } setter)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SetterKind(setter))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (accessors.Count == 0)
        {
            return null;
        }

        var type = CreateReturnType(property.Type, property.ReturnsByRef, property.ReturnsByRefReadonly, nullableAnnotations);
        var accessorList = SyntaxFactory.AccessorList(SyntaxFactory.List(accessors));

        return property.IsIndexer
            ? SyntaxFactory.IndexerDeclaration(type)
                .WithParameterList(CreateBracketedParameterList(property.Parameters, nullableAnnotations))
                .WithAccessorList(accessorList)
            : SyntaxFactory.PropertyDeclaration(type, IdentifierNames.Token(property.Name))
                .WithAccessorList(accessorList);
    }

    private static EventDeclarationSyntax CreateInterfaceEvent(IEventSymbol eventSymbol, bool nullableAnnotations)
    {
        // Interface events use field syntax
        return SyntaxFactory.EventDeclaration(CreateTypeSyntax(eventSymbol.Type, nullableAnnotations), IdentifierNames.Token(eventSymbol.Name))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>
    /// Constraint clauses for a generated generic method. A method that declares its own
    /// constraints (an interface method or an implicit implementation) must repeat all of
    /// them (CS0425). An override or explicit implementation inherits them and may only
    /// restate <c>class</c>/<c>struct</c> (CS0460), plus <c>default</c> for an unconstrained
    /// type parameter it writes as <c>T?</c>, which would otherwise mean <c>Nullable&lt;T&gt;</c>.
    /// </summary>
    private static SyntaxList<TypeParameterConstraintClauseSyntax> CreateConstraintClauses(
        IMethodSymbol method,
        bool inheritsConstraints,
        bool nullableAnnotations)
    {
        var clauses = new List<TypeParameterConstraintClauseSyntax>();

        foreach (var typeParameter in method.TypeParameters)
        {
            var constraints = inheritsConstraints
                ? InheritedConstraints(method, typeParameter, nullableAnnotations)
                : DeclaredConstraints(typeParameter, nullableAnnotations);

            if (constraints.Count > 0)
            {
                clauses.Add(SyntaxFactory.TypeParameterConstraintClause(
                    IdentifierNames.Name(typeParameter.Name),
                    SyntaxFactory.SeparatedList(constraints)));
            }
        }

        return SyntaxFactory.List(clauses);
    }

    private static List<TypeParameterConstraintSyntax> DeclaredConstraints(
        ITypeParameterSymbol typeParameter,
        bool nullableAnnotations)
    {
        var constraints = new List<TypeParameterConstraintSyntax>();

        if (typeParameter.HasReferenceTypeConstraint)
        {
            var classConstraint = SyntaxFactory.ClassOrStructConstraint(SyntaxKind.ClassConstraint);
            constraints.Add(nullableAnnotations && typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                ? classConstraint.WithQuestionToken(SyntaxFactory.Token(SyntaxKind.QuestionToken))
                : classConstraint);
        }
        else if (typeParameter.HasUnmanagedTypeConstraint)
        {
            constraints.Add(SyntaxFactory.TypeConstraint(SyntaxFactory.IdentifierName("unmanaged")));
        }
        else if (typeParameter.HasValueTypeConstraint)
        {
            constraints.Add(SyntaxFactory.ClassOrStructConstraint(SyntaxKind.StructConstraint));
        }
        else if (typeParameter.HasNotNullConstraint && nullableAnnotations)
        {
            constraints.Add(SyntaxFactory.TypeConstraint(SyntaxFactory.IdentifierName("notnull")));
        }

        constraints.AddRange(typeParameter.ConstraintTypes.Select(type =>
            SyntaxFactory.TypeConstraint(CreateTypeSyntax(type, nullableAnnotations))));

        if (typeParameter.HasConstructorConstraint)
        {
            constraints.Add(SyntaxFactory.ConstructorConstraint());
        }

        if (typeParameter.AllowsRefLikeType)
        {
            constraints.Add(SyntaxFactory.AllowsConstraintClause(
                SyntaxFactory.SingletonSeparatedList<AllowsConstraintSyntax>(SyntaxFactory.RefStructConstraint())));
        }

        return constraints;
    }

    private static List<TypeParameterConstraintSyntax> InheritedConstraints(
        IMethodSymbol method,
        ITypeParameterSymbol typeParameter,
        bool nullableAnnotations)
    {
        if (typeParameter.HasReferenceTypeConstraint)
        {
            return [SyntaxFactory.ClassOrStructConstraint(SyntaxKind.ClassConstraint)];
        }

        if (typeParameter.HasValueTypeConstraint)
        {
            return [SyntaxFactory.ClassOrStructConstraint(SyntaxKind.StructConstraint)];
        }

        var writesNullable = nullableAnnotations
            && method.Parameters.Select(p => p.Type).Prepend(method.ReturnType)
                .Any(type => MentionsAnnotated(type, typeParameter));

        return writesNullable ? [SyntaxFactory.DefaultConstraint()] : [];
    }

    private static bool MentionsAnnotated(ITypeSymbol type, ITypeParameterSymbol typeParameter) =>
        type switch
        {
            ITypeParameterSymbol candidate => candidate.NullableAnnotation == NullableAnnotation.Annotated
                && SymbolEqualityComparer.Default.Equals(candidate, typeParameter),
            IArrayTypeSymbol array => MentionsAnnotated(array.ElementType, typeParameter),
            INamedTypeSymbol named => named.TypeArguments.Any(argument => MentionsAnnotated(argument, typeParameter)),
            _ => false
        };

    private static SyntaxTokenList CreateModifiers(
        ISymbol member,
        bool explicitInterface,
        INamedTypeSymbol? emittingType)
    {
        var modifiers = new List<SyntaxToken>();
        var implementsInterface = member.ContainingType?.TypeKind == TypeKind.Interface;

        if (!IsExplicit(member, explicitInterface))
        {
            modifiers.AddRange(implementsInterface
                ? [SyntaxFactory.Token(SyntaxKind.PublicKeyword)]
                : AccessibilityModifiers(OverrideAccessibility(member, emittingType)));
        }

        // Static abstract/virtual interface members are implemented by static members.
        if (member.IsStatic)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        if (!implementsInterface)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
        }

        return SyntaxFactory.TokenList(modifiers);
    }

    private static ExplicitInterfaceSpecifierSyntax? CreateExplicitInterfaceSpecifier(
        ISymbol member,
        bool explicitInterface,
        bool nullableAnnotations) =>
        IsExplicit(member, explicitInterface)
            ? SyntaxFactory.ExplicitInterfaceSpecifier((NameSyntax)CreateTypeSyntax(member.ContainingType, nullableAnnotations))
            : null;

    private static bool IsExplicit(ISymbol member, bool explicitInterface) =>
        explicitInterface && member.ContainingType?.TypeKind == TypeKind.Interface;

    /// <summary>
    /// The accessibility an override must declare: the overridden member's, except that a
    /// <c>protected internal</c> member from another assembly is overridden as
    /// <c>protected</c> (CS0507).
    /// </summary>
    private static Accessibility OverrideAccessibility(ISymbol member, INamedTypeSymbol? emittingType) =>
        member.DeclaredAccessibility == Accessibility.ProtectedOrInternal
            && emittingType != null
            && !IsSameAssemblyOrFriend(member, emittingType)
                ? Accessibility.Protected
                : member.DeclaredAccessibility;

    private static bool IsSameAssemblyOrFriend(ISymbol member, INamedTypeSymbol emittingType) =>
        SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, emittingType.ContainingAssembly)
        || member.ContainingAssembly.GivesAccessTo(emittingType.ContainingAssembly);

    private static IEnumerable<SyntaxToken> AccessibilityModifiers(Accessibility accessibility) =>
        accessibility switch
        {
            Accessibility.Private => [SyntaxFactory.Token(SyntaxKind.PrivateKeyword)],
            Accessibility.Protected => [SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)],
            Accessibility.Internal => [SyntaxFactory.Token(SyntaxKind.InternalKeyword)],
            Accessibility.ProtectedOrInternal =>
                [SyntaxFactory.Token(SyntaxKind.ProtectedKeyword), SyntaxFactory.Token(SyntaxKind.InternalKeyword)],
            Accessibility.ProtectedAndInternal =>
                [SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)],
            _ => [SyntaxFactory.Token(SyntaxKind.PublicKeyword)]
        };

    private static AccessorListSyntax CreateAccessorList(
        IPropertySymbol property,
        bool explicitInterface,
        bool throwNotImplemented,
        bool callBase,
        INamedTypeSymbol? emittingType,
        bool nullableAnnotations)
    {
        var useBase = callBase && !property.IsAbstract;
        var returnsByRef = property.ReturnsByRef || property.ReturnsByRefReadonly;
        var accessors = new List<AccessorDeclarationSyntax>();

        if (property.GetMethod is { } getter && IsOverridableFrom(getter, emittingType))
        {
            BlockSyntax body;
            if (useBase)
            {
                var baseAccess = CreateBasePropertyAccess(property);
                body = SyntaxFactory.Block(SyntaxFactory.ReturnStatement(
                    returnsByRef ? SyntaxFactory.RefExpression(baseAccess) : baseAccess));
            }
            else
            {
                body = throwNotImplemented || returnsByRef
                    ? CreateThrowNotImplementedBody()
                    : CreateDefaultReturnBody(property.Type, nullableAnnotations);
            }

            accessors.Add(CreateAccessor(SyntaxKind.GetAccessorDeclaration, getter, property, body, explicitInterface, emittingType));
        }

        if (property.SetMethod is { } setter && IsOverridableFrom(setter, emittingType))
        {
            BlockSyntax body;
            if (useBase)
            {
                body = SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        CreateBasePropertyAccess(property),
                        SyntaxFactory.IdentifierName("value"))));
            }
            else
            {
                body = throwNotImplemented ? CreateThrowNotImplementedBody() : SyntaxFactory.Block();
            }

            accessors.Add(CreateAccessor(SetterKind(setter), setter, property, body, explicitInterface, emittingType));
        }

        return SyntaxFactory.AccessorList(SyntaxFactory.List(accessors));
    }

    /// <summary>
    /// An accessor whose accessibility differs from its property's (e.g. <c>protected set</c>)
    /// must restate it on the override.
    /// </summary>
    private static AccessorDeclarationSyntax CreateAccessor(
        SyntaxKind kind,
        IMethodSymbol accessor,
        IPropertySymbol property,
        BlockSyntax body,
        bool explicitInterface,
        INamedTypeSymbol? emittingType)
    {
        var declaration = SyntaxFactory.AccessorDeclaration(kind).WithBody(body);
        var accessibility = OverrideAccessibility(accessor, emittingType);

        return IsExplicit(property, explicitInterface) || accessibility == OverrideAccessibility(property, emittingType)
            ? declaration
            : declaration.WithModifiers(SyntaxFactory.TokenList(AccessibilityModifiers(accessibility)));
    }

    private static SyntaxKind SetterKind(IMethodSymbol setter) =>
        setter.IsInitOnly ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration;

    private static ExpressionSyntax CreateBasePropertyAccess(IPropertySymbol property) =>
        property.IsIndexer
            ? SyntaxFactory.ElementAccessExpression(
                SyntaxFactory.BaseExpression(),
                SyntaxFactory.BracketedArgumentList(
                    SyntaxFactory.SeparatedList(property.Parameters.Select(CreateArgument))))
            : SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.BaseExpression(),
                IdentifierNames.Name(property.Name));

    private static TypeSyntax CreateReturnType(
        ITypeSymbol type,
        bool returnsByRef,
        bool returnsByRefReadonly,
        bool nullableAnnotations)
    {
        var typeSyntax = CreateTypeSyntax(type, nullableAnnotations);

        if (returnsByRefReadonly)
        {
            return SyntaxFactory.RefType(
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword),
                typeSyntax);
        }

        return returnsByRef ? SyntaxFactory.RefType(typeSyntax) : typeSyntax;
    }

    private static ParameterListSyntax CreateParameterList(
        IEnumerable<IParameterSymbol> parameters,
        bool nullableAnnotations) =>
        SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            parameters.Select(p => CreateParameter(p, nullableAnnotations))));

    private static BracketedParameterListSyntax CreateBracketedParameterList(
        IEnumerable<IParameterSymbol> parameters,
        bool nullableAnnotations) =>
        SyntaxFactory.BracketedParameterList(SyntaxFactory.SeparatedList(
            parameters.Select(p => CreateParameter(p, nullableAnnotations))));

    private static ParameterSyntax CreateParameter(IParameterSymbol parameter, bool nullableAnnotations)
    {
        var modifiers = new List<SyntaxToken>();

        // Dropping `scoped` changes the parameter's escape scope, so the stub would no
        // longer match the member it implements (CS8987). `out` is implicitly scoped.
        var scoped = parameter.ScopedKind switch
        {
            ScopedKind.ScopedValue => parameter.RefKind == RefKind.None,
            ScopedKind.ScopedRef => parameter.RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter,
            _ => false
        };
        if (scoped)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.ScopedKeyword));
        }

        switch (parameter.RefKind)
        {
            case RefKind.Ref:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.RefKeyword));
                break;
            case RefKind.Out:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.OutKeyword));
                break;
            case RefKind.In:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.InKeyword));
                break;
            case RefKind.RefReadOnlyParameter:
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.RefKeyword));
                modifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
                break;
        }

        return SyntaxFactory.Parameter(IdentifierNames.Token(parameter.Name))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithType(CreateTypeSyntax(parameter.Type, nullableAnnotations));
    }

    /// <summary>
    /// Passes a parameter on to the base member with the matching argument modifier;
    /// <c>ref readonly</c> parameters are passed with <c>in</c>.
    /// </summary>
    private static ArgumentSyntax CreateArgument(IParameterSymbol parameter)
    {
        var argument = SyntaxFactory.Argument(IdentifierNames.Name(parameter.Name));

        return parameter.RefKind switch
        {
            RefKind.Ref => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            RefKind.Out => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)),
            RefKind.In or RefKind.RefReadOnlyParameter => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.InKeyword)),
            _ => argument
        };
    }

    /// <summary>
    /// <c>throw new global::System.NotImplementedException();</c>, the type marked for the simplifier,
    /// so it is written <c>NotImplementedException</c> only where <c>using System;</c> (or an
    /// implicit global using) allows it.
    /// </summary>
    private static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("global::System.NotImplementedException").WithAdditionalAnnotations(Simplifier.Annotation))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }

    private static BlockSyntax CreateBaseCallBody(IMethodSymbol method)
    {
        SimpleNameSyntax name = method.TypeParameters.Length > 0
            ? SyntaxFactory.GenericName(
                IdentifierNames.Token(method.Name),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList<TypeSyntax>(
                    method.TypeParameters.Select(tp => IdentifierNames.Name(tp.Name)))))
            : IdentifierNames.Name(method.Name);

        var baseCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.BaseExpression(),
                name))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(method.Parameters.Select(CreateArgument))));

        if (method.ReturnsVoid)
        {
            return SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(baseCall));
        }

        return SyntaxFactory.Block(SyntaxFactory.ReturnStatement(
            method.ReturnsByRef || method.ReturnsByRefReadonly ? SyntaxFactory.RefExpression(baseCall) : baseCall));
    }

    /// <summary>
    /// Default-return body that also assigns every <c>out</c> parameter (CS0177).
    /// </summary>
    private static BlockSyntax CreateDefaultMethodBody(IMethodSymbol method, bool nullableAnnotations)
    {
        var outAssignments = method.Parameters
            .Where(p => p.RefKind == RefKind.Out)
            .Select(p => (StatementSyntax)SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierNames.Name(p.Name),
                    SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))));

        var body = CreateDefaultReturnBody(method.ReturnType, nullableAnnotations);
        return body.WithStatements(SyntaxFactory.List(outAssignments.Concat(body.Statements)));
    }

    private static BlockSyntax CreateDefaultReturnBody(ITypeSymbol returnType, bool nullableAnnotations)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return SyntaxFactory.Block();
        }

        ExpressionSyntax defaultExpr;
        if (returnType.IsReferenceType)
        {
            defaultExpr = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        }
        else
        {
            defaultExpr = SyntaxFactory.DefaultExpression(CreateTypeSyntax(returnType, nullableAnnotations));
        }

        return SyntaxFactory.Block(SyntaxFactory.ReturnStatement(defaultExpr));
    }
}
