using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
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

        // Kept in SessionState: a script change makes Unity reload this code in the middle of a build or a compile, and the
        // agent asks for the result afterwards.
        const string BuildStartedKey = "SupercentVN.LunaMcp.BuildStarted";
        const string BuildResultKey = "SupercentVN.LunaMcp.BuildResult";
        const string CompileResultKey = "SupercentVN.LunaMcp.CompileResult";
        static bool buildWasRunning;
        static readonly List<CompilerMessage> compileMessages = new List<CompilerMessage>();
        static DateTime compileStartedUtc;

        static LunaMcp()
        {
            DevToolsMcpTools.Register(Server, Channel, () => editorState);
            RegisterEditorTools();
            EditorApplication.update += Update;
            CompilationPipeline.compilationStarted += _ => { compileMessages.Clear(); compileStartedUtc = DateTime.UtcNow; };
            CompilationPipeline.assemblyCompilationFinished += (_, messages) => compileMessages.AddRange(messages);
            CompilationPipeline.compilationFinished += _ => SaveCompileResult();
            if (Enabled)
                EditorApplication.delayCall += () => LunaDevTools.Start();
        }

        static void SaveCompileResult()
        {
            var errors = compileMessages.Where(m => m.type == CompilerMessageType.Error).ToList();
            StoreJson(CompileResultKey, new Dictionary<string, object>
            {
                { "finishedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "seconds", compileStartedUtc == default ? null : (object)Math.Round((DateTime.UtcNow - compileStartedUtc).TotalSeconds, 1) },
                { "errorCount", errors.Count },
                { "errors", errors.Take(40).Select(CompileIssue).ToList() },
                { "warningCount", compileMessages.Count(m => m.type == CompilerMessageType.Warning) },
            });
        }

        // SessionState does not keep non-ASCII text intact ("−" came back as "âˆ’"): store JSON with \u escapes. Non-ASCII
        // characters only occur inside JSON strings, where the escape means the same.
        static void StoreJson(string key, Dictionary<string, object> value)
        {
            string json = MiniJson.Write(value);
            var sb = new System.Text.StringBuilder(json.Length);
            foreach (char c in json)
                if (c > 127)
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                else
                    sb.Append(c);
            SessionState.SetString(key, sb.ToString());
        }

        static object CompileIssue(CompilerMessage m) => new Dictionary<string, object>
        {
            { "file", m.file }, { "line", m.line > 0 ? (object)m.line : null }, { "message", m.message },
        };

        // Main thread: notices the end of an agent build and keeps its result across a domain reload.
        static void TrackBuild()
        {
            bool running = LunaBuildAssistantWindow.AgentBuildRunning;
            if (buildWasRunning && !running)
            {
                var summary = BuildSummary(LunaBuildAssistantWindow.AgentBuildReport, LunaBuildAssistantWindow.AgentBuildStatus);
                summary["seconds"] = Math.Round(LunaBuildAssistantWindow.AgentBuildSeconds, 1);
                summary["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                StoreJson(BuildResultKey, summary);
                SessionState.EraseString(BuildStartedKey);
            }
            buildWasRunning = running;
        }

        static Dictionary<string, object> BuildState()
        {
            string started = SessionState.GetString(BuildStartedKey, null), result = SessionState.GetString(BuildResultKey, null);
            if (LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding())
            {
                var running = new Dictionary<string, object> { { "state", "running" }, { "startedAt", started } };
                if (DateTime.TryParse(started, out DateTime at))
                    running["seconds"] = Math.Round((DateTime.Now - at).TotalSeconds, 1);
                return running;
            }
            if (!string.IsNullOrEmpty(started))
                return new Dictionary<string, object>
                {
                    { "state", "lost" }, { "startedAt", started },
                    { "note", "Unity reloaded its scripts during the build, so its result was lost: see diagnostics, or build again." },
                };
            if (!string.IsNullOrEmpty(result))
            {
                var finished = MiniJson.Obj(MiniJson.Parse(result)) ?? new Dictionary<string, object>();
                finished["state"] = "finished";
                return finished;
            }
            return new Dictionary<string, object> { { "state", "none" } };
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
            TrackBuild();
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
                Description = "Starts Clear Cache + Build Develop in the Unity Editor (Luna Build Assistant) and returns right away: a build takes " +
                              "minutes and Unity may reload its scripts during it, which would cut a long request. Poll unity_build_report " +
                              "(build.state running / finished, with errors such as compile errors, unsupported C# / API, Playground sign-in " +
                              "with file:line, Playworks warnings, duration). With openPage the playable opens through the Dev Tools and connects " +
                              "for the luna_* tools. Changed scripts are imported first; if Unity is compiling it says so: call again after. " +
                              "Unity may ask the user to save a modified scene.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "openPage", McpServer.Prop("boolean", "Open the built page through the Dev Tools", true) },
                    { "waitSeconds", McpServer.Prop("number", "Optionally wait up to this long (max 60) for the build before returning", 0) },
                }),
                Run = args =>
                {
                    bool openPage = MiniJson.Bool(args, "openPage", true);
                    string refused = OnMainThread(() =>
                    {
                        string why = LunaBuildAssistantWindow.StartAgentBuild(openPage);
                        if (why == null)
                        {
                            SessionState.SetString(BuildStartedKey, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            SessionState.EraseString(BuildResultKey);
                            buildWasRunning = true;
                        }
                        return why;
                    });
                    if (refused != null)
                        return McpServer.ToolResult.Text(refused, true);
                    DateTime until = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(60, MiniJson.Num(args, "waitSeconds", 0))));
                    while (LunaBuildAssistantWindow.AgentBuildRunning && DateTime.UtcNow < until)
                        Thread.Sleep(500);
                    var state = OnMainThread(() => { TrackBuild(); return BuildState(); });
                    if ((string)state["state"] == "running")
                        state["hint"] = "Call unity_build_report every 15-30 s until build.state is finished.";
                    return McpServer.ToolResult.Json(state);
                },
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_build_report",
                Description = "State of the build started by unity_build_develop (build.state: running / finished / lost / none, with errors, " +
                              "warnings and duration when finished) and the Playworks plugin's Project Diagnostics of the last build " +
                              "(errors and warnings with C# file:line).",
                ReadOnly = true,
                InputSchema = McpServer.Schema(),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => new Dictionary<string, object>
                {
                    { "build", BuildState() },
                    { "diagnostics", BuildSummary(BuildDiagnostics.Collect("Last build", false, false, null), null) },
                })),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_compile",
                Description = "Imports changed scripts and reports C# compile errors (file:line), like switching to Unity does. If Unity starts " +
                              "compiling, the answer is state=compiling: call again in 10-30 s (the MCP server restarts while Unity reloads " +
                              "scripts; retry if the connection drops). Use after editing C#, before unity_build_develop.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(),
                Run = args =>
                {
                    bool compiling = OnMainThread(() =>
                    {
                        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                            return true;
                        AssetDatabase.Refresh();
                        return EditorApplication.isCompiling || EditorApplication.isUpdating;
                    });
                    if (!compiling)
                    {
                        Thread.Sleep(1500);         // Compilation starts on a later editor tick.
                        compiling = OnMainThread(() => EditorApplication.isCompiling || EditorApplication.isUpdating);
                    }
                    return McpServer.ToolResult.Json(OnMainThread(() =>
                    {
                        if (compiling)
                            return new Dictionary<string, object>
                            {
                                { "state", "compiling" },
                                { "hint", "Unity is compiling the changed scripts. Call unity_compile again in 10-30 s." },
                            };
                        string last = SessionState.GetString(CompileResultKey, null);
                        var data = new Dictionary<string, object>
                        {
                            { "state", "idle" },
                            { "hasCompileErrors", EditorUtility.scriptCompilationFailed },
                            { "lastCompile", string.IsNullOrEmpty(last) ? null : MiniJson.Parse(last) },
                        };
                        if (EditorUtility.scriptCompilationFailed && string.IsNullOrEmpty(last))
                            data["note"] = "Compile errors exist but were compiled before this Editor session: see Unity's Console.";
                        return data;
                    }));
                },
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
