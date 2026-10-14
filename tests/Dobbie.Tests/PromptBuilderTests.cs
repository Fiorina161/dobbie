namespace Dobbie.Tests;

public class PromptBuilderTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static string FilePath(string name) => Path.Combine(Root, name);

    [Fact]
    public void CommandRun_ListsFilesLinesAndQuestionProtocol()
    {
        var comments = new List<AiComment>
        {
            new(FilePath("Service.cs"), 42,
                "make this method async AI!", AiCommentKind.Command),
            new(FilePath("Service.cs"), 10,
                "AI the cache must stay lazy", AiCommentKind.Context),
        };

        string prompt = PromptBuilder.Build(Root, comments, isCommand: true);

        Assert.Contains("Service.cs", prompt);
        Assert.Contains("line 42", prompt);
        Assert.Contains("line 10", prompt);
        Assert.Contains("make this method async AI!", prompt);
        Assert.Contains(PromptBuilder.QuestionPrefix, prompt);
        Assert.Contains("remove ALL of the AI comments", prompt);
    }

    [Fact]
    public void QuestionRun_AsksForAnswerAndCommentRemovalOnly()
    {
        var comments = new List<AiComment>
        {
            new(FilePath("Module1.vb"), 7,
                "why does this overflow AI?", AiCommentKind.Question),
        };

        string prompt = PromptBuilder.Build(Root, comments, isCommand: false);

        Assert.Contains("Module1.vb", prompt);
        Assert.Contains("answer the question", prompt);
        Assert.Contains("make no other changes", prompt);
        Assert.DoesNotContain(PromptBuilder.QuestionPrefix, prompt);
    }
}
