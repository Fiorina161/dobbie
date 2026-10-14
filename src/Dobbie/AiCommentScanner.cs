using System.Text.RegularExpressions;

namespace Dobbie;

/// <summary>What an AI comment asks dobbie to do.</summary>
public enum AiCommentKind
{
    /// <summary>Plain "AI": extra context, does not trigger a run.</summary>
    Context,

    /// <summary>"AI!": change the code.</summary>
    Command,

    /// <summary>"AI?": answer a question.</summary>
    Question,
}

public sealed record AiComment(
    string FilePath, int LineNumber, string Text, AiCommentKind Kind);

/// <summary>
/// Finds aider-style AI comments in C# and VB.NET source: one-line comments
/// whose text starts or ends with "AI", "AI!" or "AI?" (case-insensitive).
/// </summary>
public static partial class AiCommentScanner
{
    [GeneratedRegex(@"^\s*ai(?<mark>[!?])?(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingAi();

    [GeneratedRegex(@"(?:^|\s)ai(?<mark>[!?])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingAi();

    public static IReadOnlyList<AiComment> ScanFile(string path)
        => ScanLines(path, File.ReadLines(path));

    public static IReadOnlyList<AiComment> ScanLines(
        string path, IEnumerable<string> lines)
    {
        bool isVb = Path.GetExtension(path)
            .Equals(".vb", StringComparison.OrdinalIgnoreCase);
        string marker = isVb ? "'" : "//";

        var result = new List<AiComment>();
        int lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;

            // Markers inside string/char literals must not count, so search
            // for the marker in a copy whose literals are blanked out (same
            // column positions), then take the comment text from the original.
            string stripped = StripStringLiterals(line, isVb);
            int idx = stripped.IndexOf(marker, StringComparison.Ordinal);
            while (idx >= 0)
            {
                string text = line[(idx + marker.Length)..].Trim();
                if (TryClassify(text, out var kind))
                {
                    result.Add(new AiComment(path, lineNo, text, kind));
                    break;
                }
                idx = stripped.IndexOf(marker, idx + 1, StringComparison.Ordinal);
            }
        }
        return result;
    }

    private static bool TryClassify(string text, out AiCommentKind kind)
    {
        var m = LeadingAi().Match(text);
        if (!m.Success)
            m = TrailingAi().Match(text);
        if (!m.Success)
        {
            kind = default;
            return false;
        }
        kind = m.Groups["mark"].Value switch
        {
            "!" => AiCommentKind.Command,
            "?" => AiCommentKind.Question,
            _ => AiCommentKind.Context,
        };
        return true;
    }

    /// <summary>
    /// Replaces the contents of string and char literals with spaces so that
    /// comment markers inside them are ignored. Column positions are kept.
    /// Multi-line literals (verbatim/raw strings) are not tracked; that is an
    /// accepted false-positive risk, same as aider's line-based scan.
    /// </summary>
    internal static string StripStringLiterals(string line, bool isVb)
    {
        var chars = line.ToCharArray();
        int i = 0;
        while (i < chars.Length)
        {
            char c = chars[i];
            if (c == '"')
            {
                bool verbatim = !isVb && i > 0 && chars[i - 1] == '@';
                int j = i + 1;
                while (j < chars.Length)
                {
                    if (chars[j] == '"')
                    {
                        // "" escapes a quote in VB and C# verbatim strings.
                        if ((isVb || verbatim)
                            && j + 1 < chars.Length && chars[j + 1] == '"')
                        {
                            chars[j] = ' ';
                            chars[j + 1] = ' ';
                            j += 2;
                            continue;
                        }
                        break;
                    }
                    if (!isVb && !verbatim && chars[j] == '\\')
                    {
                        chars[j] = ' ';
                        if (j + 1 < chars.Length) chars[j + 1] = ' ';
                        j += 2;
                        continue;
                    }
                    chars[j] = ' ';
                    j++;
                }
                i = j + 1;
            }
            else if (!isVb && c == '\'')
            {
                // C# char literal: 'x' or an escape like '\n'.
                int j = i + 1;
                if (j < chars.Length && chars[j] == '\\')
                {
                    chars[j] = ' ';
                    j++;
                }
                if (j < chars.Length && chars[j] != '\'')
                {
                    chars[j] = ' ';
                    j++;
                }
                i = j + 1;
            }
            else
            {
                i++;
            }
        }
        return new string(chars);
    }
}
