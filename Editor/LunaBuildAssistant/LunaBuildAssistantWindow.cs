using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // One window for the team's Luna cloud flow, laid out like the Playworks plugin (sidebar + pages):
    //   Build Develop: scene picker; [regenerate _NoPrefab scenes] -> Clear Build Cache -> Build Develop -> open the local
    //                  server in a browser -> the plugin's Size breakdown for the chosen network.
    //   Build & upload: pick app + concept -> stripped-code and Luna checks -> [regenerate _NoPrefab scenes]
    //                  -> clear SVC_Luna scene/runtime variants -> plugin's Build & upload.
    //   Checks / Build size: the details behind the pre-upload checks and the size report.
    //   Assets: the plugin's Assets section (include/exclude lists, default and per-asset export settings).
    public class LunaBuildAssistantWindow : EditorWindow
    {
        const string OpenBrowserPref = "SupercentVN.LunaBuildAssistant.OpenBrowser";
        const string PlayworksMenu = "Tools/Unity Playworks Plugin";
        const string NewConceptLabel = "< New Concept >";
        const int MaxListedChanges = 10;
        const float SidebarWidth = 190f;
        const float ContentMargin = 12f;                    // Space on each side of the page content.
        const double ChangesInterval = 3.0;                 // Seconds between scans for files changed since the last Build Develop.
        const double UploadStartTimeout = 10.0;             // Build & upload that never shows as building: read its result anyway.

        enum Page { Private, Upload, Checks, Size, Assets }

        LunaPluginBridge.App[] apps = new LunaPluginBridge.App[0];
        LunaPluginBridge.Concept[] concepts = new LunaPluginBridge.Concept[0];
        int appIndex;
        int conceptIndex;                   // 0 = new concept, otherwise concepts[conceptIndex - 1].
        string conceptName = string.Empty;
        string suffix = string.Empty;
        bool conceptNameEdited;
        string branch;
        bool appsRequested, loadingApps, loadingConcepts, building, showProgress;
        int conceptsRequest;
        string status;
        Vector2 scroll;
        readonly UploadChecksPanel checks = new UploadChecksPanel();
        readonly BuildSizeView sizeView = new BuildSizeView();
        readonly AssetSettingsView assetsView = new AssetSettingsView();
        readonly ScenePickerPanel scenePicker = new ScenePickerPanel();
        readonly BuildReportPanel reportPanel = new BuildReportPanel();
        [SerializeField] Page page;
        [SerializeField] BuildReport report;            // Errors/warnings of the last build, kept across domain reloads.
        List<string> changes;
        double changesCheckedAt;
        string uploadReportBefore;                      // Diagnostics report id when Build & upload started.
        double uploadStartedAt = -1;                    // -1: no Build & upload to wait for.
        bool uploadSawBuilding;

        [MenuItem("Tools/Luna/Build Assistant")]
        static void Open() => Open(null);

        [MenuItem("Tools/Luna/Build Size Report")]
        static void OpenSize() => Open(Page.Size);

        [MenuItem("Tools/Luna/Asset Settings")]
        static void OpenAssets() => Open(Page.Assets);

        static void Open(Page? target)
        {
            var window = GetWindow<LunaBuildAssistantWindow>("Luna Build Assistant");
            window.minSize = new Vector2(900, 560);
            if (target.HasValue)
                window.page = target.Value;
        }

        void OnEnable()
        {
            branch = ProjectBuildState.CurrentBranch();
            appsRequested = false;
            checks.OnError = Fail;
            sizeView.Repaint = Repaint;
            scenePicker.OnError = Fail;
            scenePicker.Repaint = Repaint;
            reportPanel.OpenPlayworks = OpenPlayworks;
            checks.Invalidate();
        }

        void OnFocus() => assetsView.Reload();

        void OnDisable()
        {
            sizeView.Close();
        }

        void OnInspectorUpdate()
        {
            // Load once signed in; kept out of OnGUI so the layout never changes mid-event.
            if (!appsRequested && LunaPluginBridge.IsAvailable && SafeIsSignedIn())
            {
                appsRequested = true;
                RefreshApps();
            }
            bool busy = building || SafeIsBuilding();
            if (uploadStartedAt >= 0)
                WatchUpload(busy);
            if (report != null && report.NeedsSignIn && LunaPluginBridge.IsAvailable && SafeIsSignedIn())
            {
                report = null;              // Signed in meanwhile: the sign-in error no longer applies.
                Repaint();
            }
            if (sizeView.Update())
                Repaint();
            if (!busy && LunaPluginBridge.IsAvailable && checks.Refresh())
                Repaint();
            if (!busy && EditorApplication.timeSinceStartup - changesCheckedAt > ChangesInterval)
            {
                changesCheckedAt = EditorApplication.timeSinceStartup;
                changes = ProjectBuildState.ChangesSincePrivateBuild();
                if (LunaPluginBridge.IsAvailable)
                    scenePicker.Refresh();
                Repaint();
            }
            if (busy || loadingApps || loadingConcepts)
                Repaint();
        }

        void OnGUI()
        {
            // Decided on Layout only, so Layout and Repaint always see the same controls.
            if (Event.current.type == EventType.Layout)
                showProgress = LunaPluginBridge.IsAvailable && (building || SafeIsBuilding());

            // position includes the window's tab strip; fill exactly the area below it.
            float height = position.height - (GUIUtility.GUIToScreenPoint(Vector2.zero).y - position.y);
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(height)))
            {
                DrawSidebar();
                GUILayout.Space(ContentMargin);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(8);
                    if (DrawPluginState())
                    {
                        switch (page)
                        {
                            case Page.Checks:
                                checks.Draw();
                                break;
                            case Page.Size:
                                sizeView.AvailableWidth = position.width - SidebarWidth - 2 * ContentMargin;
                                sizeView.Draw();
                                break;
                            case Page.Assets:
                                using (new EditorGUI.DisabledScope(showProgress))
                                    assetsView.Draw();
                                break;
                            default:
                                scroll = EditorGUILayout.BeginScrollView(scroll);
                                using (new EditorGUI.DisabledScope(showProgress))
                                {
                                    if (page == Page.Private)
                                        DrawPrivateBuild();
                                    else
                                        DrawUpload();
                                }
                                EditorGUILayout.EndScrollView();
                                break;
                        }
                    }
                    DrawStatus();
                    GUILayout.Space(6);
                }
                GUILayout.Space(ContentMargin);
            }
        }

        // Plugin-style navigation: title, pages with warning badges, build progress at the bottom.
        void DrawSidebar()
        {
            using (var side = new EditorGUILayout.VerticalScope(GUILayout.Width(SidebarWidth), GUILayout.ExpandHeight(true)))
            {
                if (Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(side.rect, AssistantUI.Sidebar);
                GUILayout.Space(18);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(18);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        GUILayout.Label("Build Assistant", AssistantUI.Logo);
                        string version = "Playworks " + (LunaPluginBridge.Version ?? "?");
                        bool tested = LunaPluginBridge.IsAvailable && LunaPluginBridge.IsTestedVersion;
                        GUILayout.Label(new GUIContent(version + (tested ? string.Empty : "  (untested)"),
                            "Tested with " + string.Join(", ", LunaPluginBridge.TestedVersions)), AssistantUI.Caption);
                    }
                }
                GUILayout.Space(22);

                Nav(Page.Assets, "Assets", 0);
                Nav(Page.Private, "Build Develop", 0);
                Nav(Page.Upload, "Build & upload", 0);
                Nav(Page.Checks, "Checks", checks.IssueCount);
                Nav(Page.Size, "Build size", 0);
                GUILayout.FlexibleSpace();

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        if (EditorGUILayout.LinkButton(new GUIContent("Open Unity Playworks Plugin", "The original plugin window: Playground sign-in and every advanced setting")))
                            OpenPlayworks();
                        GUILayout.Space(6);
                        if (showProgress || sizeView.Estimating)
                            DrawProgress();
                        else
                            using (new EditorGUI.DisabledScope(!LunaPluginBridge.IsAvailable))
                                if (AssistantUI.PrimaryButton("Build Develop", 26))
                                    BuildPrivate(OpenBrowser);
                    }
                    GUILayout.Space(8);
                }
                GUILayout.Space(8);
            }
        }

        void Nav(Page target, string text, int badge)
        {
            if (AssistantUI.NavButton(text, page == target, badge))
                GoTo(target);
        }

        void GoTo(Page target)
        {
            if (page == Page.Assets && target != Page.Assets && !assetsView.ConfirmLeave())
                return;
            if (target == Page.Assets)
                assetsView.Reload();
            page = target;
            GUI.FocusControl(null);
        }

        // Bottom line: a failed build's summary (with a way to its details) or the last status message.
        void DrawStatus()
        {
            if (report != null && report.Failed)
            {
                AssistantUI.Separator();
                Page reportPage = report.Upload ? Page.Upload : Page.Private;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(AssistantUI.Icon("console.erroricon.sml"), GUILayout.Width(18), GUILayout.Height(18));
                    GUILayout.Label(report.Summary, EditorStyles.miniBoldLabel);
                    if (page != reportPage && EditorGUILayout.LinkButton("View errors"))
                        GoTo(reportPage);
                    GUILayout.FlexibleSpace();
                }
            }
            else if (!string.IsNullOrEmpty(status))
            {
                AssistantUI.Separator();
                GUILayout.Label(new GUIContent(status, status), AssistantUI.Caption);
            }
        }

        // The last build's errors and warnings, on the page of the build that produced them.
        void DrawReport(bool upload)
        {
            if (report == null || report.Upload != upload)
                return;
            if (!reportPanel.Draw(report))
                report = null;
        }

        // Blocking errors replace the page; false when the tool cannot be used.
        bool DrawPluginState()
        {
            if (LunaPluginBridge.IsAvailable)
                return true;
            AssistantUI.Header("Plugin not supported");
            EditorGUILayout.HelpBox(LunaPluginBridge.BlockedReason, MessageType.Error);
            if (GUILayout.Button("Retry", GUILayout.Width(80)))
            {
                LunaPluginBridge.Reset();
                appsRequested = false;
            }
            return false;
        }

        void DrawProgress()
        {
            string label = null;
            float fraction = 0f;
            try
            {
                LunaPluginBridge.TryGetProgress(out label, out fraction);
            }
            catch (Exception e)
            {
                Fail(e);
            }
            GUILayout.Label(string.IsNullOrEmpty(label) ? "Building…" : label, AssistantUI.Caption);
            Rect bar = GUILayoutUtility.GetRect(0, 6, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(bar, AssistantUI.Track);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, bar.width * fraction, bar.height), AssistantUI.Accent);
        }

        static bool OpenBrowser => EditorPrefs.GetBool(OpenBrowserPref, true);

        static void OpenPlayworks() => EditorApplication.ExecuteMenuItem(PlayworksMenu);

        void DrawPrivateBuild()
        {
            AssistantUI.Header("Build Develop",
                "Clear Build Cache → Build Develop → open the local server → size breakdown.\n\n" +
                "Play every path here: Luna strips code that never runs in Build Develop from the cloud build.");
            DrawReport(false);

            using (new EditorGUILayout.HorizontalScope())
            {
                DateTime? last = ProjectBuildState.LastPrivateBuildUtc;
                AssistantUI.Tile("Last build", last.HasValue ? AssistantUI.Ago(last.Value.ToLocalTime()) : "never", null, null,
                                 last.HasValue ? last.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null);
                BuildSizeReport.Snapshot size = BuildSizeReport.Latest;
                long delta = size == null || BuildSizeReport.Previous == null ? 0 : size.totalBytes - BuildSizeReport.Previous.totalBytes;
                AssistantUI.Tile("Size · " + NetworkName(BuildSizeReport.Network), size == null ? "—" : BuildSizeReport.FormatBytes(size.totalBytes),
                                 delta == 0 ? null : BuildSizeReport.FormatDelta(delta), AssistantUI.DeltaColor(delta), "Size breakdown of the playable; details in Build size");
                AssistantUI.Tile("Changed since", changes == null ? "…" : changes.Count == 0 ? "none" : changes.Count + " file(s)", null, null, ChangesTooltip());
            }
            EditorGUILayout.Space(10);

            scenePicker.Draw();
            EditorGUILayout.Space(10);

            AssistantUI.SubHeader("Options");
            bool openBrowser = EditorGUILayout.ToggleLeft(new GUIContent("Open the browser when the build finishes"), OpenBrowser);
            if (openBrowser != OpenBrowser)
                EditorPrefs.SetBool(OpenBrowserPref, openBrowser);
            bool noPrefab = EditorGUILayout.ToggleLeft(new GUIContent($"Build unpacked-prefab scenes (<Scene>{NoPrefabScenes.Suffix})",
                "Before every build, the Luna scenes are copied to <Scene>_NoPrefab with every prefab unpacked, and the copies are built.\n" +
                "Smaller playable; keep editing the source scene."), NoPrefabScenes.Enabled);
            if (noPrefab != NoPrefabScenes.Enabled)
                NoPrefabScenes.Enabled = noPrefab;
            if (noPrefab && scenePicker.Selected != null)
                foreach (string scene in scenePicker.Selected)
                    GUILayout.Label("     → " + System.IO.Path.GetFileName(NoPrefabScenes.CopyPath(scene)), AssistantUI.Caption);
            bool devTools = EditorGUILayout.ToggleLeft(new GUIContent("Open with Dev Tools",
                "Adds a debug panel to the Build Develop page (` key): frame time, draw calls step by step, frame capture, " +
                "most called methods, JS heap, pause / step / speed, restart.\n\n" +
                "A local proxy adds it while the page loads: nothing is written to the build, so the uploaded playable, " +
                "luna.json and the size estimate are unchanged."), LunaDevTools.Enabled);
            if (devTools != LunaDevTools.Enabled)
                LunaDevTools.Enabled = devTools;
            DrawDevTools();
            bool mcp = EditorGUILayout.ToggleLeft(new GUIContent("MCP server for AI agents",
                "Lets an agent (Claude Code, ...) debug the running Build Develop page and drive builds: errors with C# stacks, " +
                "frame stats, hot paths, frame capture, scene reading, field watches, Build Develop, Luna checks, source lookup.\n\n" +
                "Served by the Dev Tools proxy to this machine only, and started again with Unity."), LunaMcp.Enabled);
            if (mcp != LunaMcp.Enabled)
                LunaMcp.Enabled = mcp;
            DrawMcp();

            EditorGUILayout.Space(14);
            if (AssistantUI.PrimaryButton("Clear Cache & Build Develop"))
                BuildPrivate(openBrowser);
        }

        // The last Build Develop in the browser: through the Dev Tools proxy, or the plugin's plain page.
        static void OpenPlayable()
        {
            if (LunaDevTools.Enabled)
                LunaDevTools.Open();
            else
                LunaPluginBridge.OpenInBrowser();
        }

        // Dev Tools state under the options: addresses while the proxy runs, or a way to open the last build.
        static void DrawDevTools()
        {
            if (!LunaDevTools.Enabled && !LunaDevTools.IsRunning)
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(18);
                if (LunaDevTools.IsRunning)
                {
                    string lan = LunaDevTools.LanUrl;
                    GUILayout.Label(new GUIContent("Dev Tools: " + LunaDevTools.LocalUrl + (lan != null ? "   phone: " + lan : string.Empty),
                        "Phones on the same network open the LAN address (allow Unity through the firewall if asked)."), AssistantUI.Caption);
                    if (EditorGUILayout.LinkButton("Open"))
                        LunaDevTools.Open();
                    if (lan != null && EditorGUILayout.LinkButton("Copy phone URL"))
                        EditorGUIUtility.systemCopyBuffer = lan;
                    if (EditorGUILayout.LinkButton("Stop"))
                        LunaDevTools.Stop();
                }
                else
                {
                    using (new EditorGUI.DisabledScope(!ProjectBuildState.LastPrivateBuildUtc.HasValue))
                        if (EditorGUILayout.LinkButton("Open the last build with Dev Tools"))
                            LunaDevTools.Open();
                    if (LunaDevTools.LastError != null)
                        GUILayout.Label(new GUIContent(LunaDevTools.LastError, LunaDevTools.LastError), AssistantUI.Caption);
                }
                GUILayout.FlexibleSpace();
            }
        }

        // MCP address and the command that registers it in Claude Code.
        static void DrawMcp()
        {
            if (!LunaMcp.Enabled)
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(18);
                string url = LunaMcp.Url;
                if (url == null)
                    GUILayout.Label(new GUIContent("MCP: not running" + (LunaDevTools.LastError != null ? " (" + LunaDevTools.LastError + ")" : string.Empty)), AssistantUI.Caption);
                else
                {
                    int pages = LunaMcp.ConnectedPages;
                    GUILayout.Label(new GUIContent($"MCP: {url}   {pages} page(s) connected",
                        "This project keeps this address on this machine, also with other Luna projects open.\n" +
                        "Pages connect when the Build Develop page is opened through the Dev Tools."), AssistantUI.Caption);
                    if (EditorGUILayout.LinkButton(new GUIContent("Copy Claude Code command",
                            "Run once per machine. Registers the MCP broker, which starts with the agent (also before Unity is open) " +
                            "and sends each call to the Editor of the agent's project:\n" + LunaMcp.ClaudeCommand)))
                        EditorGUIUtility.systemCopyBuffer = LunaMcp.ClaudeCommand;
                }
                GUILayout.FlexibleSpace();
            }
            if (LunaDevTools.PortNote != null)
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(18);
                    GUILayout.Label(new GUIContent(LunaDevTools.PortNote, LunaDevTools.PortNote), AssistantUI.Caption);
                }
        }

        string ChangesTooltip() =>
            changes == null || changes.Count == 0 ? "Scenes and files changed after the last Build Develop"
                : string.Join("\n", changes.Take(MaxListedChanges)) + (changes.Count > MaxListedChanges ? "\n…" : string.Empty);

        void DrawUpload()
        {
            AssistantUI.Header("Build & upload", "Builds with Luna's cloud settings and uploads the playable to the Playground Creative Library.");
            DrawReport(true);
            if (!SafeIsSignedIn())
            {
                EditorGUILayout.HelpBox("Sign in to Playground in the Unity Playworks Plugin first.", MessageType.Info);
                if (GUILayout.Button("Open Unity Playworks Plugin", GUILayout.Width(200)))
                    OpenPlayworks();
                return;
            }

            AssistantUI.SubHeader("Target");
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(loadingApps || apps.Length == 0))
                {
                    int newApp = EditorGUILayout.Popup("Application", appIndex, apps.Select(a => a.Name).ToArray());
                    if (newApp != appIndex)
                    {
                        appIndex = newApp;
                        ProjectBuildState.LastApplicationId = apps[appIndex].Id;
                        RefreshConcepts();
                    }
                }
                if (GUILayout.Button(AssistantUI.Icon("Refresh", "Reload applications from Playground"), GUILayout.Width(28)))
                    RefreshApps();
            }
            if (loadingApps)
            {
                EditorGUILayout.LabelField(" ", "Loading applications…");
                return;
            }
            if (apps.Length == 0)
            {
                EditorGUILayout.HelpBox("No application loaded from Playground. Press refresh to try again.", MessageType.Warning);
                return;
            }
            DrawConcept();

            EditorGUILayout.Space(12);
            AssistantUI.SubHeader("Before upload", "Each item only warns: Build & Upload asks for confirmation when one is not OK.");
            DateTime? last = ProjectBuildState.LastPrivateBuildUtc;
            bool tested = last.HasValue && changes != null && changes.Count == 0;
            Check(tested, "Tested in Build Develop",
                  !last.HasValue ? "Never built" : changes == null ? "…" : changes.Count == 0 ? "Up to date" : changes.Count + " change(s) since",
                  ChangesTooltip(), Page.Private, 0);
            Check(checks.StrippedRiskCount == 0, "Stripped code", checks.StrippedSummary, null, Page.Checks, 0);
            Check(checks.LintIssueCount == 0, "Luna checks", checks.LintSummary, null, Page.Checks, 1);

            EditorGUILayout.Space(14);
            bool isNew = conceptIndex == 0;
            using (new EditorGUI.DisabledScope(loadingConcepts || (isNew && ConceptNaming.Sanitize(conceptName).Length == 0)))
            {
                if (AssistantUI.PrimaryButton("Build & Upload"))
                    BuildAndUpload();
            }
        }

        // One "Before upload" line: status icon, name, short state, and a link to the page with details.
        void Check(bool ok, string label, string state, string tooltip, Page target, int checksTab)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(AssistantUI.StatusIcon(ok), GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(200));
                GUILayout.Label(new GUIContent(state, tooltip), AssistantUI.Caption);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("View", EditorStyles.miniButton, GUILayout.Width(48)))
                {
                    page = target;
                    checks.Tab = checksTab;
                }
            }
        }

        void DrawConcept()
        {
            if (loadingConcepts)
            {
                EditorGUILayout.LabelField("Concept", "Loading concepts…");
                return;
            }

            string[] options = new[] { NewConceptLabel }.Concat(concepts.Select(c => c.Name)).ToArray();
            int newConcept = EditorGUILayout.Popup(new GUIContent("Concept", conceptIndex > 0 ? "Uploads a new version of this concept" : "Creates a new concept"), conceptIndex, options);
            if (newConcept != conceptIndex)
            {
                conceptIndex = newConcept;
                GUI.FocusControl(null);
            }
            if (conceptIndex > 0)
                return;

            EditorGUI.BeginChangeCheck();
            suffix = EditorGUILayout.TextField(new GUIContent("Suffix", "Optional, appended as _suffix (e.g. low, oil, Ortho)"), suffix);
            if (EditorGUI.EndChangeCheck() && !conceptNameEdited)
                UpdateSuggestedName();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                conceptName = EditorGUILayout.TextField(new GUIContent("Name", "playable<number>_<game>_<yymmdd>[_suffix]"), conceptName);
                if (EditorGUI.EndChangeCheck())
                    conceptNameEdited = true;
                if (GUILayout.Button(new GUIContent("↺", "Back to the suggested name"), GUILayout.Width(28)))
                {
                    conceptNameEdited = false;
                    UpdateSuggestedName();
                    GUI.FocusControl(null);
                }
            }

            string clean = ConceptNaming.Sanitize(conceptName);
            if (clean != conceptName.Trim())
                Note("Saved as " + clean, false);
            if (clean.Length > 0 && !ConceptNaming.TryParse(clean, out _))
                Note("Not playable<number>_<game>_<yymmdd>[_suffix]", true);

            int existing = Array.FindIndex(concepts, c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Note("This concept already exists", true);
                    if (GUILayout.Button("Use it", EditorStyles.miniButton, GUILayout.Width(52)))
                        conceptIndex = existing + 1;
                }
            }
        }

        static void Note(string text, bool warning)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                GUILayout.Label(warning ? AssistantUI.Icon("console.warnicon.sml") : AssistantUI.Icon("console.infoicon.sml"), GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.Label(text, AssistantUI.Caption);
                GUILayout.FlexibleSpace();
            }
        }

        async void RefreshApps()
        {
            if (loadingApps)
                return;
            loadingApps = true;
            try
            {
                await LunaPluginBridge.LoadApps();
                apps = LunaPluginBridge.Apps;
                // Last app picked here first: luna.json's applicationID is often reset to 0 by the plugin.
                int last = ProjectBuildState.LastApplicationId;
                int saved = LunaPluginBridge.SavedApplicationId;
                appIndex = Array.FindIndex(apps, a => a.Id == last);
                if (appIndex < 0)
                    appIndex = Math.Max(0, Array.FindIndex(apps, a => a.Id == saved));
                concepts = new LunaPluginBridge.Concept[0];
                if (apps.Length > 0)
                    RefreshConcepts();
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                loadingApps = false;
                Repaint();
            }
        }

        async void RefreshConcepts()
        {
            int request = ++conceptsRequest;     // Drop answers for an app the user already switched away from.
            int appId = apps[appIndex].Id;
            loadingConcepts = true;
            try
            {
                await LunaPluginBridge.LoadConcepts(appId);
                if (request != conceptsRequest)
                    return;
                concepts = LunaPluginBridge.Concepts.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                string preferred = ConceptNaming.DefaultConcept(concepts.Select(c => c.Name), branch);
                conceptIndex = preferred == null ? 0 : Array.FindIndex(concepts, c => c.Name == preferred) + 1;
                conceptNameEdited = false;
                UpdateSuggestedName();
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                if (request == conceptsRequest)
                    loadingConcepts = false;
                Repaint();
            }
        }

        void UpdateSuggestedName()
        {
            string appName = apps.Length > appIndex ? apps[appIndex].Name : string.Empty;
            conceptName = ConceptNaming.Suggest(concepts.Select(c => c.Name), appName, branch, DateTime.Now, suffix);
        }

        // Build Develop asked by an agent (MCP unity_build_develop): read from the MCP request thread.
        static volatile bool agentBuildRunning;
        public static bool AgentBuildRunning => agentBuildRunning;
        public static BuildReport AgentBuildReport { get; private set; }
        public static string AgentBuildStatus { get; private set; }
        public static double AgentBuildSeconds { get; private set; }

        // Main thread. Null when started, otherwise why not.
        public static string StartAgentBuild(bool openPage)
        {
            var window = GetWindow<LunaBuildAssistantWindow>("Luna Build Assistant");
            if (window.building || agentBuildRunning)
                return "A build is already running.";
            if (!LunaPluginBridge.IsAvailable)
                return "The Unity Playworks Plugin cannot be used: " + LunaPluginBridge.BlockedReason;
            // An agent edits scripts while Unity is in the background, where nothing is imported: pick the files
            // up first, otherwise the build uses old code or Unity reloads the domain in the middle of it.
            AssetDatabase.Refresh();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "Unity is importing / compiling changed scripts. Call again in a few seconds (the MCP server comes back after the reload).";
            // A modified Luna scene makes Unity / the plugin ask to save it: a modal dialog that stops the Editor until the user answers.
            List<string> unsaved = LunaUpload.UnsavedLunaScenes();
            if (unsaved.Count > 0)
                return "Unsaved Luna scene(s): " + string.Join(", ", unsaved) + ". The user saves (or reverts) them in Unity first; agents never save scenes.";
            AgentBuildReport = null;
            AgentBuildStatus = null;
            agentBuildRunning = true;
            window.BuildPrivate(openPage, true);
            return null;
        }

        async void BuildPrivate(bool openBrowser, bool forAgent = false)
        {
            const string action = "Build Develop";
            building = true;
            status = null;
            report = null;
            DateTime started = DateTime.UtcNow;
            try
            {
                // The plugin only builds signed in; say so here instead of in its window.
                if (!SafeIsSignedIn())
                {
                    if (LunaPluginBridge.IsAvailable)
                        report = BuildDiagnostics.NotSignedIn(action, false);
                    return;
                }
                if (!PrepareScenes())
                    return;
                LunaCacheCleaner.ClearBuildCache();
                string reportBefore = BuildDiagnostics.CurrentReportId();
                bool built = await LunaPluginBridge.BuildDevelop();
                report = BuildDiagnostics.Collect(action, false, !built, reportBefore);
                if (!built)
                    return;
                ProjectBuildState.MarkPrivateBuilt();
                status = "Build Develop finished at " + DateTime.Now.ToString("HH:mm") + ".";
                changesCheckedAt = 0;
                if (forAgent)
                {
                    if (openBrowser)
                        LunaDevTools.Open();       // Through the Dev Tools, so the page connects to the agent.
                }
                else if (openBrowser || EditorUtility.DisplayDialog("Build Finished", "Would you like to open the playable in the browser?", "Open in browser", "Close"))
                    OpenPlayable();
                await EstimateSize();
            }
            catch (Exception e)
            {
                if (forAgent)
                    AgentBuildStatus = "Build failed with an exception: " + e.Message;
                Fail(e);
            }
            finally
            {
                building = false;
                if (forAgent)
                {
                    AgentBuildReport = report;
                    AgentBuildStatus = AgentBuildStatus ?? status;
                    AgentBuildSeconds = (DateTime.UtcNow - started).TotalSeconds;
                    agentBuildRunning = false;
                }
                Repaint();
            }
        }

        void BuildAndUpload()
        {
            if (!ConfirmUpload())
                return;

            LunaPluginBridge.App app = apps[appIndex];
            bool isNew = conceptIndex == 0;
            string name = isNew ? ConceptNaming.Sanitize(conceptName) : concepts[conceptIndex - 1].Name;
            int creativeId = isNew ? -1 : concepts[conceptIndex - 1].Id;
            try
            {
                report = null;
                uploadReportBefore = BuildDiagnostics.CurrentReportId();
                uploadStartedAt = EditorApplication.timeSinceStartup;
                uploadSawBuilding = false;
                string cancelled = LunaUpload.Start(app.Id, name, creativeId);
                if (cancelled != null)
                {
                    uploadStartedAt = -1;
                    status = cancelled;
                    return;
                }
                checks.Invalidate();
                status = $"Build & upload started: {app.Name} / {name}{(isNew ? " (new concept)" : string.Empty)}. " +
                         "The browser opens the Creative Library upload page when it is done.";
                Debug.Log("[LunaBuildAssistant] " + status);
            }
            catch (Exception e)
            {
                uploadStartedAt = -1;
                Fail(e);
            }
        }

        // Build & upload runs in the plugin without a result: once it stops building, read what it reported.
        void WatchUpload(bool busy)
        {
            if (busy)
            {
                uploadSawBuilding = true;
                return;
            }
            if (!uploadSawBuilding && EditorApplication.timeSinceStartup - uploadStartedAt < UploadStartTimeout)
                return;
            uploadStartedAt = -1;
            report = BuildDiagnostics.Collect("Build & upload", true, false, uploadReportBefore);
            if (report.Failed)
                status = null;
            Repaint();
        }

        // Uploading work that was never checked in Build Develop, or that the checks flag, needs an explicit yes.
        bool ConfirmUpload()
        {
            List<string> parts = LunaUpload.Warnings(checks);
            if (parts.Count == 0)
                return true;
            return EditorUtility.DisplayDialog("Before uploading", string.Join("\n\n", parts) + "\n\nBuild & upload anyway?", "Build & Upload", "Cancel");
        }

        // Regenerates the _NoPrefab copies (or points Luna back at the source scenes); false when cancelled.
        bool PrepareScenes()
        {
            try
            {
                if (NoPrefabScenes.Prepare(out string summary))
                {
                    if (!string.IsNullOrEmpty(summary))
                        Debug.Log("[LunaBuildAssistant] " + summary);
                    return true;
                }
                status = "Build cancelled: save the modified scene first.";
            }
            catch (InvalidOperationException e)
            {
                status = "Build cancelled: " + e.Message;     // e.g. the _NoPrefab scene is open.
            }
            return false;
        }

        // The plugin's Size breakdown for the chosen network, right after Build Develop.
        async System.Threading.Tasks.Task EstimateSize()
        {
            if (!LunaPluginBridge.CanEstimateSize)
                return;
            await sizeView.Estimate();
            BuildSizeReport.Snapshot now = BuildSizeReport.Latest, before = BuildSizeReport.Previous;
            if (now != null)
                status += $" {NetworkName(now.network)}: {BuildSizeReport.FormatBytes(now.totalBytes)}" +
                          (before == null ? "." : " (" + BuildSizeReport.FormatDelta(now.totalBytes - before.totalBytes) + ").");
        }

        static string NetworkName(string network) =>
            LunaPluginBridge.SizeNetworks.FirstOrDefault(n => n.Key == network).Value ?? network;

        bool SafeIsSignedIn() => Safe(() => LunaPluginBridge.IsSignedIn);
        bool SafeIsBuilding() => LunaPluginBridge.IsAvailable && Safe(() => LunaPluginBridge.IsBuilding);

        T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception e) { Fail(e); return default; }
        }

        void Fail(Exception e)
        {
            LunaPluginBridge.Block(e);
            Debug.LogException(e);
            status = null;
            Repaint();
        }
    }
}
