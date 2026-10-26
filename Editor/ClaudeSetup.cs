using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace ClaudeHelper
{
    enum SetupState
    {
        Checking,
        NotInstalled,
        SignedOut,
        Ready,
    }

    /// <summary>The model and effort Claude Code uses when the window doesn't choose one. Null means not set.</summary>
    [Serializable]
    class ClaudeDefaults
    {
        public string model;
        public string effortLevel;

        // Claude Code also keeps an effort per model, under "modelSettings", keyed by model ID (for example "claude-opus-5-5").
        [NonSerialized] public Dictionary<string, string> modelEfforts = new Dictionary<string, string>();

        /// <summary>The effort Claude Code uses for a model alias or ID when none is chosen here, or null if it isn't set.</summary>
        public string EffortFor(string modelAliasOrId)
        {
            if (!string.IsNullOrEmpty(modelAliasOrId))
            {
                if (modelEfforts.TryGetValue(modelAliasOrId, out var exact))
                    return exact;

                // An alias such as "opus" means the newest model of that family, so take the highest ID that contains it.
                var newest = modelEfforts.Keys
                    .Where(id => id.IndexOf(modelAliasOrId, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(id => id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (newest != null)
                    return modelEfforts[newest];
            }

            return effortLevel;
        }
    }

    /// <summary>
    /// Finds Claude Code on this computer, and installs and signs in Claude Code for someone who doesn't have it yet.
    /// </summary>
    static class ClaudeSetup
    {
        /// <summary>Anthropic's official install command for Windows.</summary>
        public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

        const int StatusTimeoutMilliseconds = 15000;

        /// <summary>Where the official installer puts Claude Code.</summary>
        public static string InstallerPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");

        /// <summary>The first copy of Claude Code found on this computer, or null if there is none.</summary>
        public static string Find()
        {
            // The installer's folder comes first and is checked directly: Unity keeps the PATH it started with,
            // so a copy installed while Unity is open isn't on it yet.
            var candidates = new List<string> { InstallerPath };
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                candidates.Add(SafeCombine(folder, "claude.exe"));
                candidates.Add(SafeCombine(folder, "claude.cmd"));
            }

            // Where an npm install of Claude Code goes.
            candidates.Add(SafeCombine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"));
            return candidates.FirstOrDefault(path => path != null && File.Exists(path));
        }

        public static bool LooksValid(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // A bare command name such as "claude" is looked up on PATH when the process starts.
            var hasFolder = path.IndexOfAny(new[] { '\\', '/' }) >= 0;
            return !hasFolder || File.Exists(path);
        }

        /// <summary>
        /// Whether Claude Code is installed and signed in. Runs "claude auth status" off the main thread. Anything
        /// unexpected counts as ready, so a problem here never stands in the way; a real request reports it instead.
        /// </summary>
        public static Task<SetupState> CheckAsync(string claudePath)
        {
            if (string.IsNullOrEmpty(claudePath))
                return Task.FromResult(SetupState.NotInstalled);

            return Task.Run(() => CheckSignIn(claudePath));
        }

        static SetupState CheckSignIn(string claudePath)
        {
            var startInfo = CommandStartInfo(Quote(claudePath) + " auth status --json");
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return SetupState.Ready;

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(StatusTimeoutMilliseconds))
                    {
                        KillTree(process.Id);
                        return SetupState.Ready;
                    }

                    var output = stdout.Result + "\n" + stderr.Result;
                    if (Regex.IsMatch(output, "\"loggedIn\"\\s*:\\s*true"))
                        return SetupState.Ready;
                    if (Regex.IsMatch(output, "\"loggedIn\"\\s*:\\s*false"))
                        return SetupState.SignedOut;

                    // cmd.exe couldn't find a bare "claude" on PATH.
                    if (output.Contains("is not recognized"))
                        return SetupState.NotInstalled;

                    // For example, an older Claude Code without "auth status".
                    return SetupState.Ready;
                }
            }
            catch (Exception)
            {
                return SetupState.Ready;
            }
        }

        /// <summary>Opens a PowerShell window that runs the official installer and waits for Enter before closing.</summary>
        public static bool OpenInstaller(out string error)
        {
            error = null;
            var script = InstallCommand +
                         "; Write-Host ''; Write-Host 'Finished. Go back to Unity to continue.'; Read-Host 'Press Enter to close this window'";

            // Not a shell execute, so the environment matches the other commands. The Editor has no console of its own,
            // so Windows gives PowerShell a new window.
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"" + script + "\"",
                UseShellExecute = false,
            };
            return StartWindow(startInfo, "Couldn't open PowerShell to install Claude Code", out error);
        }

        /// <summary>Opens a terminal window where Claude Code signs in with a Claude subscription in the browser.</summary>
        public static bool OpenSignIn(string claudePath, out string error)
        {
            var startInfo = CommandStartInfo(Quote(claudePath) + " auth login --claudeai & echo. & echo Go back to Unity to continue. & pause");
            return StartWindow(startInfo, "Couldn't open a terminal window to sign in", out error);
        }

        /// <summary>A cmd.exe command without API keys, so Claude Code always uses the signed-in Claude subscription.</summary>
        static ProcessStartInfo CommandStartInfo(string command)
        {
            // /s strips the outer quotes and keeps everything inside as-is.
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"" + command + "\"",
                UseShellExecute = false,
            };
            startInfo.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            startInfo.EnvironmentVariables.Remove("ANTHROPIC_AUTH_TOKEN");
            return startInfo;
        }

        static bool StartWindow(ProcessStartInfo startInfo, string failure, out string error)
        {
            error = null;
            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                        return true;
                }

                error = failure + ".";
                return false;
            }
            catch (Win32Exception e)
            {
                error = $"{failure}:\n{e.Message}";
                return false;
            }
        }

        static void KillTree(int processId)
        {
            try
            {
                using (var taskkill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {processId} /T /F")
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

        static string SafeCombine(params string[] parts)
        {
            try
            {
                return parts.Any(string.IsNullOrWhiteSpace) ? null : Path.Combine(parts.Select(p => p.Trim().Trim('"')).ToArray());
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters that can't be in a path.
                return null;
            }
        }

        // Paths never contain quotes, so wrapping them in quotes is enough for cmd.exe.
        static string Quote(string value) => "\"" + value + "\"";

        /// <summary>Claude Code's settings folder, normally .claude in the user's home folder.</summary>
        public static string ConfigFolder
        {
            get
            {
                var folder = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                return string.IsNullOrEmpty(folder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                    : folder;
            }
        }

        /// <summary>
        /// The model and effort Claude Code picks on its own, read from its settings files the same way it layers them:
        /// the user's settings, then the project's, then the project's local ones.
        /// </summary>
        static readonly Regex ModelEffortPattern =
            new Regex(@"""(claude-[A-Za-z0-9.\-]+)""\s*:\s*\{[^{}]*?""effortLevel""\s*:\s*""([A-Za-z]+)""");

        public static ClaudeDefaults ReadDefaults(string projectFolder)
        {
            var defaults = new ClaudeDefaults();
            var files = new[]
            {
                Path.Combine(ConfigFolder, "settings.json"),
                Path.Combine(projectFolder, ".claude", "settings.json"),
                Path.Combine(projectFolder, ".claude", "settings.local.json"),
            };

            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file))
                        continue;
                    var json = File.ReadAllText(file);
                    var settings = JsonUtility.FromJson<ClaudeDefaults>(json);
                    if (!string.IsNullOrEmpty(settings?.model))
                        defaults.model = settings.model;
                    if (!string.IsNullOrEmpty(settings?.effortLevel))
                        defaults.effortLevel = settings.effortLevel;

                    // JsonUtility can't read dictionaries, so find each "<model ID>": { ... "effortLevel": "..." } entry directly.
                    foreach (Match match in ModelEffortPattern.Matches(json))
                        defaults.modelEfforts[match.Groups[1].Value] = match.Groups[2].Value;
                }
                catch (Exception)
                {
                    // A settings file Claude Code can read but JsonUtility can't (comments, for example): skip it.
                }
            }

            return defaults;
        }
    }
}
