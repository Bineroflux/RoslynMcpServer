namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a search for unused symbols.
/// </summary>
public sealed class FindUnusedSymbolsResult
{
    /// <summary>
    /// The unused symbols, ordered by file, line and column. A member or nested type of a listed
    /// type is not listed separately.
    /// </summary>
    public required IReadOnlyList<UnusedSymbolEntry> Symbols { get; init; }

    /// <summary>
    /// Number of unused symbols found (may exceed the list when truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the list was cut off at maxResults.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Number of symbols whose references were searched.
    /// </summary>
    public int SymbolsChecked { get; init; }
}

/// <summary>
/// A type or member that nothing references outside its own declaration.
/// </summary>
public sealed class UnusedSymbolEntry
{
    /// <summary>
    /// Simple name (a constructor has its type's name).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Fully qualified name, with the parameter types of a method.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Symbol kind.
    /// </summary>
    public required Enums.SymbolKind Kind { get; init; }

    /// <summary>
    /// The containing type, fully qualified; null for a top-level type.
    /// </summary>
    public string? ContainingType { get; init; }

    /// <summary>
    /// The declared accessibility as written in C#: public, internal, protected, private,
    /// protected internal or private protected.
    /// </summary>
    public required string Accessibility { get; init; }

    /// <summary>
    /// File of the declaration (with a sourceFile scope, the declaration in that file).
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// 1-based line of the symbol's name.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column of the symbol's name.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Why it is listed: <see cref="Unreferenced"/> (nothing outside its declaration references
    /// it) or <see cref="WriteOnly"/> (a field or auto-property that is assigned but never read;
    /// deleting it means deleting the assignments too, so <c>safe_delete</c> refuses it until
    /// they are gone).
    /// </summary>
    public string Reason { get; init; } = Unreferenced;

    /// <summary><see cref="Reason"/> of a symbol nothing references outside its declaration.</summary>
    public const string Unreferenced = "unreferenced";

    /// <summary><see cref="Reason"/> of a field or auto-property whose references only write it.</summary>
    public const string WriteOnly = "write-only";
}
