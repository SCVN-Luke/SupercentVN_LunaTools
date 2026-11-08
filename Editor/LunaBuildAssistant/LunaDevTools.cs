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
        public static int UpstreamPort =>
            EditorPrefs.GetInt(UpstreamPortPref + Convert.ToBase64String(Encoding.UTF8.GetBytes(Application.dataPath)), DefaultUpstreamPort);

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
                proxy = DevToolsProxy.Start(FirstPort, PortCount, UpstreamPort, script);
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                return false;
            }
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
                Application.OpenURL(LocalUrl);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogException(e);
            }
        }

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
