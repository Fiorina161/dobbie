namespace Dobby;

/// <summary>Command-line options for dobby.</summary>
public sealed class Options
{
    public const string Version = "0.1.0";

    public string RootPath { get; set; } = Directory.GetCurrentDirectory();
    public string? Model { get; set; }
    public string WeakModel { get; set; } = "haiku";
    public bool AutoCommits { get; set; } = true;
    public bool Verbose { get; set; }
    public string ClaudeCommand { get; set; } = "claude";
    public int DebounceMs { get; set; } = 600;

    public static Options? Parse(string[] args, TextWriter error, out int exitCode)
    {
        exitCode = 0;
        var o = new Options();
        string? positional = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            string? Next()
            {
                if (i + 1 >= args.Length)
                {
                    error.WriteLine($"Missing value for {a}");
                    return null;
                }
                return args[++i];
            }

            switch (a)
            {
                case "--model" or "-m":
                    if (Next() is not { } model) { exitCode = 1; return null; }
                    o.Model = model;
                    break;
                case "--weak-model":
                    if (Next() is not { } weak) { exitCode = 1; return null; }
                    o.WeakModel = weak;
                    break;
                case "--claude-cmd":
                    if (Next() is not { } cmd) { exitCode = 1; return null; }
                    o.ClaudeCommand = cmd;
                    break;
                case "--debounce-ms":
                    if (Next() is not { } ms || !int.TryParse(ms, out var v) || v < 50)
                    {
                        error.WriteLine("--debounce-ms expects an integer >= 50");
                        exitCode = 1;
                        return null;
                    }
                    o.DebounceMs = v;
                    break;
                case "--no-auto-commits":
                    o.AutoCommits = false;
                    break;
                case "--verbose" or "-v":
                    o.Verbose = true;
                    break;
                case "--version":
                    Console.WriteLine($"dobby {Version}");
                    return null;
                case "--help" or "-h" or "/?":
                    PrintUsage(Console.Out);
                    return null;
                default:
                    if (a.StartsWith('-'))
                    {
                        error.WriteLine($"Unknown option: {a} (try --help)");
                        exitCode = 1;
                        return null;
                    }
                    if (positional is not null)
                    {
                        error.WriteLine("Only one path argument is allowed.");
                        exitCode = 1;
                        return null;
                    }
                    positional = a;
                    break;
            }
        }

        if (positional is not null)
            o.RootPath = positional;
        return o;
    }

    public static void PrintUsage(TextWriter o)
    {
        o.WriteLine($"""
            dobby {Version} - AI pair programmer for C#/VB.NET, powered by the
            Claude Code CLI (uses your existing Claude subscription login).

            dobby watches *.cs and *.vb files under a directory. Mark work for
            it with one-line comments ("//" in C#, "'" in VB.NET):

              // make this method async AI!     change the code
              // why does this test fail AI?    answer a question
              // the cache must stay lazy AI    extra context for the task

            Usage:
              dobby [path] [options]

            Arguments:
              path                 Directory to watch (default: current dir).

            Options:
              -m, --model <name>   Claude model for edits (default: your
                                   Claude Code default, e.g. sonnet/opus).
              --weak-model <name>  Model for commit messages (default: haiku).
              --no-auto-commits    Do not git-commit after each change.
              --claude-cmd <path>  Claude Code CLI executable (default:
                                   "claude", resolved from PATH).
              --debounce-ms <n>    Delay after a file change before scanning
                                   (default: 600).
              -v, --verbose        Show extra diagnostics.
              --version            Print version and exit.
              -h, --help           Show this help.

            In-session commands: /undo /pause /resume /status /help /quit
            """);
    }
}
