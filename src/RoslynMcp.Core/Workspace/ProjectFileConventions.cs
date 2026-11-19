using System.Text;
using System.Text.Unicode;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// How the files of a project are written, for the files a refactoring creates: what the
/// <c>.editorconfig</c> that applies to a path says (<c>charset</c>, <c>end_of_line</c>), and
/// what most of the project's own files do.
/// </summary>
internal static class ProjectFileConventions
{
    /// <summary>
    /// The value of <paramref name="key"/> in the <c>.editorconfig</c> sections that apply to
    /// <paramref name="document"/>'s path, resolved by Roslyn's analyzer config set from the
    /// project's <c>.editorconfig</c> files (the ones MSBuild found next to its sources and
    /// above them); null when none sets it.
    /// </summary>
    /// <remarks>
    /// Roslyn matches a file against the directory of each <c>.editorconfig</c> ordinally, so
    /// both are spelled alike first: normalized (an 8.3 short name such as <c>PROGRA~1</c>
    /// expanded), and the file's path below a config's directory with that directory's letter
    /// case where the file system ignores case. A file a refactoring creates has the path its
    /// caller gave, which would otherwise match no section of a differently spelled directory.
    /// </remarks>
    public static async Task<string?> GetEditorConfigValueAsync(
        Document document, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(document.FilePath))
            return null;

        var path = PathResolver.NormalizePath(document.FilePath);
        var configs = new List<AnalyzerConfig>();
        foreach (var configDocument in document.Project.AnalyzerConfigDocuments)
        {
            if (string.IsNullOrEmpty(configDocument.FilePath))
                continue;

            var configPath = PathResolver.NormalizePath(configDocument.FilePath);
            var text = await configDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            configs.Add(AnalyzerConfig.Parse(text, configPath));
            path = SpellBelow(Path.GetDirectoryName(configPath), path);
        }

        if (configs.Count == 0)
            return null;

        return AnalyzerConfigSet.Create(configs).GetOptionsForSourcePath(path).AnalyzerOptions.TryGetValue(key, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// The encoding the <c>.editorconfig</c> <c>charset</c> for <paramref name="document"/>'s
    /// path names (see <see cref="SourceFileEncoding.FromEditorConfigCharset"/>); null when
    /// none applies.
    /// </summary>
    public static async Task<Encoding?> GetEditorConfigEncodingAsync(Document document, CancellationToken cancellationToken) =>
        SourceFileEncoding.FromEditorConfigCharset(
            await GetEditorConfigValueAsync(document, "charset", cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The line ending the <c>.editorconfig</c> <c>end_of_line</c> for
    /// <paramref name="document"/>'s path names (<c>lf</c>, <c>crlf</c>, <c>cr</c>); null when
    /// none applies.
    /// </summary>
    public static async Task<string?> GetEditorConfigLineEndingAsync(Document document, CancellationToken cancellationToken) =>
        (await GetEditorConfigValueAsync(document, "end_of_line", cancellationToken).ConfigureAwait(false))
            ?.Trim().ToLowerInvariant() switch
        {
            "lf" => "\n",
            "crlf" => "\r\n",
            "cr" => "\r",
            _ => null,
        };

    /// <summary>
    /// The encoding most of <paramref name="project"/>'s own files were read in, as their texts
    /// record it: UTF-8 or a code page, with or without byte order mark. Null when no encoding
    /// is recorded for more files than any other (a tie, or texts that record none). See
    /// <see cref="OwnDocuments"/> for which files count.
    /// </summary>
    public static Task<Encoding?> GetPrevailingEncodingAsync(Project project, CancellationToken cancellationToken) =>
        GetPrevailingAsync(
            project,
            text => text.Encoding switch
            {
                null => null,
                { CodePage: 65001 } utf8 => utf8.Preamble.Length > 0 ? SourceFileEncoding.Utf8WithBom : SourceFileEncoding.Utf8WithoutBom,
                var other => other,
            },
            encoding => (encoding.CodePage, encoding.Preamble.Length > 0),
            cancellationToken);

    /// <summary>
    /// The encoding each project of <paramref name="solution"/> declares for its files without
    /// byte order mark (see <see cref="InferDeclaredEncodingAsync"/>), for the projects that
    /// declare one as far as their documents tell. Meant for a solution as MSBuild loaded it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<ProjectId, Encoding>> InferDeclaredEncodingsAsync(
        Solution solution, CancellationToken cancellationToken)
    {
        var declared = new Dictionary<ProjectId, Encoding>();
        foreach (var project in solution.Projects)
        {
            if (await InferDeclaredEncodingAsync(project, cancellationToken).ConfigureAwait(false) is { } encoding)
                declared[project.Id] = encoding;
        }

        return declared;
    }

    /// <summary>
    /// The encoding <paramref name="project"/> declares for its files without byte order mark
    /// (MSBuild's <c>&lt;CodePage&gt;</c>), as the texts MSBuild decoded its documents to show
    /// it; null when it declares none, declares UTF-8, or its texts don't tell.
    /// </summary>
    /// <remarks>
    /// A loaded project does not carry the declared encoding: MSBuild hands it only to the
    /// loaders of the project's documents. But the compiler reads every file without byte
    /// order mark in it, also one that is valid UTF-8, which it reads as UTF-8 otherwise. So a
    /// document without byte order mark read as UTF-8 shows that the project declares none (or
    /// UTF-8), and one read in a code page although its bytes are valid UTF-8 (any file of
    /// plain ASCII) shows that the project declares that code page. A project whose files
    /// without byte order mark are all invalid UTF-8 doesn't tell; they read the same either
    /// way. Only texts as MSBuild loaded them tell this: a file a commit creates is labelled
    /// with the encoding it was written in.
    /// </remarks>
    public static async Task<Encoding?> InferDeclaredEncodingAsync(Project project, CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents)
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (text.Encoding is not { } encoding || encoding.Preamble.Length > 0)
                continue; // no encoding recorded, or read by its byte order mark

            if (encoding.CodePage == Encoding.UTF8.CodePage)
                return null;

            // UTF-16 or UTF-32 without byte order mark: only a declared encoding reads a file so.
            if (!SourceFileEncoding.IsCodePage(encoding) || Utf8.IsValid(encoding.GetBytes(text.ToString())))
                return encoding;
        }

        return null;
    }

    /// <summary>
    /// The line ending most of <paramref name="project"/>'s own files use, judged by their
    /// first line break. Null when none is used by more files than any other. See
    /// <see cref="OwnDocuments"/> for which files count.
    /// </summary>
    public static Task<string?> GetPrevailingLineEndingAsync(Project project, CancellationToken cancellationToken) =>
        GetPrevailingAsync(project, FirstLineBreak, lineEnding => lineEnding, cancellationToken);

    /// <summary>
    /// The first line break of <paramref name="text"/> (<c>\r\n</c>, <c>\n</c> or <c>\r</c>);
    /// null when it has none.
    /// </summary>
    public static string? FirstLineBreak(SourceText text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\n':
                    return "\n";
                case '\r':
                    return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
            }
        }

        return null;
    }

    /// <summary>
    /// The value most of the project's own files have, by <paramref name="keyOf"/>; null when
    /// none has a value or two values are equally frequent.
    /// </summary>
    private static async Task<TValue?> GetPrevailingAsync<TValue, TKey>(
        Project project,
        Func<SourceText, TValue?> select,
        Func<TValue, TKey> keyOf,
        CancellationToken cancellationToken)
        where TValue : class
        where TKey : notnull
    {
        var counts = new Dictionary<TKey, (TValue Value, int Count)>();
        foreach (var document in OwnDocuments(project))
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (select(text) is not { } value)
                continue;

            var key = keyOf(value);
            counts[key] = counts.TryGetValue(key, out var entry) ? (entry.Value, entry.Count + 1) : (value, 1);
        }

        var ranked = counts.Values.OrderByDescending(entry => entry.Count).Take(2).ToList();
        if (ranked.Count == 0 || (ranked.Count == 2 && ranked[1].Count == ranked[0].Count))
            return null;

        return ranked[0].Value;
    }

    /// <summary>
    /// <paramref name="path"/> with <paramref name="directory"/> spelled as given when the
    /// path lies below it, compared the way the file system does (so only letter case can
    /// differ); else <paramref name="path"/> unchanged. Both are normalized.
    /// </summary>
    private static string SpellBelow(string? directory, string path)
    {
        if (string.IsNullOrEmpty(directory))
            return path;

        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathResolver.PathComparison)
            ? prefix + path[prefix.Length..]
            : path;
    }

    /// <summary>
    /// The documents that show how the project's files are written: those in the project's
    /// directory, except its <c>bin</c> and <c>obj</c> folders, where MSBuild generates files
    /// (global usings, assembly attributes) in its own format. Linked files from elsewhere
    /// don't count either. A project without a file path counts every document with one.
    /// </summary>
    private static IEnumerable<Document> OwnDocuments(Project project)
    {
        var projectDirectory = Path.GetDirectoryName(project.FilePath);
        foreach (var document in project.Documents)
        {
            if (string.IsNullOrEmpty(document.FilePath))
                continue;

            if (!string.IsNullOrEmpty(projectDirectory))
            {
                var relative = Path.GetRelativePath(projectDirectory, document.FilePath);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (relative.Length > firstSegment.Length &&
                    (firstSegment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                     firstSegment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            yield return document;
        }
    }
}
