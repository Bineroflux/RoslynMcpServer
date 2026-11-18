using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Gets a structured outline of all declarations in a document.
/// Pure syntax traversal — no semantic model needed.
/// </summary>
public sealed class GetDocumentOutlineOperation : QueryOperationBase<GetDocumentOutlineParams, GetDocumentOutlineResult>
{
    /// <inheritdoc />
    public GetDocumentOutlineOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GetDocumentOutlineParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.MaxDepth.HasValue && @params.MaxDepth.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxDepth must be >= 1.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        SymbolKindFilter.Parse(@params.KindFilter);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<GetDocumentOutlineResult>> ExecuteCoreAsync(
        Guid operationId,
        GetDocumentOutlineParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        if (root == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var entries = new List<OutlineEntry>();
        var totalCount = 0;

        // Process top-level namespace declarations
        foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var nsEntry = BuildNamespaceEntry(ns, ref totalCount);
            entries.Add(nsEntry);
        }

        // Process top-level type declarations (not inside namespaces — for top-level types)
        foreach (var type in root.ChildNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeEntry = BuildTypeEntry(type, ref totalCount);
            entries.Add(typeEntry);
        }

        // Process top-level enum declarations
        foreach (var enumDecl in root.ChildNodes().OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            entries.Add(BuildEnumEntry(enumDecl, ref totalCount));
        }

        // Process top-level delegate declarations
        foreach (var del in root.ChildNodes().OfType<DelegateDeclarationSyntax>())
        {
            totalCount++;
            entries.Add(CreateEntry(del.Identifier.Text, "Delegate",
                del.GetLocation(), GetAccessibility(del.Modifiers), del.ReturnType.ToString()));
        }

        var truncated = false;
        if (@params.MaxDepth.HasValue || @params.MaxResults.HasValue ||
            !string.IsNullOrWhiteSpace(@params.KindFilter) || !string.IsNullOrEmpty(@params.NameFilter))
        {
            var filtered = new OutlineFilter(@params).Apply(entries, depth: 0);
            totalCount = OutlineFilter.Count(filtered);

            var budget = @params.MaxResults ?? int.MaxValue;
            entries = OutlineFilter.TakePreorder(filtered, ref budget);
            truncated = totalCount > OutlineFilter.Count(entries);
        }

        var result = new GetDocumentOutlineResult
        {
            File = @params.SourceFile,
            Entries = entries,
            TotalCount = totalCount,
            Truncated = truncated
        };

        return QueryResult<GetDocumentOutlineResult>.Succeeded(operationId, result);
    }

    private static OutlineEntry BuildNamespaceEntry(BaseNamespaceDeclarationSyntax ns, ref int totalCount)
    {
        totalCount++;
        var children = new List<OutlineEntry>();

        foreach (var type in ns.Members.OfType<TypeDeclarationSyntax>())
        {
            children.Add(BuildTypeEntry(type, ref totalCount));
        }

        foreach (var enumDecl in ns.Members.OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            children.Add(BuildEnumEntry(enumDecl, ref totalCount));
        }

        foreach (var del in ns.Members.OfType<DelegateDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(del.Identifier.Text, "Delegate",
                del.GetLocation(), GetAccessibility(del.Modifiers), del.ReturnType.ToString()));
        }

        var lineSpan = ns.GetLocation().GetLineSpan();
        return new OutlineEntry
        {
            Name = ns.Name.ToString(),
            Kind = "Namespace",
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Children = children.Count > 0 ? children : null
        };
    }

    private static OutlineEntry BuildTypeEntry(TypeDeclarationSyntax type, ref int totalCount)
    {
        totalCount++;
        var children = new List<OutlineEntry>();
        var kind = type switch
        {
            ClassDeclarationSyntax => "Class",
            InterfaceDeclarationSyntax => "Interface",
            StructDeclarationSyntax => "Struct",
            RecordDeclarationSyntax => "Record",
            _ => "Type"
        };

        // Nested types
        foreach (var nested in type.Members.OfType<TypeDeclarationSyntax>())
        {
            children.Add(BuildTypeEntry(nested, ref totalCount));
        }

        // Constructors
        foreach (var ctor in type.Members.OfType<ConstructorDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(ctor.Identifier.Text, "Constructor",
                ctor.GetLocation(), GetAccessibility(ctor.Modifiers)));
        }

        // Methods
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(method.Identifier.Text, "Method",
                method.GetLocation(), GetAccessibility(method.Modifiers), method.ReturnType.ToString()));
        }

        // Properties
        foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(prop.Identifier.Text, "Property",
                prop.GetLocation(), GetAccessibility(prop.Modifiers), prop.Type.ToString()));
        }

        // Fields
        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                totalCount++;
                var fieldKind = field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "Constant" : "Field";
                children.Add(CreateEntry(variable.Identifier.Text, fieldKind,
                    variable.GetLocation(), GetAccessibility(field.Modifiers), field.Declaration.Type.ToString()));
            }
        }

        // Events
        foreach (var evt in type.Members.OfType<EventDeclarationSyntax>())
        {
            totalCount++;
            children.Add(CreateEntry(evt.Identifier.Text, "Event",
                evt.GetLocation(), GetAccessibility(evt.Modifiers), evt.Type.ToString()));
        }

        foreach (var evtField in type.Members.OfType<EventFieldDeclarationSyntax>())
        {
            foreach (var variable in evtField.Declaration.Variables)
            {
                totalCount++;
                children.Add(CreateEntry(variable.Identifier.Text, "Event",
                    variable.GetLocation(), GetAccessibility(evtField.Modifiers), evtField.Declaration.Type.ToString()));
            }
        }

        // Enums nested in type
        foreach (var enumDecl in type.Members.OfType<EnumDeclarationSyntax>())
        {
            totalCount++;
            children.Add(BuildEnumEntry(enumDecl, ref totalCount));
        }

        var lineSpan = type.GetLocation().GetLineSpan();
        return new OutlineEntry
        {
            Name = type.Identifier.Text,
            Kind = kind,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = GetAccessibility(type.Modifiers),
            Children = children.Count > 0 ? children : null
        };
    }

    private static OutlineEntry BuildEnumEntry(EnumDeclarationSyntax enumDecl, ref int totalCount)
    {
        var children = new List<OutlineEntry>();
        foreach (var member in enumDecl.Members)
        {
            totalCount++;
            children.Add(CreateEntry(member.Identifier.Text, "EnumMember", member.GetLocation()));
        }

        var lineSpan = enumDecl.GetLocation().GetLineSpan();
        return new OutlineEntry
        {
            Name = enumDecl.Identifier.Text,
            Kind = "Enum",
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = GetAccessibility(enumDecl.Modifiers),
            Children = children.Count > 0 ? children : null
        };
    }

    private static OutlineEntry CreateEntry(string name, string kind, Location location,
        string? accessibility = null, string? returnType = null)
    {
        var lineSpan = location.GetLineSpan();
        return new OutlineEntry
        {
            Name = name,
            Kind = kind,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            Accessibility = accessibility,
            ReturnType = returnType
        };
    }

    /// <summary>
    /// The maxDepth, kindFilter and nameFilter options, applied to the built outline.
    /// </summary>
    private sealed class OutlineFilter
    {
        private readonly int _maxDepth;
        private readonly Contracts.Enums.SymbolKind? _kind;
        private readonly string? _name;

        public OutlineFilter(GetDocumentOutlineParams @params)
        {
            _maxDepth = @params.MaxDepth ?? int.MaxValue;
            _kind = SymbolKindFilter.Parse(@params.KindFilter);
            _name = string.IsNullOrEmpty(@params.NameFilter) ? null : @params.NameFilter;
        }

        /// <summary>
        /// The entries within maxDepth that match the kind and name filters, each with the
        /// containing entries that lead to it. Without those filters, everything within
        /// maxDepth. <paramref name="depth"/> is the nesting depth of the entries' parent;
        /// namespaces don't add to it.
        /// </summary>
        public List<OutlineEntry> Apply(IReadOnlyList<OutlineEntry> entries, int depth)
        {
            var filtering = _kind.HasValue || _name is not null;
            var kept = new List<OutlineEntry>();
            foreach (var entry in entries)
            {
                var entryDepth = entry.Kind == "Namespace" ? depth : depth + 1;
                if (entryDepth > _maxDepth)
                    continue;

                var children = entry.Children is null ? [] : Apply(entry.Children, entryDepth);
                if (!filtering || Matches(entry) || children.Count > 0)
                    kept.Add(WithChildren(entry, children));
            }

            return kept;
        }

        /// <summary>Counts the entries of a tree.</summary>
        public static int Count(IReadOnlyList<OutlineEntry> entries) =>
            entries.Sum(e => 1 + (e.Children is null ? 0 : Count(e.Children)));

        /// <summary>
        /// The first <paramref name="budget"/> entries in depth-first preorder, so every
        /// entry returned comes with its parents.
        /// </summary>
        public static List<OutlineEntry> TakePreorder(IReadOnlyList<OutlineEntry> entries, ref int budget)
        {
            var taken = new List<OutlineEntry>();
            foreach (var entry in entries)
            {
                if (budget == 0)
                    break;

                budget--;
                var children = entry.Children is null ? [] : TakePreorder(entry.Children, ref budget);
                taken.Add(WithChildren(entry, children));
            }

            return taken;
        }

        private bool Matches(OutlineEntry entry) =>
            (!_kind.HasValue || KindOf(entry.Kind) == _kind.Value) &&
            (_name is null || entry.Name.Contains(_name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// An outline kind as the symbol kind kindFilter names, the way SymbolKindMapper maps
        /// symbols: a constructor is a Method, an enum member a Constant.
        /// </summary>
        private static Contracts.Enums.SymbolKind? KindOf(string outlineKind) => outlineKind switch
        {
            "Namespace" => Contracts.Enums.SymbolKind.Namespace,
            "Class" => Contracts.Enums.SymbolKind.Class,
            "Interface" => Contracts.Enums.SymbolKind.Interface,
            "Struct" => Contracts.Enums.SymbolKind.Struct,
            "Record" => Contracts.Enums.SymbolKind.Record,
            "Enum" => Contracts.Enums.SymbolKind.Enum,
            "Delegate" => Contracts.Enums.SymbolKind.Delegate,
            "Constructor" or "Method" => Contracts.Enums.SymbolKind.Method,
            "Property" => Contracts.Enums.SymbolKind.Property,
            "Field" => Contracts.Enums.SymbolKind.Field,
            "Constant" or "EnumMember" => Contracts.Enums.SymbolKind.Constant,
            "Event" => Contracts.Enums.SymbolKind.Event,
            _ => null
        };

        private static OutlineEntry WithChildren(OutlineEntry entry, List<OutlineEntry> children) => new()
        {
            Name = entry.Name,
            Kind = entry.Kind,
            Line = entry.Line,
            Column = entry.Column,
            Accessibility = entry.Accessibility,
            ReturnType = entry.ReturnType,
            Children = children.Count > 0 ? children : null
        };
    }

    private static string? GetAccessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword) && modifiers.Any(SyntaxKind.ProtectedKeyword)) return "private protected";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword) && modifiers.Any(SyntaxKind.InternalKeyword)) return "protected internal";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        return null;
    }
}
