using System.Diagnostics;
using System.Text;

namespace Dobby;

/// <summary>Thin wrapper over the git CLI for dobby's commit workflow.</summary>
public sealed class GitRepo
{
    public string Root { get; }

    private GitRepo(string root) => Root = root;

    public static GitRepo? Open(string path)
    {
        var r = RunIn(path, "rev-parse", "--show-toplevel");
        if (r.code != 0)
            return null;
        string root = r.stdout.Trim().Replace('/', Path.DirectorySeparatorChar);
        return root.Length == 0 ? null : new GitRepo(root);
    }

    public bool IsIgnored(string file)
        => RunIn(Root, "check-ignore", "-q", "--", file).code == 0;

    public bool IsClean()
        => RunIn(Root, "status", "--porcelain").stdout.Trim().Length == 0;

    /// <summary>Paths (relative to Root) with uncommitted changes.</summary>
    public IReadOnlyList<string> DirtyFiles()
    {
        var result = new List<string>();
        foreach (var raw in RunIn(Root, "status", "--porcelain")
                     .stdout.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 4)
                continue;
            string path = line[3..];
            // Renames are shown as "old -> new"; keep the new path.
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                path = path[(arrow + 4)..];
            path = path.Trim().Trim('"');
            if (path.Length > 0)
                result.Add(path.Replace('/', Path.DirectorySeparatorChar));
        }
        return result;
    }

    public string? HeadSha()
    {
        var r = RunIn(Root, "rev-parse", "HEAD");
        return r.code == 0 ? r.stdout.Trim() : null;
    }

    public string StagedDiff() => RunIn(Root, "diff", "--cached").stdout;

    public (bool ok, string error) Stage(IEnumerable<string> relativePaths)
    {
        var args = new List<string> { "add", "--" };
        args.AddRange(relativePaths);
        var r = RunIn(Root, args.ToArray());
        return (r.code == 0, r.stderr.Trim());
    }

    /// <summary>Commits staged changes; returns the new HEAD sha.</summary>
    public (bool ok, string shaOrError) Commit(string message)
    {
        string msgFile = Path.Combine(
            Path.GetTempPath(), $"dobby-commit-{Guid.NewGuid():N}.txt");
        File.WriteAllText(msgFile, message, new UTF8Encoding(false));
        try
        {
            // --no-verify matches aider's default (git-commit-verify: false).
            var r = RunIn(Root, "commit", "--no-verify", "-F", msgFile);
            if (r.code != 0)
                return (false, (r.stderr + r.stdout).Trim());
            return (true, HeadSha() ?? "");
        }
        finally
        {
            try { File.Delete(msgFile); } catch { /* temp file */ }
        }
    }

    /// <summary>
    /// Reverts the last commit, but only when HEAD is still the commit dobby
    /// made and the working tree is otherwise clean, so user work is never
    /// destroyed.
    /// </summary>
    public (bool ok, string message) UndoLast(string expectedSha)
    {
        if (HeadSha() != expectedSha)
            return (false,
                "HEAD moved since dobby's last commit; not undoing.");
        if (!IsClean())
            return (false,
                "Working tree has uncommitted changes; commit or stash them " +
                "before /undo.");
        var r = RunIn(Root, "reset", "--hard", "HEAD~1");
        return r.code == 0
            ? (true, $"Reverted commit {expectedSha[..7]}.")
            : (false, r.stderr.Trim());
    }

    private static (int code, string stdout, string stderr) RunIn(
        string dir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }
}
