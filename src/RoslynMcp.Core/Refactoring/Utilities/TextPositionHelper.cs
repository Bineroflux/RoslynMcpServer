using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Converts the 1-based line/column positions tools receive into absolute text positions.
/// </summary>
internal static class TextPositionHelper
{
    /// <summary>
    /// Converts a 1-based line/column to an absolute position with bounds validation.
    /// A column may point one past the last character (an exclusive selection end), but
    /// not further: that would silently spill into the next line.
    /// </summary>
    /// <param name="text">Source text to navigate.</param>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <returns>Absolute position in text.</returns>
    /// <exception cref="RefactoringException">Thrown if line/column is out of bounds.</exception>
    public static int GetPosition(SourceText text, int line, int column)
    {
        var lineIndex = line - 1; // Convert to 0-based

        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidLineNumber,
                $"Line {line} is out of range. File has {text.Lines.Count} lines.");
        }

        var lineInfo = text.Lines[lineIndex];
        var columnIndex = column - 1; // Convert to 0-based
        var lineLength = lineInfo.End - lineInfo.Start;

        if (columnIndex < 0 || columnIndex > lineLength)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidColumnNumber,
                $"Column {column} is out of range for line {line} (line has {lineLength} characters).");
        }

        return lineInfo.Start + columnIndex;
    }

    /// <summary>The span without the whitespace (indentation, line breaks) at its ends.</summary>
    public static TextSpan Trim(SourceText text, TextSpan span)
    {
        var start = span.Start;
        var end = span.End;
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;
        return TextSpan.FromBounds(start, end);
    }
}
