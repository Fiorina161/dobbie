# dobbie

An AI pair programmer for **C# / VB.NET**, inspired by
[aider](https://aider.chat)'s [watch mode](https://aider.chat/docs/usage/watch.html),
written in .NET and powered by the **Claude Code CLI** — it reuses your
existing Claude subscription login, so **no API key is needed**.

dobbie runs in a terminal window and watches every `*.cs` and `*.vb` file
under a directory. You stay in your editor; when you want something done,
you type an *AI comment* in the code and save. dobbie notices, sends the
request to Claude, applies the edits, removes the AI comments, and commits
the result to git with a generated message.

## Requirements

- Windows, .NET 10 SDK (to build) or runtime (to run a published build)
- [Claude Code CLI](https://claude.com/claude-code) installed and logged in
  (`claude` must work in a terminal — dobbie shells out to it headlessly)
- git (auto-commit and `/undo` need the watched directory to be a git repo)

## Build & run

```powershell
git clone <this repo> ; cd dobbie
dotnet build
dotnet run --project src/Dobbie -- C:\path\to\your\repo
```

Or publish a standalone `dobbie.exe`:

```powershell
dotnet publish src/Dobbie -c Release -o publish
```

A launcher batch file (e.g. `C:\my\bin\dobbie.cmd`, with `C:\my\bin` on the
PATH) makes it available everywhere:

```bat
@echo off
"C:\dev\dobbie\publish\dobbie.exe" %*
```

After changing the sources, re-run the `dotnet publish` line above to
refresh the binary the launcher points at.

## AI comments

Same conventions as aider's watch mode, adapted to .NET languages. Write a
**one-line comment** (`//` in C#, `'` in VB.NET) whose text **starts or ends**
with `AI`, `AI!` or `AI?` (case doesn't matter):

| Marker | Meaning |
| ------ | ------- |
| `AI!`  | **Do it**: triggers dobbie to make the requested change |
| `AI?`  | **Question**: triggers dobbie to answer (shown in the terminal) |
| `AI`   | Context only: noted, but nothing runs until a `!` or `?` appears |

```csharp
// make this method async AI!
public int Fetch(string url) { ... }

public decimal Total;  // why is this decimal instead of double AI?
```

```vb
' convert this loop to LINQ AI!
For Each item In items
```

You can spread several plain `AI` comments across files to describe a bigger
task, then finish with one `AI!` — dobbie gathers **all** AI comments in the
repo and sends them together. After a run, dobbie removes the AI comments
from your files.

Notes:

- Comments inside string literals are ignored (`"http://x AI!"` won't fire).
- Files matched by `.gitignore`, and anything under `bin`, `obj`, `.git`,
  `.vs`, `node_modules`, is not watched.
- AI comments already present when dobbie starts are picked up immediately.

## What a run looks like

For an `AI!` trigger dobbie launches `claude -p` in headless mode with edit
permissions, streams its progress to the terminal (each file edit, each
tool step, the assistant's commentary), then:

1. re-scans the files (the AI comments should be gone),
2. stages exactly the files that were part of the task,
3. generates a one-line Conventional-Commits message with a small/fast
   model (`--weak-model`, default `haiku`),
4. commits with a `Co-authored-by: Claude (dobbie)` trailer — like aider's
   default attribution.

If the instruction is ambiguous, Claude may ask **you** a question instead
of editing: dobbie shows it in a yellow panel; type your answer and press
Enter and the same session continues.

For an `AI?` trigger dobbie answers the question in the terminal, removes
the comment, and makes no other changes.

## In-session commands

Type these in dobbie's terminal:

| Command   | Effect |
| --------- | ------ |
| `/undo`   | Revert the last commit dobbie made (refuses if HEAD moved or the tree is dirty, so your work is never destroyed) |
| `/pause`  | Stop reacting to file changes |
| `/resume` | React again |
| `/status` | Watcher state, pending AI comments, undoable commits |
| `/help`   | Command list |
| `/quit`   | Exit (also `/exit` or `q`); `Ctrl+C` cancels a run in progress |

After `/undo` the restored AI comment does **not** re-fire (dobbie remembers
it); edit the comment text if you want a retry.

## Options

```
dobbie [path] [options]

  -m, --model <name>   Claude model for edits (default: your Claude Code
                       default; e.g. sonnet, opus)
  --weak-model <name>  model for commit messages (default: haiku)
  --no-auto-commits    don't git-commit after each change
  --claude-cmd <path>  Claude Code CLI executable (default: claude on PATH)
  --debounce-ms <n>    delay after a save before scanning (default: 600)
  -v, --verbose        extra diagnostics
  --version, --help
```

## Differences from aider (v0.1)

dobbie deliberately implements only aider's watch-mode workflow for now:

- No interactive chat prompt, `/add`, repo map, architect/ask modes, voice,
  browser, images, or `.aider.conf.yml` config files.
- Backend is the Claude Code CLI only (that's the point: subscription
  login, no API key). Claude builds its own context by reading your repo,
  which replaces aider's repo-map machinery.
- VB.NET `'` comments are supported in addition to aider's `//` / `#` /
  `--` markers.
- Commits use aider's modern default attribution (co-authored-by trailer).

## Project layout

```
src/Dobbie/
  Program.cs            entry point, terminal input loop
  Options.cs            command-line parsing
  AiCommentScanner.cs   finds AI comments (string-literal aware)
  Orchestrator.cs       watcher, debounce, trigger -> run -> commit flow
  PromptBuilder.cs      builds the headless-claude prompt
  ClaudeCliRunner.cs    spawns claude -p, parses stream-json progress
  GitRepo.cs            git CLI wrapper (stage/commit/undo)
  Ui.cs                 Spectre.Console terminal output
tests/Dobbie.Tests/      xunit tests for scanner & prompt builder
```
