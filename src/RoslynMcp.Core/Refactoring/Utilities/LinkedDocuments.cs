using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Keeps the documents of one file in step when a project compiles it more than once (one
/// project per target framework, linked files): an operation edits one of them, the others get
/// its text.
/// </summary>
internal static class LinkedDocuments
{
    /// <summary>
    /// <paramref name="changed"/> with the other documents of each file in
    /// <paramref name="edited"/> given that document's new text; documents whose text did not
    /// change stay as they were.
    /// </summary>
    public static async Task<Solution> SyncAsync(
        Solution original,
        Solution changed,
        IEnumerable<DocumentId> edited,
        CancellationToken cancellationToken)
    {
        var result = changed;
        foreach (var id in edited.Distinct())
        {
            if (changed.GetDocument(id) is not { FilePath: { } path } document)
                continue;

            var text = await document.GetTextAsync(cancellationToken);
            if (original.GetDocument(id) is { } before && (await before.GetTextAsync(cancellationToken)).ContentEquals(text))
                continue;

            foreach (var linked in changed.GetDocumentIdsWithFilePath(path).Where(l => l != id))
                result = result.WithDocumentText(linked, text);
        }

        return result;
    }

    /// <summary>
    /// Gives the documents of a file that several projects compile (one per target framework)
    /// one text with the changes of all of them. An operation that changes each of those
    /// documents on its own (Roslyn's renamer, edits found through each framework's references
    /// and declarations) changes the code of an <c>#if</c> branch only in the documents that
    /// compile it, and the commit would write one of the versions. Each version's changes are
    /// found line by line against the original text (lines added or removed included) and
    /// combined: a change several versions make is made once; where two versions change the
    /// same lines differently, the version of <paramref name="preferred"/> (else the first
    /// document's) wins and the other's change there is reported in <paramref name="conflicts"/>.
    /// A document whose version already is the merged text keeps its syntax tree.
    /// </summary>
    /// <param name="original">The solution before the change.</param>
    /// <param name="changed">The solution with each document changed on its own.</param>
    /// <param name="preferred">The document whose version wins a conflict, e.g. the declaring one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="conflicts">Receives <c>path(line): ...</c> for each change left out.</param>
    public static async Task<Solution> MergeAsync(
        Solution original,
        Solution changed,
        DocumentId? preferred,
        CancellationToken cancellationToken,
        ICollection<string>? conflicts = null)
    {
        var result = changed;
        var paths = new HashSet<string>(PathResolver.PathComparer);
        var changedIds = changed.GetChanges(original).GetProjectChanges()
            .SelectMany(p => p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true));
        foreach (var id in changedIds)
        {
            if (changed.GetDocument(id)?.FilePath is not { } path || !paths.Add(path))
                continue;

            var ids = changed.GetDocumentIdsWithFilePath(path).OrderBy(d => d == preferred ? 0 : 1).ToList();
            if (ids.Count < 2 || original.GetDocument(ids[0]) is not { } originalDocument)
                continue;

            var originalText = await originalDocument.GetTextAsync(cancellationToken);
            var versions = new List<SourceText>();
            foreach (var linked in ids)
                versions.Add(await changed.GetDocument(linked)!.GetTextAsync(cancellationToken));

            if (versions.All(v => v.ContentEquals(versions[0])))
                continue;

            var (text, leftOut) = Merge(originalText, versions);
            foreach (var (line, kept, dropped) in leftOut)
            {
                conflicts?.Add(
                    $"{path}({line}): the target frameworks change this line differently; kept the change of " +
                    $"'{changed.GetDocument(ids[kept])!.Project.Name}', not that of '{changed.GetDocument(ids[dropped])!.Project.Name}'");
            }

            for (var i = 0; i < ids.Count; i++)
            {
                if (!versions[i].ContentEquals(text))
                    result = result.WithDocumentText(ids[i], text);
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="original"/> with the changes of every version, and the changes left out
    /// (the 1-based original line, the version kept there and the version left out).
    /// </summary>
    private static (SourceText Text, List<(int Line, int Kept, int Dropped)> LeftOut) Merge(
        SourceText original,
        IReadOnlyList<SourceText> versions)
    {
        var originalLines = Lines(original);
        var accepted = new List<Hunk>();
        var leftOut = new List<(int, int, int)>();
        for (var version = 0; version < versions.Count; version++)
        {
            foreach (var hunk in LineDiff.Hunks(originalLines, Lines(versions[version]), version))
            {
                if (accepted.Any(a => a.SameChange(hunk)))
                    continue;

                if (accepted.FirstOrDefault(a => a.Overlaps(hunk)) is { } kept)
                {
                    leftOut.Add((hunk.Start + 1, kept.Version, version));
                    continue;
                }

                accepted.Add(hunk);
            }
        }

        var merged = new StringBuilder();
        var next = 0;
        foreach (var hunk in accepted.OrderBy(h => h.Start).ThenBy(h => h.Count == 0 ? 0 : 1))
        {
            for (; next < hunk.Start; next++)
                merged.Append(originalLines[next]);
            foreach (var line in hunk.Lines)
                merged.Append(line);
            next = hunk.Start + hunk.Count;
        }

        for (; next < originalLines.Count; next++)
            merged.Append(originalLines[next]);

        return (SourceText.From(merged.ToString(), original.Encoding, original.ChecksumAlgorithm), leftOut);
    }

    /// <summary>The lines of <paramref name="text"/>, each with its line break.</summary>
    private static List<string> Lines(SourceText text) =>
        text.Lines.Select(line => text.ToString(line.SpanIncludingLineBreak)).ToList();
}

/// <summary>
/// A change of one version of a file: the <see cref="Count"/> original lines from
/// <see cref="Start"/> (0-based; none for an insertion) replaced by <see cref="Lines"/>.
/// </summary>
internal sealed record Hunk(int Start, int Count, IReadOnlyList<string> Lines, int Version)
{
    /// <summary>Whether <paramref name="other"/> makes the same change (from any version).</summary>
    public bool SameChange(Hunk other) => Start == other.Start && Count == other.Count && Lines.SequenceEqual(other.Lines);

    /// <summary>
    /// Whether the two changes touch the same original lines, or insert at the same place, so
    /// only one of them can be made.
    /// </summary>
    public bool Overlaps(Hunk other)
    {
        if (Count > 0 && other.Count > 0)
            return Start < other.Start + other.Count && other.Start < Start + Count;
        if (Count == 0 && other.Count == 0)
            return Start == other.Start;

        var (insertion, range) = Count == 0 ? (this, other) : (other, this);
        return range.Start < insertion.Start && insertion.Start < range.Start + range.Count;
    }
}

/// <summary>Line differences between two versions of a text (Myers' algorithm).</summary>
internal static class LineDiff
{
    // Beyond this many cells of trace (lines that differ times lines compared), the lines
    // between the common start and end are taken as one change rather than diffed.
    private const long MaxTraceCells = 4_000_000;

    /// <summary>
    /// The changes that turn <paramref name="before"/> into <paramref name="after"/>, in order.
    /// A change that replaces as many lines as it writes is given line by line (only the lines
    /// that differ), so a change of one line never covers its neighbours.
    /// </summary>
    public static List<Hunk> Hunks(IReadOnlyList<string> before, IReadOnlyList<string> after, int version)
    {
        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && before[prefix] == after[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < before.Count - prefix && suffix < after.Count - prefix &&
               before[before.Count - 1 - suffix] == after[after.Count - 1 - suffix])
        {
            suffix++;
        }

        var hunks = new List<Hunk>();
        var (x, y) = (prefix, prefix);
        var end = (before.Count - suffix, after.Count - suffix);
        foreach (var (kept, keptAfter) in CommonLines(before, after, prefix, end.Item1 - prefix, end.Item2 - prefix).Append(end))
        {
            if (kept > x || keptAfter > y)
                Add(hunks, before, after, x, kept - x, y, keptAfter - y, version);
            (x, y) = (kept + 1, keptAfter + 1);
        }

        return hunks;
    }

    private static void Add(List<Hunk> hunks, IReadOnlyList<string> before, IReadOnlyList<string> after, int start, int count, int newStart, int newCount, int version)
    {
        if (count != newCount)
        {
            hunks.Add(new Hunk(start, count, after.Skip(newStart).Take(newCount).ToList(), version));
            return;
        }

        for (var i = 0; i < count; i++)
        {
            if (before[start + i] != after[newStart + i])
                hunks.Add(new Hunk(start + i, 1, [after[newStart + i]], version));
        }
    }

    /// <summary>
    /// The lines both texts keep between <paramref name="offset"/> and the next
    /// <paramref name="n"/> / <paramref name="m"/> lines, as (index in before, index in after),
    /// in order; none when they are too different to diff cheaply.
    /// </summary>
    private static List<(int, int)> CommonLines(IReadOnlyList<string> before, IReadOnlyList<string> after, int offset, int n, int m)
    {
        var common = new List<(int, int)>();
        if (n == 0 || m == 0)
            return common;

        var max = n + m;
        var v = new int[(2 * max) + 2];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            if ((long)(d + 1) * v.Length > MaxTraceCells)
                return common;

            trace.Add((int[])v.Clone());
            var done = false;
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? v[max + k + 1] : v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && before[offset + x] == after[offset + y])
                    (x, y) = (x + 1, y + 1);

                v[max + k] = x;
                if (x >= n && y >= m)
                {
                    done = true;
                    break;
                }
            }

            if (done)
                break;
        }

        var (cx, cy) = (n, m);
        for (var d = trace.Count - 1; d >= 0 && (cx > 0 || cy > 0); d--)
        {
            var w = trace[d];
            var k = cx - cy;
            var previousK = k == -d || (k != d && w[max + k - 1] < w[max + k + 1]) ? k + 1 : k - 1;
            var previousX = w[max + previousK];
            var previousY = previousX - previousK;
            while (cx > previousX && cy > previousY)
            {
                (cx, cy) = (cx - 1, cy - 1);
                common.Add((offset + cx, offset + cy));
            }

            (cx, cy) = (previousX, previousY);
        }

        common.Reverse();
        return common;
    }
}
