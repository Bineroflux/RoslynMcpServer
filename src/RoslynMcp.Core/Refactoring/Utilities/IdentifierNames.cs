using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>What a new name declares, which decides the contextual keywords it may be.</summary>
internal enum NameKind
{
    /// <summary>A class, interface, struct, record, enum or delegate.</summary>
    Type,

    /// <summary>A method, property, field, constant or event.</summary>
    Member,

    /// <summary>A local variable.</summary>
    Local,

    /// <summary>A parameter.</summary>
    Parameter
}

/// <summary>
/// Checks the names a refactoring is asked to declare, and writes names as identifiers.
/// </summary>
/// <remarks>
/// A name is a C# identifier as the compiler defines it: Unicode letters, digits, connecting
/// and formatting characters, so <c>Größe</c> and <c>Λ</c> are names. A reserved keyword is a
/// name only when it is written verbatim: <c>@class</c> is accepted (Roslyn's renamer accepts it
/// too) and declared as <c>@class</c>, while <c>class</c> is refused with
/// <see cref="ErrorCodes.ReservedKeyword"/>. Any other name may be verbatim as well and is
/// written as given. Contextual keywords such as <c>var</c>, <c>async</c>, <c>value</c> or
/// <c>nameof</c> are names wherever they compile; a type cannot be named <c>record</c>,
/// <c>required</c>, <c>scoped</c>, <c>file</c> or <c>extension</c> unless it is verbatim
/// (CS8860, CS9029, CS9062, CS9056, CS9306), and <c>await</c> cannot name a local or a method
/// that is written inside an async function (CS4003). Namespace segments must be plain
/// identifiers: no keywords, verbatim or not.
/// <para>
/// rename_symbol writes the name the way Roslyn's renamer does: a reserved keyword verbatim,
/// any other name plain (<c>@value</c> becomes <c>value</c>). It therefore refuses the type
/// names above even when they are given verbatim.
/// </para>
/// </remarks>
internal static class IdentifierNames
{
    /// <summary>Contextual keywords the compiler refuses (or warns about) as the name of a type.</summary>
    private static readonly HashSet<SyntaxKind> ReservedTypeNames =
    [
        SyntaxKind.RecordKeyword,
        SyntaxKind.RequiredKeyword,
        SyntaxKind.ScopedKeyword,
        SyntaxKind.FileKeyword,
        SyntaxKind.ExtensionKeyword
    ];

    /// <summary>
    /// Refuses <paramref name="name"/> unless it can declare a <paramref name="kind"/>, and
    /// returns its value (the name without a verbatim <c>@</c>).
    /// </summary>
    /// <param name="name">The name as the request gives it, e.g. <c>Größe</c> or <c>@class</c>.</param>
    /// <param name="kind">What the name declares.</param>
    /// <param name="parameter">The request parameter it came from, for the error message.</param>
    /// <param name="invalidErrorCode">The error code for a name that is no identifier at all.</param>
    /// <exception cref="RefactoringException">The name is not valid there.</exception>
    public static string Validate(string name, NameKind kind, string parameter, string invalidErrorCode)
    {
        var verbatim = name.StartsWith('@');
        var value = ValueText(name);
        if (!SyntaxFacts.IsValidIdentifier(value))
            throw new RefactoringException(invalidErrorCode, $"{parameter} '{name}' is not a valid C# identifier.");

        if (verbatim)
            return value;

        if (SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None)
        {
            throw new RefactoringException(
                ErrorCodes.ReservedKeyword,
                $"{parameter} '{name}' is a C# keyword; write '@{name}' to use it as a name.");
        }

        if (kind == NameKind.Type && IsReservedTypeName(value))
        {
            throw new RefactoringException(
                ErrorCodes.ReservedKeyword,
                $"{parameter} '{name}' cannot name a type; write '@{name}' to use it as a name.");
        }

        return value;
    }

    /// <summary>
    /// Refuses a plain <c>await</c> written at <paramref name="site"/> when that is inside an
    /// async function (method, local function, lambda), where it is the await operator.
    /// </summary>
    /// <param name="name">The name as the request gives it.</param>
    /// <param name="site">Where the name is written.</param>
    /// <param name="parameter">The request parameter it came from, for the error message.</param>
    public static void CheckNotAwaitInAsync(string name, SyntaxNode site, string parameter)
    {
        if (name != "await")
            return;

        var function = site.AncestorsAndSelf().FirstOrDefault(n =>
            n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax);
        var isAsync = function switch
        {
            BaseMethodDeclarationSyntax method => method.Modifiers.Any(SyntaxKind.AsyncKeyword),
            LocalFunctionStatementSyntax local => local.Modifiers.Any(SyntaxKind.AsyncKeyword),
            AnonymousFunctionExpressionSyntax lambda => lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            _ => false
        };

        if (isAsync)
        {
            throw new RefactoringException(
                ErrorCodes.ReservedKeyword,
                $"{parameter} 'await' is a keyword inside an async function; write '@await' to use it as a name.");
        }
    }

    /// <summary>
    /// Refuses <paramref name="value"/> unless it is a namespace name: identifiers separated by
    /// dots, none of them a keyword.
    /// </summary>
    /// <param name="value">The namespace, e.g. <c>App.Größen</c>.</param>
    /// <param name="parameter">The request parameter it came from, for the error message.</param>
    public static void ValidateNamespace(string value, string parameter)
    {
        var segments = value.Split('.');
        if (!segments.All(SyntaxFacts.IsValidIdentifier))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidNamespace,
                $"{parameter} '{value}' is not a valid namespace name: C# identifiers separated by dots.");
        }

        if (segments.FirstOrDefault(s => SyntaxFacts.GetKeywordKind(s) != SyntaxKind.None) is { } keyword)
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"{parameter} '{value}' contains the keyword '{keyword}'.");
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a contextual keyword that names a type only when
    /// written verbatim (<c>record</c>, <c>required</c>, <c>scoped</c>, <c>file</c>, <c>extension</c>).
    /// </summary>
    public static bool IsReservedTypeName(string value) =>
        ReservedTypeNames.Contains(SyntaxFacts.GetContextualKeywordKind(value));

    /// <summary>The name without a verbatim <c>@</c>: what the compiler compares.</summary>
    public static string ValueText(string name) => name.StartsWith('@') ? name[1..] : name;

    /// <summary>
    /// The identifier token for a name: verbatim when the name is given verbatim or is a
    /// reserved keyword (a parameter named <c>event</c> is written <c>@event</c>), plain otherwise.
    /// </summary>
    public static SyntaxToken Token(string name)
    {
        var value = ValueText(name);
        return value != name || SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None
            ? SyntaxFactory.VerbatimIdentifier(default, value, value, default)
            : SyntaxFactory.Identifier(name);
    }

    /// <summary>A name expression for a name, written like <see cref="Token"/>.</summary>
    public static IdentifierNameSyntax Name(string name) => SyntaxFactory.IdentifierName(Token(name));
}
