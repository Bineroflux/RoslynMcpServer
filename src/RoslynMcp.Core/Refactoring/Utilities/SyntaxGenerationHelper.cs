using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
/// </remarks>
public static class SyntaxGenerationHelper
{
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
    /// <returns>Method declaration syntax.</returns>
    public static MethodDeclarationSyntax CreateMethodStub(
        IMethodSymbol method,
        bool explicitInterface = false,
        bool callBase = false,
        bool throwNotImplemented = true,
        INamedTypeSymbol? emittingType = null)
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
            body = CreateDefaultMethodBody(method);
        }

        var methodDecl = SyntaxFactory.MethodDeclaration(
                CreateReturnType(method.ReturnType, method.ReturnsByRef, method.ReturnsByRefReadonly),
                method.Name)
            .WithModifiers(CreateModifiers(method, explicitInterface, emittingType))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(method, explicitInterface))
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(method.Parameters.Select(CreateParameter))))
            .WithBody(body);

        // Add type parameters if generic
        if (method.TypeParameters.Length > 0)
        {
            var typeParams = method.TypeParameters.Select(tp =>
                SyntaxFactory.TypeParameter(tp.Name));
            methodDecl = methodDecl.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)));
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
    /// <returns>Property declaration syntax.</returns>
    public static PropertyDeclarationSyntax CreatePropertyStub(
        IPropertySymbol property,
        bool explicitInterface = false,
        bool throwNotImplemented = true,
        bool callBase = false,
        INamedTypeSymbol? emittingType = null)
    {
        return SyntaxFactory.PropertyDeclaration(
                CreateReturnType(property.Type, property.ReturnsByRef, property.ReturnsByRefReadonly),
                property.Name)
            .WithModifiers(CreateModifiers(property, explicitInterface, emittingType))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(property, explicitInterface))
            .WithAccessorList(CreateAccessorList(property, explicitInterface, throwNotImplemented, callBase, emittingType))
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
    /// <returns>Indexer declaration syntax.</returns>
    public static IndexerDeclarationSyntax CreateIndexerStub(
        IPropertySymbol indexer,
        bool explicitInterface = false,
        bool throwNotImplemented = true,
        bool callBase = false,
        INamedTypeSymbol? emittingType = null)
    {
        return SyntaxFactory.IndexerDeclaration(
                CreateReturnType(indexer.Type, indexer.ReturnsByRef, indexer.ReturnsByRefReadonly))
            .WithModifiers(CreateModifiers(indexer, explicitInterface, emittingType))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(indexer, explicitInterface))
            .WithParameterList(SyntaxFactory.BracketedParameterList(
                SyntaxFactory.SeparatedList(indexer.Parameters.Select(CreateParameter))))
            .WithAccessorList(CreateAccessorList(indexer, explicitInterface, throwNotImplemented, callBase, emittingType))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an event stub for interface implementation.
    /// </summary>
    /// <param name="eventSymbol">The event to implement.</param>
    /// <param name="explicitInterface">If true, creates explicit interface implementation.</param>
    /// <returns>Event declaration syntax.</returns>
    public static EventDeclarationSyntax CreateEventStub(
        IEventSymbol eventSymbol,
        bool explicitInterface = false)
    {
        var eventType = SyntaxFactory.ParseTypeName(eventSymbol.Type.ToDisplayString());

        var addAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.AddAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());
        var removeAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.RemoveAccessorDeclaration)
            .WithBody(SyntaxFactory.Block());

        return SyntaxFactory.EventDeclaration(eventType, eventSymbol.Name)
            .WithModifiers(CreateModifiers(eventSymbol, explicitInterface, emittingType: null))
            .WithExplicitInterfaceSpecifier(CreateExplicitInterfaceSpecifier(eventSymbol, explicitInterface))
            .WithAccessorList(SyntaxFactory.AccessorList(
                SyntaxFactory.List(new[] { addAccessor, removeAccessor })))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Converts a return type to its async equivalent.
    /// </summary>
    /// <param name="returnType">The original return type.</param>
    /// <returns>The async return type (Task or Task&lt;T&gt;).</returns>
    public static TypeSyntax ToAsyncReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return SyntaxFactory.ParseTypeName("Task");
        }

        return SyntaxFactory.GenericName("Task")
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                    SyntaxFactory.ParseTypeName(returnType.ToDisplayString()))));
    }

    /// <summary>
    /// Creates a field declaration from a property.
    /// </summary>
    /// <param name="property">The property to back with a field.</param>
    /// <returns>Field declaration syntax.</returns>
    public static FieldDeclarationSyntax CreateBackingField(IPropertySymbol property)
    {
        var fieldName = "_" + char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
        var fieldType = SyntaxFactory.ParseTypeName(property.Type.ToDisplayString());

        return SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(fieldType)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(fieldName))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates a property from a field (encapsulation).
    /// </summary>
    /// <param name="field">The field to encapsulate.</param>
    /// <param name="propertyName">Name for the property.</param>
    /// <param name="readOnly">If true, creates read-only property.</param>
    /// <returns>Property declaration syntax.</returns>
    public static PropertyDeclarationSyntax CreatePropertyFromField(
        IFieldSymbol field,
        string propertyName,
        bool readOnly = false)
    {
        var propertyType = SyntaxFactory.ParseTypeName(field.Type.ToDisplayString());

        var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(field.Name))));

        var accessors = new List<AccessorDeclarationSyntax> { getAccessor };

        if (!readOnly && !field.IsReadOnly)
        {
            var setAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(field.Name),
                            SyntaxFactory.IdentifierName("value")))));
            accessors.Add(setAccessor);
        }

        return SyntaxFactory.PropertyDeclaration(propertyType, propertyName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Creates an interface declaration with extracted members.
    /// </summary>
    /// <param name="interfaceName">Name of the interface.</param>
    /// <param name="members">Members to include in interface.</param>
    /// <returns>Interface declaration syntax.</returns>
    public static InterfaceDeclarationSyntax CreateInterfaceDeclaration(
        string interfaceName,
        IEnumerable<ISymbol> members)
    {
        var memberSyntax = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? syntax = member switch
            {
                IMethodSymbol method => CreateInterfaceMethod(method),
                IPropertySymbol prop => CreateInterfaceProperty(prop),
                IEventSymbol evt => CreateInterfaceEvent(evt),
                _ => null
            };

            if (syntax != null)
            {
                memberSyntax.Add(syntax);
            }
        }

        return SyntaxFactory.InterfaceDeclaration(interfaceName)
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

    private static MethodDeclarationSyntax CreateInterfaceMethod(IMethodSymbol method)
    {
        var methodDecl = SyntaxFactory.MethodDeclaration(
                CreateReturnType(method.ReturnType, method.ReturnsByRef, method.ReturnsByRefReadonly),
                method.Name)
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(method.Parameters.Select(CreateParameter))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        if (method.TypeParameters.Length > 0)
        {
            var typeParams = method.TypeParameters.Select(tp =>
                SyntaxFactory.TypeParameter(tp.Name));
            methodDecl = methodDecl.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams)));
        }

        return methodDecl;
    }

    /// <summary>
    /// Creates the interface counterpart of a property or indexer. Only public accessors are
    /// included: a <c>private set</c> would otherwise become an interface setter the class
    /// cannot implement. Init-only setters stay <c>init</c>.
    /// </summary>
    private static BasePropertyDeclarationSyntax? CreateInterfaceProperty(IPropertySymbol property)
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

        var type = CreateReturnType(property.Type, property.ReturnsByRef, property.ReturnsByRefReadonly);
        var accessorList = SyntaxFactory.AccessorList(SyntaxFactory.List(accessors));

        return property.IsIndexer
            ? SyntaxFactory.IndexerDeclaration(type)
                .WithParameterList(SyntaxFactory.BracketedParameterList(
                    SyntaxFactory.SeparatedList(property.Parameters.Select(CreateParameter))))
                .WithAccessorList(accessorList)
            : SyntaxFactory.PropertyDeclaration(type, property.Name)
                .WithAccessorList(accessorList);
    }

    private static EventDeclarationSyntax CreateInterfaceEvent(IEventSymbol eventSymbol)
    {
        // Interface events use field syntax
        return SyntaxFactory.EventDeclaration(
                SyntaxFactory.ParseTypeName(eventSymbol.Type.ToDisplayString()),
                eventSymbol.Name)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

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

    private static ExplicitInterfaceSpecifierSyntax? CreateExplicitInterfaceSpecifier(ISymbol member, bool explicitInterface) =>
        IsExplicit(member, explicitInterface)
            ? SyntaxFactory.ExplicitInterfaceSpecifier(SyntaxFactory.ParseName(member.ContainingType.ToDisplayString()))
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
        INamedTypeSymbol? emittingType)
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
                    : CreateDefaultReturnBody(property.Type);
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
                SyntaxFactory.IdentifierName(property.Name));

    private static TypeSyntax CreateReturnType(ITypeSymbol type, bool returnsByRef, bool returnsByRefReadonly)
    {
        var typeSyntax = SyntaxFactory.ParseTypeName(type.ToDisplayString());

        if (returnsByRefReadonly)
        {
            return SyntaxFactory.RefType(
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword),
                typeSyntax);
        }

        return returnsByRef ? SyntaxFactory.RefType(typeSyntax) : typeSyntax;
    }

    private static ParameterSyntax CreateParameter(IParameterSymbol parameter)
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

        return SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithType(SyntaxFactory.ParseTypeName(parameter.Type.ToDisplayString()));
    }

    /// <summary>
    /// Passes a parameter on to the base member with the matching argument modifier;
    /// <c>ref readonly</c> parameters are passed with <c>in</c>.
    /// </summary>
    private static ArgumentSyntax CreateArgument(IParameterSymbol parameter)
    {
        var argument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Name));

        return parameter.RefKind switch
        {
            RefKind.Ref => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            RefKind.Out => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)),
            RefKind.In or RefKind.RefReadOnlyParameter => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.InKeyword)),
            _ => argument
        };
    }

    private static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.IdentifierName("NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }

    private static BlockSyntax CreateBaseCallBody(IMethodSymbol method)
    {
        SimpleNameSyntax name = method.TypeParameters.Length > 0
            ? SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(method.Name),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList<TypeSyntax>(
                    method.TypeParameters.Select(tp => SyntaxFactory.IdentifierName(tp.Name)))))
            : SyntaxFactory.IdentifierName(method.Name);

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
    private static BlockSyntax CreateDefaultMethodBody(IMethodSymbol method)
    {
        var outAssignments = method.Parameters
            .Where(p => p.RefKind == RefKind.Out)
            .Select(p => (StatementSyntax)SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(p.Name),
                    SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))));

        var body = CreateDefaultReturnBody(method.ReturnType);
        return body.WithStatements(SyntaxFactory.List(outAssignments.Concat(body.Statements)));
    }

    private static BlockSyntax CreateDefaultReturnBody(ITypeSymbol returnType)
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
            defaultExpr = SyntaxFactory.DefaultExpression(
                SyntaxFactory.ParseTypeName(returnType.ToDisplayString()));
        }

        return SyntaxFactory.Block(SyntaxFactory.ReturnStatement(defaultExpr));
    }
}
