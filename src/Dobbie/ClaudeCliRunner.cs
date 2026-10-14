using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Dobbie;

public sealed record ClaudeRunResult(
    string? SessionId,
    string FinalText,
    bool IsError,
    IReadOnlyList<string> EditedFiles,
    double? CostUsd,
    int NumTurns);

/// <summary>
/// Runs the Claude Code CLI in headless mode ("claude -p") and parses its
/// stream-json output. Using the CLI means dobbie reuses the user's existing
/// Claude subscription login; no API key is needed.
/// </summary>
public sealed class ClaudeCliRunner
{
    private static readonly string[] EditToolNames =
        ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    private readonly Options _options;
    private readonly Ui _ui;
    private readonly string _claudeExe;

    public ClaudeCliRunner(Options options, Ui ui, string claudeExe)
    {
        _options = options;
        _ui = ui;
        _claudeExe = claudeExe;
    }

    /// <summary>Resolves the claude executable from PATH.</summary>
    public static string? ResolveClaudeCommand(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        string[] exts = OperatingSystem.IsWindows()
            ? [".exe", ".cmd", ".bat", ""]
            : [""];
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            foreach (var ext in exts)
            {
                string candidate = Path.Combine(dir.Trim(), command + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <param name="prompt">Prompt text, passed via stdin.</param>
    /// <param name="resumeSessionId">Continue an earlier CLI session.</param>
    /// <param name="allowEdits">Grant the acceptEdits permission mode so
    /// the model can modify files without prompting.</param>
    /// <param name="modelOverride">Model instead of Options.Model.</param>
    /// <param name="quiet">Suppress progress output (utility runs).</param>
    public async Task<ClaudeRunResult> RunAsync(
        string prompt,
        string? resumeSessionId,
        bool allowEdits,
        string? modelOverride,
        bool quiet,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _claudeExe,
            WorkingDirectory = _options.RootPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        // stream-json requires --verbose in print mode.
        psi.ArgumentList.Add("--verbose");
        if (allowEdits)
        {
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add("acceptEdits");
        }
        string? model = modelOverride ?? _options.Model;
        if (!string.IsNullOrEmpty(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }
        if (resumeSessionId is not null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(resumeSessionId);
        }

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (stderr) stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        using var killOnCancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });

        string? sessionId = resumeSessionId;
        string finalText = "";
        bool isError = false;
        double? cost = null;
        int numTurns = 0;
        var edited = new List<string>();

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None)
               is { } line)
        {
            if (line.Length == 0)
                continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t)
                    ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "system":
                        if (root.TryGetProperty("subtype", out var st)
                            && st.GetString() == "init")
                        {
                            if (root.TryGetProperty("session_id", out var sid0))
                                sessionId = sid0.GetString();
                            if (!quiet && _options.Verbose
                                && root.TryGetProperty("model", out var mm))
                                _ui.Verbose(
                                    $"claude session started ({mm.GetString()})");
                        }
                        break;

                    case "assistant":
                        HandleAssistantMessage(root, edited, quiet);
                        break;

                    case "result":
                        isError = root.TryGetProperty("is_error", out var ie)
                            && ie.GetBoolean();
                        if (root.TryGetProperty("result", out var r)
                            && r.ValueKind == JsonValueKind.String)
                            finalText = r.GetString() ?? "";
                        if (root.TryGetProperty("total_cost_usd", out var c)
                            && c.ValueKind == JsonValueKind.Number)
                            cost = c.GetDouble();
                        if (root.TryGetProperty("num_turns", out var nt)
                            && nt.ValueKind == JsonValueKind.Number)
                            numTurns = nt.GetInt32();
                        if (root.TryGetProperty("session_id", out var sid)
                            && sid.ValueKind == JsonValueKind.String)
                            sessionId = sid.GetString();
                        break;
                }
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0 && finalText.Length == 0)
        {
            isError = true;
            lock (stderr) finalText = stderr.ToString().Trim();
            if (finalText.Length == 0)
                finalText = $"claude exited with code {process.ExitCode}";
        }

        return new ClaudeRunResult(
            sessionId, finalText, isError, edited, cost, numTurns);
    }

    private void HandleAssistantMessage(
        JsonElement root, List<string> edited, bool quiet)
    {
        if (!root.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            string type = block.TryGetProperty("type", out var bt)
                ? bt.GetString() ?? "" : "";
            if (type == "text")
            {
                string? text = block.GetProperty("text").GetString();
                if (!string.IsNullOrWhiteSpace(text) && !quiet)
                    _ui.AssistantText(text);
            }
            else if (type == "tool_use")
            {
                string name = block.TryGetProperty("name", out var n)
                    ? n.GetString() ?? "?" : "?";
                string? target = null;
                if (block.TryGetProperty("input", out var input)
                    && input.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in (string[])
                        ["file_path", "notebook_path", "path", "pattern",
                         "command"])
                    {
                        if (input.TryGetProperty(key, out var v)
                            && v.ValueKind == JsonValueKind.String)
                        {
                            target = v.GetString();
                            break;
                        }
                    }
                }

                bool isEdit = EditToolNames.Contains(name);
                if (isEdit && target is not null && !edited.Contains(target))
                    edited.Add(target);
                if (!quiet)
                    _ui.ToolUse(name, target, isEdit);
            }
        }
    }
}
