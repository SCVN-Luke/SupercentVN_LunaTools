using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // MCP server for AI agents, served by the Dev Tools proxy at /mcp (this machine only).
    // Page tools (DevToolsMcpTools) run in the Build Develop pages; the tools here run in the Editor on the main thread.
    // Started with Unity when enabled, so an agent's MCP configuration keeps working across domain reloads.
    [InitializeOnLoad]
    public static class LunaMcp
    {
        const string EnabledPref = "SupercentVN.LunaBuildAssistant.Mcp";
        const int MainThreadTimeoutMs = 20000;

        public static readonly DevToolsChannel Channel = new DevToolsChannel();
        public static readonly McpServer Server = new McpServer { Instructions = DevToolsMcpTools.Instructions };

        static readonly Queue<Action> mainThread = new Queue<Action>();
        static Dictionary<string, object> editorState = new Dictionary<string, object>();
        static double stateAt;

        static LunaMcp()
        {
            DevToolsMcpTools.Register(Server, Channel, () => editorState);
            RegisterEditorTools();
            EditorApplication.update += Update;
            if (Enabled)
                EditorApplication.delayCall += () => LunaDevTools.Start();
        }

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, false);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                if (value)
                    LunaDevTools.Start();
                LunaDevTools.AttachAgents();
            }
        }

        public static string Url => LunaDevTools.IsRunning && Enabled ? LunaDevTools.LocalUrl + "mcp" : null;
        public static string ClaudeCommand => "claude mcp add --transport http luna-devtools " + (Url ?? "http://localhost:" + LunaDevTools.FirstPort + "/mcp");
        public static int ConnectedPages => Channel.Sessions().Count;

        static void Update()
        {
            while (true)
            {
                Action action;
                lock (mainThread)
                {
                    if (mainThread.Count == 0)
                        break;
                    action = mainThread.Dequeue();
                }
                action();
            }
            // Editor state for luna_status, read off the main thread by the MCP requests.
            if (EditorApplication.timeSinceStartup - stateAt > 1.0)
            {
                stateAt = EditorApplication.timeSinceStartup;
                DateTime? last = ProjectBuildState.LastPrivateBuildUtc;
                editorState = new Dictionary<string, object>
                {
                    { "project", Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) },
                    { "unity", Application.unityVersion },
                    { "playworksPlugin", LunaPluginBridge.Version },
                    { "devToolsUrl", LunaDevTools.LocalUrl },
                    { "phoneUrl", LunaDevTools.LanUrl },
                    { "developServerPort", LunaDevTools.UpstreamPort },
                    { "building", LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding() },
                    { "lastBuildDevelop", last.HasValue ? last.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : null },
                };
            }
        }

        static bool SafeIsBuilding()
        {
            try
            {
                return LunaPluginBridge.IsAvailable && LunaPluginBridge.IsBuilding;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Runs on Unity's main thread and waits for it (MCP requests arrive on the proxy's threads).
        public static T OnMainThread<T>(Func<T> work, int timeoutMs = MainThreadTimeoutMs)
        {
            T result = default;
            Exception error = null;
            using (var done = new ManualResetEventSlim())
            {
                lock (mainThread)
                    mainThread.Enqueue(() =>
                    {
                        try { result = work(); }
                        catch (Exception e) { error = e; }
                        finally { done.Set(); }
                    });
                if (!done.Wait(timeoutMs))
                    throw new TimeoutException("Unity did not respond (compiling, importing, or a dialog is open). Retry in a moment.");
            }
            if (error != null)
                throw new InvalidOperationException(error.Message, error);
            return result;
        }

        static void RegisterEditorTools()
        {
            Server.Register(new McpServer.Tool
            {
                Name = "unity_build_develop",
                Description = "Runs Clear Cache + Build Develop in the Unity Editor (Luna Build Assistant), waits for it, and returns the result: " +
                              "errors (compile errors, unsupported C# / API, Playground sign-in) with file:line, Playworks warnings, duration. " +
                              "With openPage the playable opens in the browser through the Dev Tools, so it connects for the luna_* tools. " +
                              "Unity may ask the user to save a modified scene first.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "openPage", McpServer.Prop("boolean", "Open the built page through the Dev Tools", true) },
                    { "waitSeconds", McpServer.Prop("number", "How long to wait for the build (it keeps going after)", 300) },
                }),
                Run = args =>
                {
                    bool openPage = MiniJson.Bool(args, "openPage", true);
                    string refused = OnMainThread(() => LunaBuildAssistantWindow.StartAgentBuild(openPage));
                    if (refused != null)
                        return McpServer.ToolResult.Text(refused, true);
                    DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(10, Math.Min(900, MiniJson.Num(args, "waitSeconds", 300))));
                    while (LunaBuildAssistantWindow.AgentBuildRunning && DateTime.UtcNow < until)
                        Thread.Sleep(500);
                    if (LunaBuildAssistantWindow.AgentBuildRunning)
                        return McpServer.ToolResult.Text("Still building; call unity_build_report later.");
                    var summary = OnMainThread(() => BuildSummary(LunaBuildAssistantWindow.AgentBuildReport, LunaBuildAssistantWindow.AgentBuildStatus));
                    summary["seconds"] = Math.Round(LunaBuildAssistantWindow.AgentBuildSeconds, 1);
                    return McpServer.ToolResult.Json(summary);
                },
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_build_report",
                Description = "The Playworks plugin's Project Diagnostics of the last build (errors and warnings with C# file:line) and the result " +
                              "of the last unity_build_develop.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() =>
                {
                    var data = new Dictionary<string, object>
                    {
                        { "diagnostics", BuildSummary(BuildDiagnostics.Collect("Last build", false, false, null), null) },
                    };
                    if (LunaBuildAssistantWindow.AgentBuildReport != null || LunaBuildAssistantWindow.AgentBuildStatus != null)
                        data["lastAgentBuild"] = BuildSummary(LunaBuildAssistantWindow.AgentBuildReport, LunaBuildAssistantWindow.AgentBuildStatus);
                    return data;
                })),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_luna_checks",
                Description = "The Build Assistant's checks: code Luna will strip although running code still calls it (from Build Develop " +
                              "runtime analysis), and known places where Luna behaves differently from Unity (each with its fix).",
                ReadOnly = true,
                InputSchema = McpServer.Schema(),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() =>
                {
                    var lint = LunaLint.Run();
                    var stripped = StrippedCodeCheck.Run();
                    return new Dictionary<string, object>
                    {
                        { "lunaChecks", lint.Rules.Select(r => (object)new Dictionary<string, object>
                            {
                                { "id", r.Id }, { "title", r.Title }, { "fix", r.Fix },
                                { "issues", lint.Issues.Where(i => i.RuleId == r.Id).Take(30).Select(i => (object)i.Message).ToList() },
                            }).ToList() },
                        { "strippedButCalled", stripped == null ? null : stripped.Referenced.Take(40).Select(f => (object)new Dictionary<string, object>
                            {
                                { "member", f.Label }, { "calledBy", f.ReferencedBy.Take(5).ToList() },
                            }).ToList() },
                        { "strippedNotCalled", stripped == null ? 0 : stripped.Unreferenced.Count },
                    };
                })),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_find_source",
                Description = "C# name from a stack or a hot path (Namespace.Class.Method, Class.Method, Class, or Luna's Class#Method) -> the " +
                              "project source file, line, and the lines around it.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "name", McpServer.Prop("string", "e.g. Supercent.BulldozerMaster.TradingPost.TradingPostOreCargo.SwitchView") },
                    { "context", McpServer.Prop("integer", "Lines of code around it", 20) },
                }, "name"),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => FindSource(MiniJson.Str(args, "name"), (int)MiniJson.Num(args, "context", 20)))),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_open_source",
                Description = "Opens a C# name (as for unity_find_source) in the user's code editor, at its line.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(new Dictionary<string, object> { { "name", McpServer.Prop("string", "C# name") } }, "name"),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() =>
                {
                    if (!Resolve(MiniJson.Str(args, "name"), out Type type, out string member))
                        throw new InvalidOperationException("No project class for " + MiniJson.Str(args, "name"));
                    return new Dictionary<string, object> { { "opened", ProjectCode.OpenSource(type, member) } };
                })),
            });
        }

        static Dictionary<string, object> BuildSummary(BuildReport report, string status)
        {
            var data = new Dictionary<string, object> { { "status", status } };
            if (report == null)
            {
                data["failed"] = status != null && status.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0;
                return data;
            }
            data["failed"] = report.Failed;
            data["needsSignIn"] = report.NeedsSignIn;
            data["errors"] = report.Errors.Take(40).Select(Issue).ToList();
            data["warningCount"] = report.Warnings.Count;
            data["warnings"] = report.Warnings.Take(20).Select(Issue).ToList();
            data["log"] = report.LogPath;
            return data;
        }

        static object Issue(BuildIssue i) => new Dictionary<string, object>
        {
            { "message", i.Message }, { "details", i.Details }, { "code", i.Code }, { "file", i.File }, { "line", i.Line > 0 ? (object)i.Line : null }, { "symbol", i.Symbol },
        };

        // "Ns.Class.Method", "Class.Method", "Class", "Ns.Class#Method#get" -> project type + Luna member key.
        static bool Resolve(string name, out Type type, out string member)
        {
            type = null;
            member = null;
            if (string.IsNullOrEmpty(name))
                return false;
            name = name.Trim().Replace(" (coroutine)", string.Empty).Replace(" (property / constructor)", string.Empty);
            string memberPart = null;
            int hash = name.IndexOf('#');
            if (hash >= 0)
            {
                memberPart = name.Substring(hash + 1);
                name = name.Substring(0, hash);
            }
            var byFull = ProjectCode.Types.GroupBy(ProjectCode.ClassName).ToDictionary(g => g.Key, g => g.First());
            string[] parts = name.Split('.');
            for (int take = parts.Length; take >= 1; take--)
            {
                string candidate = string.Join(".", parts.Take(take));
                Type found = byFull.TryGetValue(candidate, out Type exact) ? exact
                    : byFull.Where(kv => kv.Key.EndsWith("." + candidate, StringComparison.Ordinal)).Select(kv => kv.Value).FirstOrDefault();
                if (found == null)
                    continue;
                type = found;
                string rest = string.Join(".", parts.Skip(take));
                member = memberPart ?? (rest.Length > 0 ? rest : null);
                return true;
            }
            return false;
        }

        static Dictionary<string, object> FindSource(string name, int context)
        {
            if (!Resolve(name, out Type type, out string member))
                throw new InvalidOperationException($"No project class matches '{name}' (engine and package code have no project source).");
            if (!ProjectCode.TryLocate(type, member, out string file, out int line))
                throw new InvalidOperationException("Found " + ProjectCode.ClassName(type) + " but not its source file.");
            string[] lines = File.ReadAllLines(file);
            context = Math.Max(0, Math.Min(200, context));
            int from = Math.Max(1, line - context / 2), to = Math.Min(lines.Length, line + context);
            return new Dictionary<string, object>
            {
                { "class", ProjectCode.ClassName(type) }, { "member", member }, { "file", file }, { "line", line },
                { "absolutePath", Path.GetFullPath(file) },
                { "code", string.Join("\n", Enumerable.Range(from, to - from + 1).Select(n => n.ToString().PadLeft(5) + "  " + lines[n - 1])) },
            };
        }
    }
}
