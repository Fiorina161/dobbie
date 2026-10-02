using System.Text;

namespace Dobby;

/// <summary>Builds the prompt sent to the Claude Code CLI for a run.</summary>
public static class PromptBuilder
{
    public const string QuestionPrefix = "QUESTION:";

    public static string Build(
        string root, IReadOnlyList<AiComment> comments, bool isCommand)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "You are dobby, an AI pair programmer watching a C#/VB.NET " +
            "repository. The user marks work for you with special one-line " +
            "\"AI comments\" in source files (\"//\" comments in C#, \"'\" " +
            "comments in VB.NET):");
        sb.AppendLine("- a comment starting or ending with \"AI!\" is an " +
            "instruction to change the code;");
        sb.AppendLine("- a comment starting or ending with \"AI?\" is a " +
            "question to answer;");
        sb.AppendLine("- a comment starting or ending with plain \"AI\" " +
            "gives extra context.");
        sb.AppendLine();
        sb.AppendLine("The following AI comments are currently present " +
            "(file, 1-based line number, kind, comment text):");

        foreach (var group in comments.GroupBy(c => c.FilePath))
        {
            sb.AppendLine();
            sb.AppendLine(
                Path.GetRelativePath(root, group.Key).Replace('\\', '/') + ":");
            foreach (var c in group.OrderBy(c => c.LineNumber))
                sb.AppendLine(
                    $"  line {c.LineNumber} [{Tag(c.Kind)}]: {c.Text}");
        }

        sb.AppendLine();
        if (isCommand)
        {
            sb.AppendLine(
                "Read the listed files, then carry out the instructions in " +
                "the AI! comments, using the plain AI comments as extra " +
                "context. Follow the existing code style of each file and " +
                "make only the changes the comments ask for. Also answer " +
                "any AI? questions in your reply.");
            sb.AppendLine(
                "After making the changes, remove ALL of the AI comments " +
                "listed above from the files (including the plain AI and " +
                "AI? ones).");
            sb.AppendLine();
            sb.AppendLine(
                "If an instruction is too ambiguous to act on safely, do " +
                "not edit any files; instead reply with a single line " +
                $"starting with \"{QuestionPrefix} \" followed by what you " +
                "need to know, and nothing else.");
        }
        else
        {
            sb.AppendLine(
                "Read the listed files and answer the question(s) in the " +
                "AI? comments, using the plain AI comments as context. " +
                "Then edit the files ONLY to remove the AI comments listed " +
                "above; make no other changes.");
        }
        return sb.ToString();
    }

    private static string Tag(AiCommentKind kind) => kind switch
    {
        AiCommentKind.Command => "AI!",
        AiCommentKind.Question => "AI?",
        _ => "AI",
    };
}
