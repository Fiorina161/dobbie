using Spectre.Console;

namespace Dobbie;

/// <summary>
/// All terminal output goes through this class. Spectre.Console is not
/// thread-safe, so every write takes the same lock.
/// </summary>
public sealed class Ui
{
    private readonly object _lock = new();
    private readonly bool _verbose;

    public Ui(bool verbose) => _verbose = verbose;

    private static string Ts() => $"[grey]{DateTime.Now:HH:mm:ss}[/]";

    public void Banner(Options o, string? gitRoot, string claudeExe)
    {
        lock (_lock)
        {
            AnsiConsole.Write(new FigletText("dobbie")
                .Color(Color.MediumPurple2));
            AnsiConsole.MarkupLine(
                $"[grey]watching :[/] {Markup.Escape(o.RootPath)} " +
                "[grey](*.cs, *.vb, subdirectories)[/]");
            AnsiConsole.MarkupLine(
                "[grey]backend  :[/] Claude Code CLI" +
                (o.Model is null
                    ? " [grey](default model)[/]"
                    : $" [grey](model {Markup.Escape(o.Model)})[/]"));
            string gitLine = gitRoot is null
                ? "[yellow]not a git repository - auto-commit disabled[/]"
                : $"{Markup.Escape(gitRoot)} [grey](auto-commit " +
                  $"{(o.AutoCommits ? "on" : "off")})[/]";
            AnsiConsole.MarkupLine($"[grey]git      :[/] {gitLine}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "Mark work with one-line comments " +
                "([teal]//[/] in C#, [teal]'[/] in VB.NET):");
            AnsiConsole.MarkupLine(
                "  [bold]AI![/] [grey]make this change[/]   " +
                "[bold]AI?[/] [grey]answer this question[/]   " +
                "[bold]AI[/] [grey]extra context[/]");
            AnsiConsole.MarkupLine("[grey]Type /help for commands.[/]");
            AnsiConsole.Write(new Rule());
        }
    }

    public void Info(string msg)
    {
        lock (_lock)
            AnsiConsole.MarkupLine($"{Ts()} [green]{Markup.Escape(msg)}[/]");
    }

    public void Warn(string msg)
    {
        lock (_lock)
            AnsiConsole.MarkupLine($"{Ts()} [yellow]{Markup.Escape(msg)}[/]");
    }

    public void Error(string msg)
    {
        lock (_lock)
            AnsiConsole.MarkupLine($"{Ts()} [red]{Markup.Escape(msg)}[/]");
    }

    public void Note(string msg)
    {
        lock (_lock)
            AnsiConsole.MarkupLine($"{Ts()} [grey]{Markup.Escape(msg)}[/]");
    }

    public void Verbose(string msg)
    {
        if (_verbose)
            Note(msg);
    }

    public void AssistantText(string text)
    {
        lock (_lock)
            AnsiConsole.Write(new Padder(
                new Markup($"[silver]{Markup.Escape(text.Trim())}[/]"),
                new Padding(2, 0, 0, 0)));
        lock (_lock)
            AnsiConsole.WriteLine();
    }

    public void ToolUse(string name, string? target, bool isEdit)
    {
        string label = target is null ? "" : $" [grey]{Markup.Escape(Shorten(target))}[/]";
        string color = isEdit ? "cyan" : "grey";
        lock (_lock)
            AnsiConsole.MarkupLine($"{Ts()} [{color}]> {name}[/]{label}");
    }

    public void RunStarted(
        bool isCommand, IReadOnlyList<AiComment> triggers, string root)
    {
        lock (_lock)
        {
            AnsiConsole.Write(new Rule(
                isCommand
                    ? "[mediumpurple2]making changes[/]"
                    : "[mediumpurple2]answering question[/]")
            { Justification = Justify.Left });
            foreach (var c in triggers)
            {
                string rel = Path.GetRelativePath(root, c.FilePath);
                AnsiConsole.MarkupLine(
                    $"{Ts()} [bold]{Markup.Escape(rel)}[/]" +
                    $"[grey]:{c.LineNumber}[/] {Markup.Escape(c.Text)}");
            }
        }
    }

    public void RunFinished(TimeSpan elapsed, double? cost, int turns)
    {
        string costPart = cost is > 0
            ? $" - est. ${cost:0.00##} (covered by subscription)" : "";
        lock (_lock)
        {
            AnsiConsole.MarkupLine(
                $"{Ts()} [grey]done in {elapsed.TotalSeconds:0.#}s - " +
                $"{turns} turn(s){Markup.Escape(costPart)}[/]");
            AnsiConsole.Write(new Rule());
        }
    }

    public void CommitDone(string shaShort, string firstLine, int fileCount)
    {
        lock (_lock)
            AnsiConsole.MarkupLine(
                $"{Ts()} [green]committed {shaShort}[/] " +
                $"[grey]({fileCount} file(s))[/] " +
                $"{Markup.Escape(firstLine)} [grey](/undo to revert)[/]");
    }

    public void AskUser(string question)
    {
        lock (_lock)
        {
            AnsiConsole.Write(new Panel(Markup.Escape(question))
                .Header("[yellow]dobbie asks[/]")
                .BorderColor(Color.Yellow));
            AnsiConsole.MarkupLine(
                "[yellow]Type your answer and press Enter:[/]");
        }
    }

    public static void PrintCommands()
    {
        AnsiConsole.MarkupLine("""
            [bold]Commands:[/]
              /undo     revert the last commit made by dobbie
              /pause    stop reacting to file changes
              /resume   start reacting again
              /status   show watcher state and pending AI comments
              /help     this list
              /quit     exit dobbie (also /exit or q)
            """);
    }

    private static string Shorten(string s)
        => s.Length <= 100 ? s : s[..97] + "...";
}
