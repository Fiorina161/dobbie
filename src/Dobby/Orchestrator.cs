using System.Diagnostics;

namespace Dobby;

/// <summary>
/// Watches the source tree, collects AI comments, fires Claude runs when a
/// trigger comment (AI! or AI?) appears, and commits the results.
/// </summary>
public sealed class Orchestrator : IDisposable
{
    private static readonly string[] ExcludedDirs =
        [".git", "bin", "obj", ".vs", ".claude", "node_modules", "packages",
         "TestResults"];
    private static readonly string[] Extensions = [".cs", ".vb"];

    private readonly Options _options;
    private readonly Ui _ui;
    private readonly ClaudeCliRunner _runner;
    private readonly GitRepo? _git;

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly object _gate = new();

    private readonly HashSet<string> _pendingFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AiComment>> _registry =
        new(StringComparer.OrdinalIgnoreCase);

    // Signatures of trigger comments already processed, so an unchanged AI!
    // comment (e.g. restored by /undo, or left behind after a failed run)
    // does not fire again and loop forever.
    private readonly HashSet<string> _handledTriggers = [];

    private readonly Stack<string> _undoStack = new();

    private CancellationTokenSource? _runCts;
    private TaskCompletionSource<string>? _pendingAnswer;

    public bool Paused { get; set; }

    public Orchestrator(
        Options options, Ui ui, ClaudeCliRunner runner, GitRepo? git)
    {
        _options = options;
        _ui = ui;
        _runner = runner;
        _git = git;

        _watcher = new FileSystemWatcher(_options.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += (_, e) => Enqueue(e.FullPath);
        _watcher.Created += (_, e) => Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        _debounce = new Timer(
            _ => _ = ProcessPendingAsync(), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        // Initial sweep so AI comments that already exist are picked up,
        // like aider's --watch-files does at startup.
        foreach (var file in EnumerateSourceFiles(_options.RootPath))
            Enqueue(file);
        _watcher.EnableRaisingEvents = true;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subdirs, files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var sub in subdirs)
                if (!ExcludedDirs.Contains(
                        Path.GetFileName(sub),
                        StringComparer.OrdinalIgnoreCase))
                    stack.Push(sub);
            foreach (var f in files)
                if (Extensions.Contains(
                        Path.GetExtension(f),
                        StringComparer.OrdinalIgnoreCase))
                    yield return f;
        }
    }

    private void Enqueue(string path)
    {
        if (!Extensions.Contains(
                Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return;
        if (IsExcluded(path))
            return;
        lock (_gate)
            _pendingFiles.Add(Path.GetFullPath(path));
        _debounce.Change(_options.DebounceMs, Timeout.Infinite);
    }

    private bool IsExcluded(string path)
    {
        string rel = Path.GetRelativePath(_options.RootPath, path);
        return rel.StartsWith("..")
            || rel.Split(
                   Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Any(p => ExcludedDirs.Contains(
                   p, StringComparer.OrdinalIgnoreCase));
    }

    private async Task ProcessPendingAsync()
    {
        if (Paused)
            return;
        // A run may be in progress; re-arm the debounce instead of queueing
        // a second processor.
        if (!await _processLock.WaitAsync(0))
        {
            _debounce.Change(_options.DebounceMs, Timeout.Infinite);
            return;
        }
        try
        {
            string[] files;
            lock (_gate)
            {
                files = [.. _pendingFiles];
                _pendingFiles.Clear();
            }
            if (files.Length == 0)
                return;

            bool registryChanged = false;
            foreach (var file in files)
            {
                List<AiComment> comments;
                try
                {
                    comments = File.Exists(file)
                        ? [.. AiCommentScanner.ScanFile(file)]
                        : [];
                }
                catch (IOException)
                {
                    // File busy (editor mid-save): retry on next event.
                    continue;
                }
                if (comments.Count > 0 && _git?.IsIgnored(file) == true)
                    continue;
                lock (_gate)
                {
                    if (comments.Count == 0)
                        registryChanged |= _registry.Remove(file);
                    else
                    {
                        _registry[file] = comments;
                        registryChanged = true;
                    }
                }
            }

            List<AiComment> all;
            List<AiComment> newTriggers;
            lock (_gate)
            {
                all = _registry.Values.SelectMany(c => c).ToList();
                newTriggers = all
                    .Where(c => c.Kind != AiCommentKind.Context
                                && !_handledTriggers.Contains(Signature(c)))
                    .ToList();
            }

            if (newTriggers.Count == 0)
            {
                if (registryChanged && all.Count > 0)
                    _ui.Note(
                        $"{all.Count} AI comment(s) noted - waiting for an " +
                        "AI! or AI? trigger");
                return;
            }

            await FireAsync(all, newTriggers);
        }
        catch (Exception ex)
        {
            _ui.Error($"watcher error: {ex.Message}");
        }
        finally
        {
            _processLock.Release();
        }
    }

    private static string Signature(AiComment c)
        => $"{c.FilePath}|{c.LineNumber}|{c.Text}|{c.Kind}";

    private async Task FireAsync(
        List<AiComment> all, List<AiComment> newTriggers)
    {
        bool isCommand = newTriggers.Any(
            c => c.Kind == AiCommentKind.Command);
        _ui.RunStarted(isCommand, newTriggers, _options.RootPath);

        string prompt = PromptBuilder.Build(_options.RootPath, all, isCommand);
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _runner.RunAsync(
                prompt, null, allowEdits: true, modelOverride: null,
                quiet: false, ct);

            // The model may ask for clarification instead of editing; relay
            // the question to the user and resume the same CLI session with
            // the answer.
            int rounds = 0;
            while (!result.IsError
                   && TryExtractQuestion(result.FinalText, out var question)
                   && rounds++ < 5)
            {
                string answer = await AskUserAsync(question);
                result = await _runner.RunAsync(
                    $"User's answer: {answer}\nNow proceed accordingly.",
                    result.SessionId, allowEdits: true, modelOverride: null,
                    quiet: false, ct);
            }

            lock (_gate)
                foreach (var c in newTriggers)
                    _handledTriggers.Add(Signature(c));

            if (result.IsError)
            {
                _ui.Error($"claude failed: {Truncate(result.FinalText, 2000)}");
                return;
            }

            bool commentsRemoved = RescanAfterRun(all);

            if (_options.AutoCommits && _git is not null)
                await CommitAsync(result, all, commentsRemoved, ct);

            _ui.RunFinished(sw.Elapsed, result.CostUsd, result.NumTurns);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                foreach (var c in newTriggers)
                    _handledTriggers.Add(Signature(c));
        }
        catch (Exception ex)
        {
            _ui.Error($"run failed: {ex.Message}");
        }
        finally
        {
            _runCts = null;
        }
    }

    /// <summary>
    /// Re-scans the files that had AI comments. Returns true when at least
    /// one processed comment is gone (i.e. the run visibly changed a file).
    /// </summary>
    private bool RescanAfterRun(List<AiComment> processed)
    {
        var stillPresent = new HashSet<string>();
        foreach (var file in processed.Select(c => c.FilePath).Distinct())
        {
            List<AiComment> remaining;
            try
            {
                remaining = File.Exists(file)
                    ? [.. AiCommentScanner.ScanFile(file)]
                    : [];
            }
            catch (IOException)
            {
                continue;
            }
            lock (_gate)
            {
                if (remaining.Count == 0)
                {
                    _registry.Remove(file);
                    continue;
                }
                _registry[file] = remaining;
                foreach (var c in remaining)
                {
                    stillPresent.Add(Signature(c));
                    if (c.Kind == AiCommentKind.Context)
                        continue;
                    _handledTriggers.Add(Signature(c));
                    _ui.Warn(
                        $"AI comment still present at " +
                        $"{Path.GetRelativePath(_options.RootPath, c.FilePath)}" +
                        $":{c.LineNumber} - edit it to retry.");
                }
            }
        }
        return processed.Any(c => !stillPresent.Contains(Signature(c)));
    }

    private async Task CommitAsync(
        ClaudeRunResult result, List<AiComment> commentSources,
        bool commentsRemoved, CancellationToken ct)
    {
        // A file being dirty is not enough: the user's own AI comment makes
        // it dirty. Only commit when the run itself visibly changed files.
        if (result.EditedFiles.Count == 0 && !commentsRemoved)
        {
            _ui.Note("model made no edits - nothing to commit");
            return;
        }

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in result.EditedFiles)
            touched.Add(Path.GetFullPath(
                Path.IsPathRooted(f)
                    ? f : Path.Combine(_options.RootPath, f)));
        foreach (var c in commentSources)
            touched.Add(Path.GetFullPath(c.FilePath));

        var dirty = new HashSet<string>(
            _git!.DirtyFiles().Select(
                f => Path.GetFullPath(Path.Combine(_git.Root, f))),
            StringComparer.OrdinalIgnoreCase);

        var toCommit = touched
            .Where(dirty.Contains)
            .Select(f => Path.GetRelativePath(_git.Root, f))
            .ToList();
        if (toCommit.Count == 0)
        {
            _ui.Note("no file changes to commit");
            return;
        }

        var (staged, stageError) = _git.Stage(toCommit);
        if (!staged)
        {
            _ui.Error($"git add failed: {stageError}");
            return;
        }

        string message = await GenerateCommitMessageAsync(ct);
        message +=
            "\n\nCo-authored-by: Claude (dobby) <noreply@anthropic.com>";

        var (ok, shaOrError) = _git.Commit(message);
        if (!ok)
        {
            _ui.Error($"git commit failed: {shaOrError}");
            return;
        }
        _undoStack.Push(shaOrError);
        _ui.CommitDone(
            shaOrError[..7], message.Split('\n')[0], toCommit.Count);
    }

    private async Task<string> GenerateCommitMessageAsync(CancellationToken ct)
    {
        const string fallback = "dobby: apply changes from AI comments";
        try
        {
            string diff = _git!.StagedDiff();
            if (diff.Trim().Length == 0)
                return fallback;
            if (diff.Length > 8000)
                diff = diff[..8000] + "\n... (diff truncated)";
            string prompt =
                "Write a one-line git commit message in Conventional " +
                "Commits style (e.g. \"fix: ...\", \"feat: ...\", " +
                "\"refactor: ...\") for the diff below. Reply with ONLY " +
                "the commit message line, nothing else.\n\n" + diff;
            var r = await _runner.RunAsync(
                prompt, null, allowEdits: false,
                modelOverride: _options.WeakModel, quiet: true, ct);
            string? line = r.FinalText
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
            if (r.IsError || string.IsNullOrWhiteSpace(line))
                return fallback;
            return line.Trim('`', '"', ' ');
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static bool TryExtractQuestion(string text, out string question)
    {
        question = "";
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (!t.StartsWith(
                    PromptBuilder.QuestionPrefix,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            // Include any following lines: the question may be multi-line.
            var rest = new[] { t[PromptBuilder.QuestionPrefix.Length..] }
                .Concat(lines.Skip(i + 1))
                .Select(l => l.Trim());
            question = string.Join('\n', rest).Trim();
            return question.Length > 0;
        }
        return false;
    }

    private Task<string> AskUserAsync(string question)
    {
        var tcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAnswer = tcs;
        _ui.AskUser(question);
        return tcs.Task;
    }

    /// <summary>Routes a typed line to a pending model question.</summary>
    public bool TryAnswerPendingQuestion(string answer)
    {
        var tcs = Interlocked.Exchange(ref _pendingAnswer, null);
        if (tcs is null)
            return false;
        tcs.TrySetResult(answer);
        return true;
    }

    /// <summary>Cancels an in-flight run. Returns false when idle.</summary>
    public bool TryCancelCurrentRun()
    {
        var cts = _runCts;
        if (cts is null)
            return false;
        Interlocked.Exchange(ref _pendingAnswer, null)?.TrySetCanceled();
        cts.Cancel();
        return true;
    }

    public void Undo()
    {
        if (_git is null)
        {
            _ui.Warn("Not a git repository; nothing to undo.");
            return;
        }
        if (_undoStack.Count == 0)
        {
            _ui.Warn("Nothing to undo (no dobby commits in this session).");
            return;
        }
        var (ok, message) = _git.UndoLast(_undoStack.Peek());
        if (ok)
        {
            _undoStack.Pop();
            _ui.Info(message);
        }
        else
        {
            _ui.Warn(message);
        }
    }

    public void PrintStatus()
    {
        int files, comments;
        lock (_gate)
        {
            files = _registry.Count;
            comments = _registry.Values.Sum(c => c.Count);
        }
        _ui.Info(
            $"{(Paused ? "paused" : "watching")} - {comments} AI comment(s) " +
            $"in {files} file(s) - {_undoStack.Count} undoable commit(s)");
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
        _runCts?.Cancel();
        _processLock.Dispose();
    }
}
