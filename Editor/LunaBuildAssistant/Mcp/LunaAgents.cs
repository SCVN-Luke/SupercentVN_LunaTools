using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supercent.Util.Editor
{
    // "Auto configure" in the Build Assistant: registers the MCP broker (~/.supercent/luna-mcp/luna-mcp.mjs) in the user-level
    // MCP config of each AI agent found on this machine, so every project on the machine gets the Luna tools.
    //   Claude Code   `claude mcp add -s user` (its CLI owns ~/.claude.json)
    //   Codex         ~/.codex/config.toml          [mcp_servers.luna-devtools]   (CLI, IDE extension and app share it)
    //   Cursor        ~/.cursor/mcp.json            mcpServers
    //   Antigravity   ~/.gemini/antigravity/mcp_config.json   mcpServers
    //   Gemini CLI    ~/.gemini/settings.json       mcpServers
    //   Windsurf      ~/.codeium/windsurf/mcp_config.json      mcpServers
    //   VS Code       <user dir>/Code/User/mcp.json  servers (Copilot agent mode)
    // Only the luna-devtools entry is written; everything else in the file is kept. Each file is copied to
    // <file>.luna-backup before its first change. A file that is not plain JSON (comments) is left alone.
    // No Unity API here: tested outside the Editor.
    public static class LunaAgents
    {
        public const string ServerName = "luna-devtools";
        const string WorkspaceVariable = "${workspaceFolder}";

        public enum Format { ClaudeCli, Json, Toml }
        public enum State { Missing, Configured, Outdated, Unreadable }

        public sealed class Agent
        {
            public string Name;
            public string ConfigPath;
            public Format Format;
            public string JsonKey = "mcpServers";
            public bool StdioType;          // VS Code wants "type": "stdio".
            public bool PassWorkspace;      // Resolves ${workspaceFolder}: the broker gets the open folder as LUNA_PROJECT.
            public State State;
            public string Note;
            public override string ToString() => $"{Name}: {State} ({ConfigPath})";
        }

        static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static bool Windows => Path.DirectorySeparatorChar == '\\';
        static bool Mac => !Windows && Directory.Exists("/Applications") && Directory.Exists("/System/Library");

        // ---- Finding the agents ----

        // The agents installed on this machine (their config folder exists), each with whether the broker is registered.
        public static List<Agent> Detect(string node, string broker)
        {
            var agents = new List<Agent>();
            string claudeJson = Path.Combine(Home, ".claude.json");
            if (File.Exists(claudeJson) || FindExecutable("claude") != null)
                agents.Add(new Agent { Name = "Claude Code", ConfigPath = claudeJson, Format = Format.ClaudeCli });

            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrEmpty(codexHome))
                codexHome = Path.Combine(Home, ".codex");
            if (Directory.Exists(codexHome))
                agents.Add(new Agent { Name = "Codex", ConfigPath = Path.Combine(codexHome, "config.toml"), Format = Format.Toml });

            AddJson(agents, "Cursor", Path.Combine(Home, ".cursor"), "mcp.json", passWorkspace: true);
            AddJson(agents, "Antigravity", Path.Combine(Home, ".gemini", "antigravity"), "mcp_config.json");
            if (File.Exists(Path.Combine(Home, ".gemini", "settings.json")))
                AddJson(agents, "Gemini CLI", Path.Combine(Home, ".gemini"), "settings.json");
            AddJson(agents, "Windsurf", Path.Combine(Home, ".codeium", "windsurf"), "mcp_config.json");

            string vsCodeUser = VsCodeUserDir();
            if (vsCodeUser != null)
                AddJson(agents, "VS Code", vsCodeUser, "mcp.json", passWorkspace: true, key: "servers", stdioType: true);

            foreach (Agent agent in agents)
                ReadState(agent, node, broker);
            return agents;
        }

        static void AddJson(List<Agent> agents, string name, string dir, string file, bool passWorkspace = false,
                            string key = "mcpServers", bool stdioType = false)
        {
            if (Directory.Exists(dir))
                agents.Add(new Agent
                {
                    Name = name, ConfigPath = Path.Combine(dir, file), Format = Format.Json, JsonKey = key,
                    PassWorkspace = passWorkspace, StdioType = stdioType,
                });
        }

        static string VsCodeUserDir()
        {
            string dir;
            if (Windows)
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User");
            else if (Mac)
                dir = Path.Combine(Home, "Library", "Application Support", "Code", "User");
            else
                dir = Path.Combine(Home, ".config", "Code", "User");
            return Directory.Exists(dir) ? dir : null;
        }

        // ---- Reading the current entry ----

        public static void ReadState(Agent agent, string node, string broker)
        {
            agent.Note = null;
            try
            {
                if (!File.Exists(agent.ConfigPath))
                {
                    agent.State = State.Missing;
                    return;
                }
                string text = File.ReadAllText(agent.ConfigPath);
                if (agent.Format == Format.Toml)
                {
                    agent.State = TomlState(text, node, broker);
                    return;
                }
                Dictionary<string, object> root = ParseObject(text);
                if (root == null)
                {
                    agent.State = State.Unreadable;
                    agent.Note = "Not plain JSON (comments?): add the entry by hand.";
                    return;
                }
                string key = agent.Format == Format.ClaudeCli ? "mcpServers" : agent.JsonKey;
                var entry = Get(Get(root, key) as Dictionary<string, object>, ServerName) as Dictionary<string, object>;
                agent.State = entry == null ? State.Missing
                    : Matches(MiniJson.Str(entry, "command"), FirstArg(Get(entry, "args")), node, broker) ? State.Configured : State.Outdated;
            }
            catch (Exception e)
            {
                agent.State = State.Unreadable;
                agent.Note = e.Message;
            }
        }

        static object Get(Dictionary<string, object> obj, string key) => obj != null && obj.TryGetValue(key, out object v) ? v : null;

        static string FirstArg(object args) => args is List<object> list && list.Count > 0 ? list[0] as string : null;

        // The same broker started by the same node (a node found on PATH counts when no full path is known).
        static bool Matches(string command, string arg, string node, string broker)
        {
            if (command == null || arg == null)
                return false;
            bool sameNode = string.Equals(command, node, StringComparison.Ordinal)
                            || (node == "node" && Path.GetFileNameWithoutExtension(command) == "node");
            return sameNode && SamePath(arg, broker);
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
                    Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new Dictionary<string, object>();
            try
            {
                return MiniJson.Obj(MiniJson.Parse(text.TrimStart('﻿')));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- Writing the entry ----

        // Registers the broker in every agent that does not have it yet. Returns a summary for the window.
        public static string ConfigureAll(IEnumerable<Agent> agents, string node, string broker)
        {
            var done = new List<string>();
            var failed = new List<string>();
            foreach (Agent agent in agents.Where(a => a.State == State.Missing || a.State == State.Outdated))
            {
                string error = Configure(agent, node, broker);
                if (error == null)
                    done.Add(agent.Name);
                else
                    failed.Add(agent.Name + ": " + error);
                ReadState(agent, node, broker);
            }
            var lines = new List<string>();
            if (done.Count > 0)
                lines.Add($"Set up {string.Join(", ", done)}. Restart them (or reload their MCP servers) to see {ServerName}.");
            lines.AddRange(failed);
            return lines.Count == 0 ? "Every agent found already has " + ServerName + "." : string.Join("\n", lines);
        }

        // Writes the luna-devtools entry for one agent. Null when it worked, otherwise what went wrong.
        public static string Configure(Agent agent, string node, string broker)
        {
            if (!File.Exists(broker))
                return "the broker is not installed yet (" + broker + "): open the Build Assistant once with the MCP server on.";
            try
            {
                switch (agent.Format)
                {
                    case Format.ClaudeCli: return ConfigureClaude(node, broker);
                    case Format.Toml: return ConfigureToml(agent.ConfigPath, node, broker);
                    default: return ConfigureJson(agent, node, broker);
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        static string ConfigureJson(Agent agent, string node, string broker)
        {
            string text = File.Exists(agent.ConfigPath) ? File.ReadAllText(agent.ConfigPath) : string.Empty;
            Dictionary<string, object> root = ParseObject(text);
            if (root == null)
                return "not plain JSON (comments?), left unchanged. Add by hand: " + JsonSnippet(agent, node, broker);
            if (!(Get(root, agent.JsonKey) is Dictionary<string, object> servers))
                root[agent.JsonKey] = servers = new Dictionary<string, object>();
            var entry = Get(servers, ServerName) as Dictionary<string, object> ?? new Dictionary<string, object>();
            if (agent.StdioType)
                entry["type"] = "stdio";
            entry["command"] = node;
            entry["args"] = new List<object> { broker };
            if (agent.PassWorkspace)
            {
                var env = Get(entry, "env") as Dictionary<string, object> ?? new Dictionary<string, object>();
                env["LUNA_PROJECT"] = WorkspaceVariable;
                entry["env"] = env;
            }
            servers[ServerName] = entry;
            WriteConfig(agent.ConfigPath, Pretty(root) + "\n");
            return null;
        }

        public static string JsonSnippet(Agent agent, string node, string broker)
        {
            var entry = new Dictionary<string, object>();
            if (agent.StdioType)
                entry["type"] = "stdio";
            entry["command"] = node;
            entry["args"] = new List<object> { broker };
            if (agent.PassWorkspace)
                entry["env"] = new Dictionary<string, object> { { "LUNA_PROJECT", WorkspaceVariable } };
            return MiniJson.Write(new Dictionary<string, object> { { ServerName, entry } });
        }

        static string ConfigureClaude(string node, string broker)
        {
            string claude = FindExecutable("claude");
            if (claude == null)
                return "the claude command was not found. Run in a terminal: " + ClaudeCommand(node, broker);
            Run(claude, new[] { "mcp", "remove", ServerName, "-s", "user" }, node, out _);     // Outdated entry; fails when none.
            if (Run(claude, new[] { "mcp", "add", "-s", "user", ServerName, "--", node, broker }, node, out string output) != 0)
                return "claude mcp add failed: " + output.Trim();
            return null;
        }

        public static string ClaudeCommand(string node, string broker) => $"claude mcp add -s user {ServerName} -- {Quote(node)} {Quote(broker)}";

        static void WriteConfig(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            string backup = path + ".luna-backup";
            if (File.Exists(path) && !File.Exists(backup))
                File.Copy(path, backup);
            // Written in place (not replaced): the file may be a symlink into a dotfiles repository.
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        // ---- Codex TOML ----

        static readonly Regex TomlHeader = new Regex(@"^\s*\[\s*mcp_servers\s*\.\s*(?:luna-devtools|""luna-devtools""|'luna-devtools')\s*\]\s*(?:#.*)?$");
        static readonly Regex TomlAnyHeader = new Regex(@"^\s*\[");
        static readonly Regex TomlOwnKey = new Regex(@"^\s*(command|args)\s*=");
        static readonly Regex TomlCommand = new Regex(@"^\s*command\s*=\s*""((?:[^""\\]|\\.)*)""");
        static readonly Regex TomlFirstArg = new Regex(@"^\s*args\s*=\s*\[\s*""((?:[^""\\]|\\.)*)""");

        static State TomlState(string text, string node, string broker)
        {
            string[] lines = SplitLines(text);
            int header = Array.FindIndex(lines, l => TomlHeader.IsMatch(l));
            if (header < 0)
                return State.Missing;
            string command = null, arg = null;
            for (int i = header + 1; i < lines.Length && !TomlAnyHeader.IsMatch(lines[i]); i++)
            {
                Match m = TomlCommand.Match(lines[i]);
                if (m.Success)
                    command = TomlUnescape(m.Groups[1].Value);
                m = TomlFirstArg.Match(lines[i]);
                if (m.Success)
                    arg = TomlUnescape(m.Groups[1].Value);
            }
            return Matches(command, arg, node, broker) ? State.Configured : State.Outdated;
        }

        // Sets command / args in [mcp_servers.luna-devtools], keeping its other keys and sub-tables (tool approvals, env).
        static string ConfigureToml(string path, string node, string broker)
        {
            string text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var lines = SplitLines(text).ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            string commandLine = "command = " + TomlString(node);
            string argsLine = "args = [" + TomlString(broker) + "]";
            int header = lines.FindIndex(l => TomlHeader.IsMatch(l));
            if (header < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                    lines.Add(string.Empty);
                lines.Add("[mcp_servers." + ServerName + "]");
                lines.Add(commandLine);
                lines.Add(argsLine);
            }
            else
            {
                int i = header + 1;
                while (i < lines.Count && !TomlAnyHeader.IsMatch(lines[i]))
                {
                    if (!TomlOwnKey.IsMatch(lines[i]))
                    {
                        i++;
                        continue;
                    }
                    // Drop the old value, also an array written over several lines.
                    int depth = Depth(lines[i]);
                    lines.RemoveAt(i);
                    while (depth > 0 && i < lines.Count)
                    {
                        depth += Depth(lines[i]);
                        lines.RemoveAt(i);
                    }
                }
                lines.Insert(header + 1, argsLine);
                lines.Insert(header + 1, commandLine);
            }
            WriteConfig(path, string.Join("\n", lines) + "\n");
            return null;
        }

        static int Depth(string line)
        {
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inString)
                {
                    if (c == '\\')
                        i++;
                    else if (c == '"')
                        inString = false;
                }
                else if (c == '"')
                    inString = true;
                else if (c == '#')
                    break;
                else if (c == '[')
                    depth++;
                else if (c == ']')
                    depth--;
            }
            return depth;
        }

        static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

        static string TomlString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string TomlUnescape(string value) => Regex.Replace(value, @"\\(.)", m => m.Groups[1].Value);

        // ---- Pretty JSON (agents' files stay readable) ----

        public static string Pretty(object value)
        {
            var sb = new StringBuilder();
            WritePretty(sb, value, 0);
            return sb.ToString();
        }

        static void WritePretty(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case Dictionary<string, object> obj when obj.Count > 0:
                    sb.Append("{\n");
                    int n = 0;
                    foreach (var pair in obj)
                    {
                        sb.Append(' ', indent + 2).Append(MiniJson.Write(pair.Key)).Append(": ");
                        WritePretty(sb, pair.Value, indent + 2);
                        sb.Append(++n < obj.Count ? ",\n" : "\n");
                    }
                    sb.Append(' ', indent).Append('}');
                    return;
                case List<object> list when list.Count > 0:
                    sb.Append("[\n");
                    for (int i = 0; i < list.Count; i++)
                    {
                        sb.Append(' ', indent + 2);
                        WritePretty(sb, list[i], indent + 2);
                        sb.Append(i + 1 < list.Count ? ",\n" : "\n");
                    }
                    sb.Append(' ', indent).Append(']');
                    return;
                case Dictionary<string, object> _:
                    sb.Append("{}");
                    return;
                case List<object> _:
                    sb.Append("[]");
                    return;
                default:
                    sb.Append(MiniJson.Write(value));
                    return;
            }
        }

        // ---- node and CLIs ----

        static readonly Dictionary<string, string> found = new Dictionary<string, string>();

        // Full path of node, so agents started from the Dock (without the shell's PATH) find it. "node" when not found.
        public static string Node => FindExecutable("node") ?? "node";

        // Usual install places first, then the user's login shell (nvm, volta, asdf ...). Cached per script reload.
        public static string FindExecutable(string name)
        {
            if (found.TryGetValue(name, out string cached))
                return cached;
            string exe = Windows ? name + ".exe" : name;
            var places = new List<string>
            {
                Path.Combine(Home, ".local", "bin", exe),
                Path.Combine("/opt/homebrew/bin", exe),
                Path.Combine("/usr/local/bin", exe),
                Path.Combine(Home, ".volta", "bin", exe),
            };
            if (name == "node" && Windows)
                places.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", exe));
            if (name == "claude")
                places.Add(Path.Combine(Home, ".claude", "local", exe));
            string path = places.FirstOrDefault(File.Exists) ?? FromShell(name);
            found[name] = path;
            return path;
        }

        static string FromShell(string name)
        {
            try
            {
                string output;
                int code = Windows
                    ? RunRaw("where", name, null, out output)
                    : RunRaw(File.Exists("/bin/zsh") && Mac ? "/bin/zsh" : (Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh"),
                             "-lc \"command -v " + name + "\"", null, out output);
                string first = code == 0 ? SplitLines(output).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && File.Exists(l)) : null;
                return first;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static int Run(string file, IEnumerable<string> args, string node, out string output) =>
            RunRaw(file, string.Join(" ", args.Select(Quote)), node, out output);

        static string Quote(string arg) => arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '"', '\'', '\\', '$' }) < 0 ? arg : "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static int RunRaw(string file, string arguments, string node, out string output)
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            // A CLI installed with npm starts with "#!/usr/bin/env node": give it node's folder.
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string nodeDir = node != null && Path.IsPathRooted(node) ? Path.GetDirectoryName(node) : null;
            psi.EnvironmentVariables["PATH"] = string.Join(Path.PathSeparator.ToString(),
                new[] { nodeDir, Path.Combine(Home, ".local", "bin"), path, Windows ? null : "/opt/homebrew/bin", Windows ? null : "/usr/local/bin" }
                    .Where(p => !string.IsNullOrEmpty(p)));
            using (Process process = Process.Start(psi))
            {
                if (process == null)
                {
                    output = "could not start " + file;
                    return -1;
                }
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(); } catch (Exception) { /* already gone */ }
                    output = file + " did not finish in 30 s";
                    return -1;
                }
                output = stdout.Result + stderr.Result;
                return process.ExitCode;
            }
        }

        public static string Describe(IEnumerable<Agent> agents)
        {
            var list = agents.ToList();
            if (list.Count == 0)
                return "No AI agent found on this machine (Claude Code, Codex, Cursor, Antigravity, Gemini CLI, Windsurf, VS Code).";
            return string.Join("\n", list.Select(a => a.Name + ": " + StateText(a.State) + "  (" + a.ConfigPath + ")" +
                                                      (a.Note != null ? "\n    " + a.Note : string.Empty)));
        }

        public static string StateText(State state)
        {
            switch (state)
            {
                case State.Configured: return "set up";
                case State.Outdated: return "set up with another path (will be updated)";
                case State.Unreadable: return "config not readable";
                default: return "not set up";
            }
        }
    }
}
