using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// The using directives of a file an operation creates for code it takes from another file: the
/// other file's usings, then only those the new file needs.
/// </summary>
internal static class NewFileUsings
{
    /// <summary>
    /// The using directives of <paramref name="sourceRoot"/> to copy into a new file of the same
    /// project, in their order: plain, static and alias directives, at file level and in namespace
    /// declarations. Global usings are left out: they apply to the new file already.
    /// </summary>
    public static List<UsingDirectiveSyntax> FromSource(SyntaxNode sourceRoot) =>
        sourceRoot.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None))
            .ToList();

    /// <summary>
    /// <paramref name="solution"/> with the using directives of the new document
    /// <paramref name="newDocument"/> removed that the compiler reports unnecessary there (CS8019)
    /// as every target framework of its project compiles it: a directive only code in another
    /// framework's <c>#if</c> branch uses stays. The new document is in one project only; the
    /// projects of its other frameworks get a copy of it for the check.
    /// </summary>
    public static async Task<Solution> RemoveUnnecessaryAsync(Solution solution, DocumentId newDocument, CancellationToken cancellationToken)
    {
        if (solution.GetDocument(newDocument) is not { FilePath: { } path } document)
            return solution;

        var text = await document.GetTextAsync(cancellationToken);
        var everyFramework = solution;
        foreach (var project in solution.Projects.Where(p => p.Id != document.Project.Id && p.FilePath != null &&
            PathResolver.PathsEqual(p.FilePath, document.Project.FilePath) && !p.Documents.Any(d => PathResolver.PathsEqual(d.FilePath, path))))
        {
            everyFramework = everyFramework.AddDocument(DocumentId.CreateNewId(project.Id), document.Name, text, document.Folders, path);
        }

        var (cleaned, removed) = await NewlyReportedUsings.RemoveLeftUnusedAsync(
            solution.RemoveDocument(newDocument), everyFramework, [newDocument], cancellationToken);
        return removed.Count == 0
            ? solution
            : solution.WithDocumentText(newDocument, await cleaned.GetDocument(newDocument)!.GetTextAsync(cancellationToken));
    }
}
