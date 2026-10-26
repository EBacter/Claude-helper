using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ClaudeHelper
{
    public sealed class ClaudeResult
    {
        public bool Success;
        public bool SessionNotFound;
        public string Text;
        public string SessionId;
        public List<string> BlockedActions = new List<string>();
        public List<string> Steps = new List<string>();

        /// <summary>How many tokens of conversation Claude read for its last step, or 0 if unknown.</summary>
        public int ContextTokens;

        /// <summary>The model that answered, as Claude Code reports it (for example "claude-opus-5-5"), or null.</summary>
        public string Model;

        public static ClaudeResult Ok(string text, string sessionId) =>
            new ClaudeResult { Success = true, Text = text, SessionId = sessionId };

        public static ClaudeResult Fail(string text, string sessionId = null) =>
            new ClaudeResult { Text = text, SessionId = sessionId };
    }

    /// <summary>
    /// A "claude -p" run that is in progress. It is serializable so the window can pick the answer
    /// back up after Unity reloads scripts (for example, after Claude edits a C# file).
    /// </summary>
    [Serializable]
    public class ClaudeRequest
    {
        public int processId;
        public long processStartTicks;
        public long startedUtcTicks;
        public string folder;

        // Progress read so far from the streamed output.
        public long readOffset;
        public List<string> steps = new List<string>();
        public int contextTokens;
        public string model;

        // The effort the window asked for, shown with the answer. Empty means Claude Code's default.
        public string effort;

        public double ElapsedSeconds => (DateTime.UtcNow - new DateTime(startedUtcTicks, DateTimeKind.Utc)).TotalSeconds;
    }

    /// <summary>
    /// Runs Claude Code as a separate process whose question, answer and errors are files in the project's
    /// Temp folder, so the Editor never waits on it and a script reload doesn't interrupt it.
    /// </summary>
    public static class ClaudeCodeRunner
    {
        const string PromptFile = "question.txt";
        const string SystemPromptFile = "instructions.txt";
        const string OutputFile = "answer.json";
        const string ErrorFile = "errors.txt";

        public static ClaudeRequest Start(string claudePath, string projectFolder, string prompt, string sessionId, string model,
            string effort, string systemPrompt, out string error)
        {
            error = null;
            var folder = Path.Combine(projectFolder, "Temp", "ClaudeHelper", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(folder, PromptFile), prompt, utf8);
            File.WriteAllText(Path.Combine(folder, SystemPromptFile), systemPrompt, utf8);

            // Full Claude Code: every default tool, your settings, skills and MCP servers. Auto mode approves
            // routine actions and blocks risky ones; nobody can answer a permission prompt in the background,
            // so anything that would need one is refused and reported back instead.
            var claudeCommand = new StringBuilder();
            claudeCommand.Append(Quote(claudePath));
            // stream-json writes one line per step as it happens (it needs --verbose), so the window can show progress.
            claudeCommand.Append(" -p --output-format stream-json --verbose");
            claudeCommand.Append(" --permission-mode auto");
            AppendModel(claudeCommand, model, effort);
            claudeCommand.Append(" --permission-prompts none");
            claudeCommand.Append(" --append-system-prompt-file ").Append(Quote(Path.Combine(folder, SystemPromptFile)));
            if (!string.IsNullOrEmpty(sessionId))
                claudeCommand.Append(" --resume ").Append(Quote(sessionId));
            claudeCommand.Append(" < ").Append(Quote(Path.Combine(folder, PromptFile)));
            claudeCommand.Append(" > ").Append(Quote(Path.Combine(folder, OutputFile)));
            claudeCommand.Append(" 2> ").Append(Quote(Path.Combine(folder, ErrorFile)));

            // cmd.exe handles the file redirection; /s strips the outer quotes and keeps everything inside as-is.
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"" + claudeCommand + "\"",
                WorkingDirectory = projectFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Never fall back to API-key billing: Claude Code must use the signed-in Claude subscription.
            startInfo.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            startInfo.EnvironmentVariables.Remove("ANTHROPIC_AUTH_TOKEN");

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "Windows didn't start the process for Claude Code.";
                        return null;
                    }

                    return new ClaudeRequest
                    {
                        processId = process.Id,
                        processStartTicks = process.StartTime.ToUniversalTime().Ticks,
                        startedUtcTicks = DateTime.UtcNow.Ticks,
                        folder = folder,
                    };
                }
            }
            catch (Win32Exception e)
            {
                error = $"Couldn't start Claude Code:\n{e.Message}";
                return null;
            }
        }

        /// <summary>
        /// Opens a terminal window running interactive Claude Code on an existing session, where it can ask
        /// for permission instead of refusing.
        /// </summary>
        public static bool OpenInTerminal(string claudePath, string projectFolder, string sessionId, string model, string effort,
            out string error)
        {
            error = null;

            // "|| pause" keeps the window open only when Claude Code fails, so its error can still be read.
            var claudeCommand = new StringBuilder(Quote(claudePath));
            AppendModel(claudeCommand, model, effort);
            claudeCommand.Append(" --resume ").Append(Quote(sessionId)).Append(" || pause");

            // Not a shell execute, so the environment can be changed. The Editor has no console of its own,
            // so Windows gives cmd.exe a new terminal window.
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"" + claudeCommand + "\"",
                WorkingDirectory = projectFolder,
                UseShellExecute = false,
            };
            startInfo.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            startInfo.EnvironmentVariables.Remove("ANTHROPIC_AUTH_TOKEN");

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                        return true;
                }

                error = "Windows didn't open the terminal window.";
                return false;
            }
            catch (Win32Exception e)
            {
                error = $"Couldn't open a terminal window:\n{e.Message}";
                return false;
            }
        }

        public static bool IsRunning(ClaudeRequest request)
        {
            try
            {
                using (var process = Process.GetProcessById(request.processId))
                {
                    if (process.HasExited)
                        return false;

                    // Windows reuses process IDs, so make sure this is still the same process.
                    var startTicks = process.StartTime.ToUniversalTime().Ticks;
                    return Math.Abs(startTicks - request.processStartTicks) < TimeSpan.TicksPerSecond;
                }
            }
            catch (Exception)
            {
                // ArgumentException: no process with that ID is running any more.
                return false;
            }
        }

        public static ClaudeResult ReadResult(ClaudeRequest request)
        {
            ReadProgress(request);
            var stdout = ReadFile(Path.Combine(request.folder, OutputFile));
            var stderr = ReadFile(Path.Combine(request.folder, ErrorFile));
            DeleteFolder(request);

            var result = ParseOutput(stdout, stderr);
            result.Steps = request.steps ?? new List<string>();
            result.ContextTokens = request.contextTokens;
            result.Model = request.model;
            return result;
        }

        /// <summary>
        /// Reads the lines Claude Code has streamed since the last call and records each tool it used and the
        /// size of the conversation. Only complete lines are read; a half-written one waits for the next call.
        /// </summary>
        public static void ReadProgress(ClaudeRequest request)
        {
            byte[] bytes;
            try
            {
                var path = Path.Combine(request.folder, OutputFile);
                if (!File.Exists(path))
                    return;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= request.readOffset)
                        return;

                    stream.Seek(request.readOffset, SeekOrigin.Begin);
                    bytes = new byte[stream.Length - request.readOffset];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0)
                            break;
                        read += n;
                    }

                    if (read == 0)
                        return;

                    var end = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                    if (end < 0)
                        return;

                    request.readOffset += end + 1;
                    bytes = bytes.Take(end + 1).ToArray();
                }
            }
            catch (IOException)
            {
                return;
            }

            request.steps ??= new List<string>();
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                var isInit = line.StartsWith("{\"type\":\"system\"") && line.Contains("\"subtype\":\"init\"");
                if (!isInit && !line.StartsWith("{\"type\":\"assistant\""))
                    continue;

                StreamLine parsed;
                try
                {
                    parsed = JsonUtility.FromJson<StreamLine>(line);
                }
                catch (Exception)
                {
                    continue;
                }

                if (isInit)
                {
                    if (!string.IsNullOrEmpty(parsed?.model))
                        request.model = parsed.model;
                    continue;
                }

                var message = parsed?.message;
                if (message == null)
                    continue;

                var usage = message.usage;
                if (usage != null)
                {
                    var tokens = usage.input_tokens + usage.cache_creation_input_tokens + usage.cache_read_input_tokens;
                    if (tokens > 0)
                        request.contextTokens = tokens;
                }

                if (message.content == null)
                    continue;

                foreach (var block in message.content.Where(b => b != null && b.type == "tool_use"))
                {
                    var step = DescribeTool(block.name, block.input);
                    if (step != null)
                        request.steps.Add(step);
                }
            }
        }

        public static void Cancel(ClaudeRequest request)
        {
            if (IsRunning(request))
            {
                try
                {
                    // End the whole process tree: cmd.exe, Claude Code and anything it started.
                    using (var taskkill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {request.processId} /T /F")
                           {
                               UseShellExecute = false,
                               CreateNoWindow = true,
                           }))
                    {
                        taskkill?.WaitForExit(3000);
                    }
                }
                catch (Exception)
                {
                    // The process already exited.
                }
            }

            DeleteFolder(request);
        }

        [Serializable]
        class CliResult
        {
            public string type;
            public string subtype;
            public bool is_error;
            public string result;
            public string session_id;
            public List<CliDenial> permission_denials;
        }

        [Serializable]
        class CliDenial
        {
            public string tool_name;
            public CliToolInput tool_input;
        }

        [Serializable]
        class CliToolInput
        {
            public string command;
            public string description;
            public string file_path;
            public string url;
            public string query;
            public string pattern;
            public string skill;
        }

        [Serializable]
        class StreamLine
        {
            // Only on the first line ("system"/"init"): the model that answers.
            public string model;
            public StreamMessage message;
        }

        [Serializable]
        class StreamMessage
        {
            public List<StreamBlock> content;
            public StreamUsage usage;
        }

        [Serializable]
        class StreamBlock
        {
            public string type;
            public string name;
            public CliToolInput input;
        }

        [Serializable]
        class StreamUsage
        {
            public int input_tokens;
            public int cache_creation_input_tokens;
            public int cache_read_input_tokens;
        }

        /// <summary>A short, plain description of a tool call, or null for bookkeeping tools not worth showing.</summary>
        static string DescribeTool(string name, CliToolInput input)
        {
            input ??= new CliToolInput();
            var file = string.IsNullOrEmpty(input.file_path) ? "a file" : Path.GetFileName(input.file_path);
            switch (name)
            {
                case "Read": return $"Reading {file}";
                case "Write": return $"Writing {file}";
                case "Edit":
                case "MultiEdit": return $"Editing {file}";
                case "NotebookEdit": return "Editing a notebook";
                case "Bash":
                case "PowerShell":
                    if (!string.IsNullOrWhiteSpace(input.description))
                        return input.description.Trim();
                    return string.IsNullOrWhiteSpace(input.command)
                        ? "Running a command"
                        : "Running " + Shorten(input.command.Trim(), 80);
                case "Grep": return $"Searching the project for \"{Shorten(input.pattern ?? "", 60)}\"";
                case "Glob": return $"Looking for files named {Shorten(input.pattern ?? "", 60)}";
                case "WebSearch": return $"Searching the web for \"{Shorten(input.query ?? "", 60)}\"";
                case "WebFetch": return $"Reading {Shorten(input.url ?? "a web page", 80)}";
                case "Skill": return string.IsNullOrEmpty(input.skill) ? "Loading a skill" : $"Loading the {input.skill} skill";
                case "Task":
                case "Agent":
                    return string.IsNullOrWhiteSpace(input.description) ? "Starting a helper agent" : "Helper agent: " + input.description.Trim();
                case "ToolSearch":
                case "TodoWrite":
                case "TaskCreate":
                case "TaskUpdate":
                case "TaskGet":
                case "TaskList":
                    return null;
            }

            if (name != null && name.StartsWith("mcp__"))
            {
                // mcp__server__tool
                var parts = name.Split(new[] { "__" }, StringSplitOptions.None);
                return parts.Length >= 3 ? $"Using {parts[2]} ({parts[1]})" : $"Using {name}";
            }

            return $"Using {name ?? "a tool"}";
        }

        static string Shorten(string text, int maxLength)
        {
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }

        static void AppendModel(StringBuilder command, string model, string effort)
        {
            // The values come from fixed lists, but they end up on a cmd.exe command line, so allow only plain names.
            if (IsPlainName(model))
                command.Append(" --model ").Append(model);
            if (IsPlainName(effort))
                command.Append(" --effort ").Append(effort);
        }

        static bool IsPlainName(string value) =>
            !string.IsNullOrEmpty(value) && value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_');

        static ClaudeResult ParseOutput(string stdout, string stderr)
        {
            var json = FindResultJson(stdout);
            if (json != null)
            {
                CliResult cli = null;
                try
                {
                    cli = JsonUtility.FromJson<CliResult>(json);
                }
                catch (Exception)
                {
                    // Fall through and show the raw output instead.
                }

                if (cli != null)
                {
                    ClaudeResult result;
                    if (!cli.is_error && cli.subtype == "success")
                    {
                        result = ClaudeResult.Ok(cli.result, cli.session_id);
                    }
                    else
                    {
                        var problem = string.IsNullOrWhiteSpace(cli.result)
                            ? $"The request ended with status \"{cli.subtype}\"."
                            : cli.result.Trim();
                        result = ClaudeResult.Fail("Claude Code reported a problem:\n" + problem + Hint(problem), cli.session_id);
                    }

                    if (cli.permission_denials != null)
                        result.BlockedActions = cli.permission_denials.Select(DescribeDenial).Distinct().ToList();
                    return result;
                }
            }

            var output = (stdout + "\n" + stderr).Trim();
            if (output.Contains("No conversation found"))
            {
                return new ClaudeResult
                {
                    SessionNotFound = true,
                    Text = "Claude Code couldn't find the previous conversation, so it may have been deleted or have expired.\n" +
                           "Your next question will start a new conversation.",
                };
            }

            if (output.Length == 0)
                return ClaudeResult.Fail("Claude Code stopped without printing an answer or an error.");

            if (output.Length > 2000)
                output = output.Substring(0, 2000) + "...";
            return ClaudeResult.Fail("Claude Code stopped with this message:\n" + output + Hint(output));
        }

        static string DescribeDenial(CliDenial denial)
        {
            var input = denial.tool_input;
            var detail = input == null
                ? null
                : new[] { input.command, input.file_path, input.url, input.query }.FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (detail != null && detail.Length > 200)
                detail = detail.Substring(0, 200) + "...";
            return detail == null ? denial.tool_name : $"{denial.tool_name}: {detail}";
        }

        static string FindResultJson(string stdout)
        {
            var lines = stdout.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("{") && line.Contains("\"type\":\"result\""))
                    return line;
            }

            return null;
        }

        public static bool LooksLikeSignInProblem(string problem)
        {
            var text = (problem ?? "").ToLowerInvariant();
            return text.Contains("login") || text.Contains("log in") || text.Contains("authenticat") || text.Contains("api key");
        }

        static string Hint(string problem)
        {
            var text = problem.ToLowerInvariant();
            if (LooksLikeSignInProblem(problem))
                return "\n\nTip: press Sign in at the top of this window and sign in with your Claude account. Then ask again.";
            if (text.Contains("usage limit") || text.Contains("rate limit") || text.Contains("limit reached"))
                return "\n\nTip: you may have reached your plan's usage limit. Wait a while, then try again.";
            if (text.Contains("is not recognized"))
                return "\n\nTip: check the path to Claude Code in Settings (top right of this window), or leave it empty to find it automatically.";
            if (text.Contains("auto mode") || text.Contains("permission-mode") || text.Contains("permission mode"))
                return "\n\nTip: this window runs Claude Code in auto mode. Check that auto mode is available for your account by running \"claude\" in a terminal.";
            return "";
        }

        static string ReadFile(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            catch (IOException)
            {
                return "";
            }
        }

        static void DeleteFolder(ClaudeRequest request)
        {
            try
            {
                if (Directory.Exists(request.folder))
                    Directory.Delete(request.folder, true);
            }
            catch (Exception)
            {
                // Unity clears the Temp folder when it closes anyway.
            }
        }

        // Paths never contain quotes, so wrapping them in quotes is enough for cmd.exe.
        static string Quote(string value) => "\"" + value + "\"";
    }
}
