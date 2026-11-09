using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Supercent.Util.Editor
{
    // Build Develop with the Dev Tools overlay (DevTools/LunaDevTools.js): the plugin's develop server is opened
    // through DevToolsProxy, which adds the script to the page on its way to the browser. Nothing is written to the
    // build output, the script sits in an Editor folder Luna never exports, so the uploaded playable, luna.json and
    // the size estimate stay exactly as without it. The proxy stops on every domain reload and when Unity quits.
    [InitializeOnLoad]
    public static class LunaDevTools
    {
        public const int FirstPort = 8100;
        const int PortCount = 20;
        const int DefaultUpstreamPort = 8000;                  // The plugin's default local server port.
        const string UpstreamPortPref = "LUNA_LOCAL_SERVER_PORT_";
        const string EnabledPref = "SupercentVN.LunaBuildAssistant.DevTools";
        const string ScriptName = "LunaDevTools.js";
        const double ServerStartTimeout = 15.0;

        static DevToolsProxy proxy;

        static LunaDevTools()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        // Open the playable through the Dev Tools after Build Develop.
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPref, true);
            set => EditorPrefs.SetBool(EnabledPref, value);
        }

        public static bool IsRunning => proxy != null;
        public static string LastError { get; private set; }
        public static string LocalUrl => proxy == null ? null : $"http://localhost:{proxy.Port}/";

        // For a phone on the same network.
        public static string LanUrl
        {
            get
            {
                string ip = proxy == null ? null : LanAddress();
                return ip == null ? null : $"http://{ip}:{proxy.Port}/";
            }
        }

        // The plugin keeps the develop server port per project, under the Assets path in base64.
        static string UpstreamPortKey => UpstreamPortPref + Convert.ToBase64String(Encoding.UTF8.GetBytes(Application.dataPath));

        public static int UpstreamPort => EditorPrefs.GetInt(UpstreamPortKey, DefaultUpstreamPort);

        public static string ProjectPath => Directory.GetParent(Application.dataPath).FullName;

        // Why a port changed at the last start (another project keeps it), for the window and luna_status.
        public static string PortNote { get; private set; }

        // Starts the proxy on the ports this project keeps on this machine (LunaMachine), so several projects run
        // side by side and agents find each one at the same address after every restart.
        public static bool Start()
        {
            LastError = null;
            if (proxy != null)
            {
                proxy.UpstreamPort = UpstreamPort;
                return true;
            }
            string script = ScriptFile();
            if (script == null)
            {
                LastError = ScriptName + " not found next to the Build Assistant";
                return false;
            }
            try
            {
                int current = EditorPrefs.HasKey(UpstreamPortKey) ? EditorPrefs.GetInt(UpstreamPortKey) : DefaultUpstreamPort;
                LunaMachine.Ports ports = LunaMachine.Assign(ProjectPath, current, DevToolsProxy.CanListen, p => !DevToolsProxy.IsListening(p, 100));
                PortNote = ports.Note;
                if (ports.Develop != current)
                {
                    EditorPrefs.SetInt(UpstreamPortKey, ports.Develop);
                    PortNote = (PortNote + " Restart the develop server (or Unity) if it already runs on " + current + ".").Trim();
                }
                if (PortNote != null)
                    Debug.LogWarning("[LunaBuildAssistant] " + PortNote);
                proxy = DevToolsProxy.TryStart(ports.DevTools, ports.Develop, script)
                        ?? DevToolsProxy.Start(FirstPort, PortCount, ports.Develop, script);   // Taken in between: any free port.
                proxy.ExpectedProjectId = ProjectId();
                AttachAgents();
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                return false;
            }
        }

        // The agents' channel (pages poll it for commands) is always served; the MCP endpoint only when enabled.
        public static void AttachAgents()
        {
            if (proxy == null)
                return;
            proxy.Channel = LunaMcp.Channel;
            proxy.Mcp = LunaMcp.Enabled ? LunaMcp.Server : null;
        }

        public static void Stop()
        {
            proxy?.Dispose();
            proxy = null;
        }

        // Starts the plugin's develop server if it is not running, then opens the page through the proxy.
        public static async void Open()
        {
            try
            {
                if (!Start())
                {
                    Debug.LogError("[LunaBuildAssistant] Dev Tools: " + LastError);
                    return;
                }
                int upstream = UpstreamPort;
                if (!await Task.Run(() => DevToolsProxy.IsListening(upstream)))
                {
                    LunaPluginBridge.StartDevelopServer();
                    double until = EditorApplication.timeSinceStartup + ServerStartTimeout;
                    while (!await Task.Run(() => DevToolsProxy.IsListening(upstream)) && EditorApplication.timeSinceStartup < until)
                        await Task.Delay(250);
                }
                string other = await Task.Run(() => UpstreamProblem);
                if (other != null)
                    Debug.LogError("[LunaBuildAssistant] Dev Tools: " + other);
                Application.OpenURL(LocalUrl);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogException(e);
            }
        }

        // luna.json projectId, to recognise this project's build on the develop server.
        static string ProjectId()
        {
            try
            {
                string json = File.ReadAllText(Path.Combine(ProjectPath, "luna.json"));
                var match = System.Text.RegularExpressions.Regex.Match(json, "\"projectId\"\\s*:\\s*\"(?<v>[^\"]+)\"");
                return match.Success ? match.Groups["v"].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Null when the develop server serves this project's build (or is not running).
        public static string UpstreamProblem => proxy?.OtherProjectUpstream();

        static string ScriptFile([CallerFilePath] string self = "")
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(self)) ?? string.Empty, "DevTools", ScriptName);
                if (File.Exists(path))
                    return path;
            }
            catch (Exception)
            {
                // Fall back to the asset database below.
            }
            string asset = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(ScriptName))
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("/DevTools/" + ScriptName, StringComparison.Ordinal));
            return asset == null ? null : Path.GetFullPath(asset);
        }

        // First up, non-loopback IPv4 address, private ranges first.
        static string LanAddress()
        {
            try
            {
                var addresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .Where(a => !a.StartsWith("169.254.", StringComparison.Ordinal))
                    .ToList();
                return addresses.FirstOrDefault(a => a.StartsWith("192.168.", StringComparison.Ordinal) || a.StartsWith("10.", StringComparison.Ordinal))
                       ?? addresses.FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
