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

        static readonly string projectPath = LunaMachine.Normalize(Directory.GetParent(Application.dataPath).FullName);
        static readonly int pid = System.Diagnostics.Process.GetCurrentProcess().Id;

        static LunaMcp()
        {
            DevToolsMcpTools.Register(Server, Channel, EditorStatus);
            RegisterEditorTools();
            RegisterUploadTools();
            Server.CheckProject = CheckProject;
            EditorApplication.update += Update;
            EditorApplication.quitting += LunaInstance.Remove;
            AssemblyReloadEvents.beforeAssemblyReload += () => LunaInstance.Write(projectPath, pid, null, "reloading");
            CompilationPipeline.compilationStarted += _ => { compileMessages.Clear(); compileStartedUtc = DateTime.UtcNow; };
            CompilationPipeline.assemblyCompilationFinished += (_, messages) => compileMessages.AddRange(messages);
            CompilationPipeline.compilationFinished += _ => SaveCompileResult();
        }

        // Started with Unity (first editor tick after each domain reload), before any Build Develop: the MCP answers as
        // soon as the Editor has loaded. Not delayCall: a package that assigns delayCall instead of adding to it drops it.
        static bool started;

        static void StartOnce()
        {
            if (started)
                return;
            started = true;
            if (Enabled)
                LunaDevTools.Start();
            LunaInstance.Publish(Server, BrokerSource());
        }

        // luna_status: the Editor state, plus whether the develop server serves this project (asked when called).
        static Dictionary<string, object> EditorStatus()
        {
            var state = new Dictionary<string, object>(editorState);
            state["developServerProblem"] = LunaDevTools.UpstreamProblem;
            return state;
        }

        // The project an agent meant (the broker always says): refuse when this Editor has another project open.
        static string CheckProject(string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted))
                return null;
            string name = Path.GetFileName(projectPath);
            wanted = wanted.Trim();
            bool path = wanted.IndexOf('/') >= 0 || wanted.IndexOf('\\') >= 0;
            if (!path)
                return string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase) ? null : Wrong(wanted, name);
            string full = LunaMachine.Normalize(wanted);
            bool same = string.Equals(full, projectPath, StringComparison.OrdinalIgnoreCase)
                        || projectPath.StartsWith(full + "/", StringComparison.OrdinalIgnoreCase);   // The repository holding it.
            return same ? null : Wrong(wanted, projectPath);
        }

        static string Wrong(string wanted, string open) =>
            $"This Unity Editor has '{open}' open, not '{wanted}'. Call luna_instances to see the open projects.";

        static string BrokerSource([System.Runtime.CompilerServices.CallerFilePath] string self = "")
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(self)) ?? string.Empty, "Broker~", "luna-mcp.mjs");
                if (File.Exists(path))
                    return path;
            }
            catch (Exception)
            {
                // Fall back to the asset database below.
            }
            string script = AssetDatabase.FindAssets("LunaMcp t:MonoScript").Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("/Mcp/LunaMcp.cs", StringComparison.Ordinal));
            string broker = script == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(script)) ?? string.Empty, "Broker~", "luna-mcp.mjs");
            return broker != null && File.Exists(broker) ? broker : null;
        }

        public static string BrokerCommand(string agentName = "luna-devtools") =>
            $"claude mcp add -s user {agentName} -- node \"{LunaMachine.BrokerFile}\"";

        static void SaveCompileResult()
        {
            var errors = compileMessages.Where(m => m.type == CompilerMessageType.Error).ToList();
            SessionState.SetString(CompileResultKey, MiniJson.Write(new Dictionary<string, object>
            {
                { "finishedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "seconds", compileStartedUtc == default ? null : (object)Math.Round((DateTime.UtcNow - compileStartedUtc).TotalSeconds, 1) },
                { "errorCount", errors.Count },
                { "errors", errors.Take(40).Select(CompileIssue).ToList() },
                { "warningCount", compileMessages.Count(m => m.type == CompilerMessageType.Warning) },
            }));
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
                SessionState.SetString(BuildResultKey, MiniJson.Write(summary));
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
            get => EditorPrefs.GetBool(EnabledPref, true);
            set
            {
                EditorPrefs.SetBool(EnabledPref, value);
                if (value)
                    LunaDevTools.Start();
                LunaDevTools.AttachAgents();
            }
        }

        public static string Url => LunaDevTools.IsRunning && Enabled ? LunaDevTools.LocalUrl + "mcp" : null;
        // One registration per machine: the broker finds the Editor of the agent's project among the open ones.
        public static string ClaudeCommand => BrokerCommand();
        public static int ConnectedPages => Channel.Sessions().Count;

        static void Update()
        {
            StartOnce();
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
            TrackUpload();
            // Editor state for luna_status, read off the main thread by the MCP requests.
            if (EditorApplication.timeSinceStartup - stateAt > 1.0)
            {
                stateAt = EditorApplication.timeSinceStartup;
                DateTime? last = ProjectBuildState.LastPrivateBuildUtc;
                bool building = LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding();
                editorState = new Dictionary<string, object>
                {
                    { "project", Path.GetFileName(projectPath) },
                    { "projectPath", projectPath },
                    { "unity", Application.unityVersion },
                    { "playworksPlugin", LunaPluginBridge.Version },
                    { "mcpUrl", Url },
                    { "devToolsUrl", LunaDevTools.LocalUrl },
                    { "phoneUrl", LunaDevTools.LanUrl },
                    { "developServerPort", LunaDevTools.UpstreamPort },
                    { "portNote", LunaDevTools.PortNote },
                    { "building", building },
                    { "uploading", !string.IsNullOrEmpty(SessionState.GetString(UploadKey, null)) },
                    { "lastBuildDevelop", last.HasValue ? last.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : null },
                };
                string state = !Enabled ? "off" : !LunaDevTools.IsRunning ? "error"
                    : EditorApplication.isCompiling || EditorApplication.isUpdating ? "compiling"
                    : (bool)editorState["uploading"] ? "uploading" : building ? "building" : "ready";
                LunaInstance.Heartbeat(projectPath, pid, Url, state, LunaDevTools.LocalUrl, LunaDevTools.UpstreamPort, Application.unityVersion,
                                       LunaDevTools.LastError ?? LunaDevTools.PortNote);
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
                              "warnings and duration when finished), of the upload started by unity_build_upload (upload.state, same shape), " +
                              "and the Playworks plugin's Project Diagnostics of the last build " +
                              "(errors and warnings with C# file:line).",
                ReadOnly = true,
                InputSchema = McpServer.Schema(),
                Run = args => McpServer.ToolResult.Json(OnMainThread(() => new Dictionary<string, object>
                {
                    { "build", BuildState() },
                    { "upload", UploadState() },
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

        // ---- Build & upload (Playground Creative Library) ----

        const string UploadKey = "SupercentVN.LunaMcp.Upload";                  // The running upload, as JSON.
        const string UploadResultKey = "SupercentVN.LunaMcp.UploadResult";
        const double UploadStartTimeout = 10.0;     // An upload that never shows as building: read its result anyway.
        const int PlaygroundTimeoutMs = 45000;
        const int ChecksTimeoutMs = 90000;          // Stripped-code and Luna checks read every project assembly.

        static void RegisterUploadTools()
        {
            Server.Register(new McpServer.Tool
            {
                Name = "unity_upload_targets",
                Description = "Before unity_build_upload: Playground sign-in, the applications, the concepts of one application (newest " +
                              "first), the concept the Build Assistant would pick for this branch, a suggested name for a new concept, and the " +
                              "checks that would warn before uploading (changes since the last Build Develop, stripped code still called, Luna " +
                              "checks, unsaved Luna scenes). Reads Playground; changes nothing.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "applicationId", McpServer.Prop("integer", "Application to list concepts of (default: the last one used in this project)") },
                }),
                Run = args => McpServer.ToolResult.Json(UploadTargets((int)MiniJson.Num(args, "applicationId", 0))),
            });

            Server.Register(new McpServer.Tool
            {
                Name = "unity_build_upload",
                Description = "Build & upload to the Playground Creative Library, like the Build Assistant's Build & Upload button (uploads the " +
                              "playable for others to see: only when the user asked for an upload, with the application and concept they " +
                              "confirmed). Returns once started; poll unity_build_report (upload.state running / finished, with errors). " +
                              "Refused while a build runs, while Unity compiles, with unsaved Luna scenes, when not signed in, and when a " +
                              "pre-upload check warns (changes since the last Build Develop, stripped code still called, Luna checks) unless " +
                              "force is true. Uploads the project as it is on disk, including uncommitted changes.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "applicationId", McpServer.Prop("integer", "Playground application id (unity_upload_targets)") },
                    { "concept", McpServer.Prop("string", "Existing concept name (a new version of it), or the name of a new concept with createConcept") },
                    { "createConcept", McpServer.Prop("boolean", "Create the concept when no concept has this name (otherwise refused)", false) },
                    { "force", McpServer.Prop("boolean", "Upload even when a pre-upload check warns (only after the user accepted the warnings)", false) },
                }, "applicationId", "concept"),
                Run = args => Upload((int)MiniJson.Num(args, "applicationId", 0), MiniJson.Str(args, "concept"),
                                     MiniJson.Bool(args, "createConcept", false), MiniJson.Bool(args, "force", false)),
            });
        }

        static Dictionary<string, object> UploadTargets(int applicationId)
        {
            var data = OnMainThread(() =>
            {
                if (!LunaPluginBridge.IsAvailable)
                    throw new InvalidOperationException("The Unity Playworks Plugin cannot be used: " + LunaPluginBridge.BlockedReason);
                return new Dictionary<string, object> { { "signedIn", LunaPluginBridge.IsSignedIn } };
            });
            if (!(bool)data["signedIn"])
            {
                data["hint"] = "Sign in to Playground once in the Unity Playworks Plugin (Tools/Unity Playworks Plugin); agents cannot sign in.";
                return data;
            }
            LunaPluginBridge.App[] apps = LoadApps();
            int appId = applicationId != 0 ? applicationId : OnMainThread(DefaultApplicationId);
            LunaPluginBridge.App app = apps.FirstOrDefault(a => a.Id == appId);
            if (app.Id == 0 && apps.Length > 0)
                app = apps[0];
            data["applications"] = apps.Select(a => (object)new Dictionary<string, object> { { "id", a.Id }, { "name", a.Name } }).ToList();
            if (app.Id == 0)
                return data;
            LunaPluginBridge.Concept[] concepts = LoadConcepts(app.Id);
            return OnMainThread(() =>
            {
                string branch = ProjectBuildState.CurrentBranch();
                var names = concepts.Select(c => c.Name).ToList();
                data["application"] = new Dictionary<string, object> { { "id", app.Id }, { "name", app.Name } };
                data["concepts"] = concepts.Take(40).Select(c => (object)new Dictionary<string, object> { { "id", c.Id }, { "name", c.Name } }).ToList();
                data["conceptCount"] = concepts.Length;
                data["branch"] = branch;
                data["defaultConcept"] = ConceptNaming.DefaultConcept(names, branch);
                data["suggestedNewConcept"] = ConceptNaming.Suggest(names, app.Name, branch, DateTime.Now, string.Empty);
                DateTime? last = ProjectBuildState.LastPrivateBuildUtc;
                data["lastBuildDevelop"] = last.HasValue ? last.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : null;
                data["warnings"] = LunaUpload.Warnings(NewChecks());
                data["unsavedLunaScenes"] = LunaUpload.UnsavedLunaScenes();
                return data;
            }, ChecksTimeoutMs);
        }

        static McpServer.ToolResult Upload(int applicationId, string concept, bool createConcept, bool force)
        {
            if (string.IsNullOrWhiteSpace(concept))
                return McpServer.ToolResult.Text("concept is required (see unity_upload_targets).", true);
            string refused = OnMainThread(() =>
            {
                if (!LunaPluginBridge.IsAvailable)
                    return "The Unity Playworks Plugin cannot be used: " + LunaPluginBridge.BlockedReason;
                if (!LunaPluginBridge.IsSignedIn)
                    return "Not signed in to Playground: the user signs in once in the Unity Playworks Plugin (Tools/Unity Playworks Plugin).";
                if (LunaBuildAssistantWindow.AgentBuildRunning || SafeIsBuilding() || !string.IsNullOrEmpty(SessionState.GetString(UploadKey, null)))
                    return "A build or an upload is already running: poll unity_build_report.";
                return null;
            });
            if (refused != null)
                return McpServer.ToolResult.Text(refused, true);

            LunaPluginBridge.App app = LoadApps().FirstOrDefault(a => a.Id == applicationId);
            if (app.Id == 0)
                return McpServer.ToolResult.Text($"No Playground application with id {applicationId} (see unity_upload_targets).", true);
            LunaPluginBridge.Concept[] concepts = LoadConcepts(app.Id);
            LunaPluginBridge.Concept existing = concepts.FirstOrDefault(c => string.Equals(c.Name, concept.Trim(), StringComparison.OrdinalIgnoreCase));
            bool isNew = existing.Id == 0;
            string name = isNew ? ConceptNaming.Sanitize(concept) : existing.Name;
            if (isNew && !createConcept)
                return McpServer.ToolResult.Text($"{app.Name} has no concept '{concept}'. Pass createConcept=true to create it, or use one of: " +
                                                 string.Join(", ", concepts.Take(15).Select(c => c.Name)), true);
            if (name.Length == 0)
                return McpServer.ToolResult.Text("The concept name is empty once cleaned.", true);

            var started = OnMainThread(() =>
            {
                AssetDatabase.Refresh();
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    return Refusal("Unity is importing / compiling changed scripts. Call again in a few seconds.");
                if (EditorUtility.scriptCompilationFailed)
                    return Refusal("The project has C# compile errors (unity_compile).");
                List<string> unsaved = LunaUpload.UnsavedLunaScenes();
                if (unsaved.Count > 0)
                    return Refusal("Unsaved Luna scene(s): " + string.Join(", ", unsaved) + ". The user saves them in Unity first (agents never save scenes).");
                List<string> warnings = LunaUpload.Warnings(NewChecks());
                if (warnings.Count > 0 && !force)
                {
                    var r = Refusal("Pre-upload checks warn. Tell the user; upload with force=true only if they accept.");
                    r["warnings"] = warnings;
                    return r;
                }
                var upload = new Dictionary<string, object>
                {
                    { "application", app.Name }, { "applicationId", app.Id }, { "concept", name }, { "newConcept", isNew },
                    { "startedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }, { "startedAtEditor", EditorApplication.timeSinceStartup },
                    { "reportBefore", BuildDiagnostics.CurrentReportId() }, { "sawBuilding", false },
                };
                SessionState.SetString(UploadKey, MiniJson.Write(upload));
                SessionState.EraseString(UploadResultKey);
                string cancelled;
                try
                {
                    cancelled = LunaUpload.Start(app.Id, name, isNew ? -1 : existing.Id);
                }
                catch (Exception)
                {
                    SessionState.EraseString(UploadKey);
                    throw;
                }
                if (cancelled != null)
                {
                    SessionState.EraseString(UploadKey);
                    return Refusal(cancelled);
                }
                Debug.Log($"[LunaBuildAssistant] Build & upload started by an agent: {app.Name} / {name}{(isNew ? " (new concept)" : string.Empty)}.");
                var state = UploadState();
                if (warnings.Count > 0)
                    state["uploadedDespite"] = warnings;
                if (isNew && !ConceptNaming.TryParse(name, out _))
                    state["note"] = "The concept name is not playable<number>_<game>_<yymmdd>[_suffix].";
                state["hint"] = "Call unity_build_report every 15-30 s until upload.state is finished. The plugin opens the Creative " +
                                "Library page in the browser when the upload is done.";
                return state;
            }, ChecksTimeoutMs);
            bool wasRefused = started.ContainsKey("refused");
            return wasRefused ? McpServer.ToolResult.Text(MiniJson.Write(started), true) : McpServer.ToolResult.Json(started);
        }

        static Dictionary<string, object> Refusal(string why) => new Dictionary<string, object> { { "refused", why } };

        static UploadChecksPanel NewChecks() => new UploadChecksPanel { OnError = e => Debug.LogException(e) };

        static int DefaultApplicationId()
        {
            int last = ProjectBuildState.LastApplicationId;
            return last != 0 ? last : LunaPluginBridge.SavedApplicationId;
        }

        static LunaPluginBridge.App[] LoadApps()
        {
            AwaitMainThreadTask(LunaPluginBridge.LoadApps, "Playground applications");
            return OnMainThread(() => LunaPluginBridge.Apps);
        }

        static LunaPluginBridge.Concept[] LoadConcepts(int appId)
        {
            AwaitMainThreadTask(() => LunaPluginBridge.LoadConcepts(appId), "Playground concepts");
            return OnMainThread(() => LunaPluginBridge.Concepts.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        // Starts a plugin Task on the main thread (its continuations run there) and waits for it from the request thread.
        static void AwaitMainThreadTask(Func<System.Threading.Tasks.Task> start, string what)
        {
            System.Threading.Tasks.Task task = OnMainThread(start);
            DateTime until = DateTime.UtcNow.AddMilliseconds(PlaygroundTimeoutMs);
            while (!task.IsCompleted && DateTime.UtcNow < until)
                Thread.Sleep(100);
            if (!task.IsCompleted)
                throw new TimeoutException("Playground did not answer in time (" + what + "). Retry in a moment.");
            if (task.IsFaulted)
                throw new InvalidOperationException("Loading " + what + " failed: " + task.Exception?.GetBaseException().Message);
        }

        // Main thread: notices the end of the plugin's Build & upload and keeps its result across a domain reload.
        static void TrackUpload()
        {
            string json = SessionState.GetString(UploadKey, null);
            if (string.IsNullOrEmpty(json))
                return;
            var upload = MiniJson.Obj(MiniJson.Parse(json)) ?? new Dictionary<string, object>();
            if (SafeIsBuilding())
            {
                if (!MiniJson.Bool(upload, "sawBuilding", false))
                {
                    upload["sawBuilding"] = true;
                    SessionState.SetString(UploadKey, MiniJson.Write(upload));
                }
                return;
            }
            double startedAt = MiniJson.Num(upload, "startedAtEditor", 0);
            if (!MiniJson.Bool(upload, "sawBuilding", false) && EditorApplication.timeSinceStartup - startedAt < UploadStartTimeout)
                return;
            BuildReport report = BuildDiagnostics.Collect("Build & upload", true, false, MiniJson.Str(upload, "reportBefore"));
            var summary = BuildSummary(report, report.Failed ? null : "Build & upload finished at " + DateTime.Now.ToString("HH:mm") + ".");
            foreach (string key in new[] { "application", "applicationId", "concept", "newConcept", "startedAt" })
                summary[key] = upload.TryGetValue(key, out object v) ? v : null;
            summary["finishedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            summary["seconds"] = Math.Round(EditorApplication.timeSinceStartup - startedAt, 1);
            if (!MiniJson.Bool(upload, "sawBuilding", false))
                summary["note"] = "The plugin never showed as building: check its window or the Console.";
            SessionState.SetString(UploadResultKey, MiniJson.Write(summary));
            SessionState.EraseString(UploadKey);
        }

        static Dictionary<string, object> UploadState()
        {
            string running = SessionState.GetString(UploadKey, null), result = SessionState.GetString(UploadResultKey, null);
            if (!string.IsNullOrEmpty(running))
            {
                var upload = MiniJson.Obj(MiniJson.Parse(running)) ?? new Dictionary<string, object>();
                var state = new Dictionary<string, object> { { "state", "running" } };
                foreach (string key in new[] { "application", "applicationId", "concept", "newConcept", "startedAt" })
                    state[key] = upload.TryGetValue(key, out object v) ? v : null;
                state["seconds"] = Math.Round(EditorApplication.timeSinceStartup - MiniJson.Num(upload, "startedAtEditor", 0), 1);
                try
                {
                    if (LunaPluginBridge.IsAvailable && LunaPluginBridge.TryGetProgress(out string label, out float fraction))
                    {
                        state["step"] = label;
                        state["progress"] = Math.Round(fraction, 2);
                    }
                }
                catch (Exception)
                {
                    // Progress is only informative.
                }
                return state;
            }
            if (!string.IsNullOrEmpty(result))
            {
                var finished = MiniJson.Obj(MiniJson.Parse(result)) ?? new Dictionary<string, object>();
                finished["state"] = "finished";
                return finished;
            }
            return new Dictionary<string, object> { { "state", "none" } };
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
