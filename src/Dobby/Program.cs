using System.Text;
using Dobby;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // Some redirected consoles refuse encoding changes; keep the default.
}

var options = Options.Parse(args, Console.Error, out int exitCode);
if (options is null)
    return exitCode;

var ui = new Ui(options.Verbose);

if (!Directory.Exists(options.RootPath))
{
    ui.Error($"Directory not found: {options.RootPath}");
    return 1;
}
options.RootPath = Path.GetFullPath(options.RootPath);

string? claudeExe =
    ClaudeCliRunner.ResolveClaudeCommand(options.ClaudeCommand);
if (claudeExe is null)
{
    ui.Error(
        $"Could not find the Claude Code CLI ('{options.ClaudeCommand}'). " +
        "Install it and log in (https://claude.com/claude-code), or pass " +
        "--claude-cmd <path>.");
    return 1;
}

var git = GitRepo.Open(options.RootPath);
if (git is null)
    options.AutoCommits = false;

ui.Banner(options, git?.Root, claudeExe);

using var orchestrator = new Orchestrator(
    options, ui, new ClaudeCliRunner(options, ui, claudeExe), git);

Console.CancelKeyPress += (_, e) =>
{
    if (orchestrator.TryCancelCurrentRun())
    {
        e.Cancel = true;
        ui.Warn("Run cancelled. (Ctrl+C again to quit.)");
    }
    // Otherwise let the default handler terminate dobby.
};

orchestrator.Start();

while (true)
{
    string? line = Console.ReadLine();
    if (line is null)
    {
        // stdin closed: when it was redirected (scripted/background use),
        // keep watching until the process is terminated.
        if (Console.IsInputRedirected)
        {
            ui.Note("stdin closed - watching until process exit (Ctrl+C).");
            await Task.Delay(Timeout.Infinite);
        }
        break;
    }

    line = line.Trim();
    if (line.Length == 0)
        continue;

    // A pending model question consumes the next non-command line.
    if (!line.StartsWith('/')
        && orchestrator.TryAnswerPendingQuestion(line))
        continue;

    switch (line.ToLowerInvariant())
    {
        case "/quit" or "/exit" or "q":
            return 0;
        case "/undo":
            orchestrator.Undo();
            break;
        case "/pause":
            orchestrator.Paused = true;
            ui.Info("Watching paused. /resume to continue.");
            break;
        case "/resume":
            orchestrator.Paused = false;
            ui.Info("Watching resumed.");
            break;
        case "/status":
            orchestrator.PrintStatus();
            break;
        case "/help":
            Ui.PrintCommands();
            break;
        default:
            ui.Warn($"Unknown command: {line} (/help for commands)");
            break;
    }
}

return 0;
