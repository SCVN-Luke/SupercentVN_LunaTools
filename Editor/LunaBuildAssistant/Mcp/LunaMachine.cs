using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Supercent.Util.Editor
{
    // What several Unity Editors on this machine share, in ~/.supercent/luna-mcp:
    //   ports.json       the ports each project keeps (Dev Tools / MCP proxy and the plugin's develop server), so two
    //                    projects open side by side never take each other's port, and a project keeps its ports across
    //                    restarts (agents and bookmarks keep working).
    //   instances/*.json one file per running Editor (project, pid, MCP URL, state), read by the MCP broker.
    //   tools.json       the last MCP tool list, so the broker can list tools before any Editor is open.
    //   luna-mcp.mjs     the MCP broker (stdio) agents start; installed from Broker~ next to this tool.
    // No Unity API here: tested outside the Editor.
    public static class LunaMachine
    {
        public const int DevToolsFirstPort = 8100;
        public const int DevelopFirstPort = 8000;
        public const int PortRange = 100;
        static readonly TimeSpan ReservationExpiry = TimeSpan.FromDays(60);   // Ports of projects not opened since are reused.

        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".supercent", "luna-mcp");

        static string PortsFile => Path.Combine(Root, "ports.json");
        static string LockFile => Path.Combine(Root, "ports.lock");
        public static string InstancesDir => Path.Combine(Root, "instances");
        public static string ToolsFile => Path.Combine(Root, "tools.json");
        public static string BrokerFile => Path.Combine(Root, "luna-mcp.mjs");

        public struct Ports
        {
            public int DevTools;
            public int Develop;
            public string Note;         // Set when a port changed, e.g. taken by another project.
        }

        // The project's ports: kept from earlier runs, otherwise the first ones no other project keeps.
        //   devToolsFree: whether this Editor can listen on a port now (the proxy is stopped while this runs).
        //   currentDevelop: the plugin's develop server port setting, adopted when no other project keeps it.
        // The develop port is never checked for being free: the plugin's own server may already run on it.
        public static Ports Assign(string projectPath, int currentDevelop, Func<int, bool> devToolsFree, Func<int, bool> developFree)
        {
            string key = Normalize(projectPath);
            return WithLock(() =>
            {
                Dictionary<string, object> projects = ReadProjects();
                var mine = MiniJson.Obj(projects.TryGetValue(key, out object m) ? m : null) ?? new Dictionary<string, object>();
                DateTime now = DateTime.UtcNow;
                var others = projects.Where(p => p.Key != key && !Expired(MiniJson.Obj(p.Value), now))
                                     .Select(p => MiniJson.Obj(p.Value)).Where(p => p != null).ToList();
                var takenDevTools = new HashSet<int>(others.Select(p => (int)MiniJson.Num(p, "devTools", 0)));
                var takenDevelop = new HashSet<int>(others.Select(p => (int)MiniJson.Num(p, "develop", 0)));

                var ports = new Ports { DevTools = (int)MiniJson.Num(mine, "devTools", 0), Develop = (int)MiniJson.Num(mine, "develop", 0) };
                var notes = new List<string>();

                if (ports.DevTools == 0 || takenDevTools.Contains(ports.DevTools) || !devToolsFree(ports.DevTools))
                {
                    int before = ports.DevTools;
                    ports.DevTools = First(DevToolsFirstPort, p => !takenDevTools.Contains(p) && devToolsFree(p));
                    if (before != 0)
                        notes.Add($"Dev Tools / MCP port {before} is in use elsewhere: now {ports.DevTools}.");
                }
                // The plugin's setting wins unless another project keeps that port (the user may have changed it there).
                if (currentDevelop > 0 && !takenDevelop.Contains(currentDevelop) && currentDevelop != ports.DevTools)
                    ports.Develop = currentDevelop;
                else if (ports.Develop == 0 || takenDevelop.Contains(ports.Develop))
                {
                    ports.Develop = First(DevelopFirstPort, p => !takenDevelop.Contains(p) && !takenDevTools.Contains(p) && p != ports.DevTools && developFree(p));
                    notes.Add($"Develop server port {currentDevelop} is kept by another project: this project now uses {ports.Develop}.");
                }

                mine["devTools"] = ports.DevTools;
                mine["develop"] = ports.Develop;
                mine["seen"] = now.ToString("o", CultureInfo.InvariantCulture);
                projects[key] = mine;
                WriteAtomic(PortsFile, MiniJson.Write(new Dictionary<string, object> { { "projects", projects } }));
                ports.Note = notes.Count == 0 ? null : string.Join(" ", notes);
                return ports;
            });
        }

        static bool Expired(Dictionary<string, object> entry, DateTime now) =>
            entry == null || !DateTime.TryParse(MiniJson.Str(entry, "seen"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime seen)
            || now - seen > ReservationExpiry;

        static int First(int from, Func<int, bool> usable)
        {
            for (int port = from; port < from + PortRange; port++)
                if (usable(port))
                    return port;
            throw new IOException($"No free port between {from} and {from + PortRange - 1}");
        }

        static Dictionary<string, object> ReadProjects()
        {
            try
            {
                if (File.Exists(PortsFile))
                    return MiniJson.Obj(MiniJson.Obj(MiniJson.Parse(File.ReadAllText(PortsFile)))?["projects"]) ?? new Dictionary<string, object>();
            }
            catch (Exception)
            {
                // Unreadable: start over; every running Editor writes its ports again on its next start.
            }
            return new Dictionary<string, object>();
        }

        // Editors starting at the same time take turns.
        static T WithLock<T>(Func<T> work)
        {
            Directory.CreateDirectory(Root);
            for (int attempt = 0; ; attempt++)
            {
                FileStream gate;
                try
                {
                    gate = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (attempt < 100)
                {
                    Thread.Sleep(20);
                    continue;
                }
                using (gate)
                    return work();
            }
        }

        public static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Root);
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }

        public static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

        // instances/<hash>.json for this project.
        public static string InstanceFile(string projectPath)
        {
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(projectPath).ToLowerInvariant()));
                return Path.Combine(InstancesDir, string.Concat(hash.Take(6).Select(b => b.ToString("x2"))) + ".json");
            }
        }

        // The version written in the broker's first lines ("// luna-mcp-broker version: N").
        public static int BrokerVersion(string file)
        {
            try
            {
                foreach (string line in File.ReadLines(file).Take(5))
                {
                    int at = line.IndexOf("luna-mcp-broker version:", StringComparison.Ordinal);
                    if (at >= 0 && int.TryParse(line.Substring(at + "luna-mcp-broker version:".Length).Trim(), out int version))
                        return version;
                }
            }
            catch (Exception)
            {
                // Missing or unreadable.
            }
            return 0;
        }

        // Installs the broker shipped with this tool when it is newer than the installed one (several projects may
        // carry different versions of the tool: the newest wins). True when it was written.
        public static bool InstallBroker(string source)
        {
            if (source == null || !File.Exists(source))
                return false;
            if (BrokerVersion(source) <= BrokerVersion(BrokerFile) && File.Exists(BrokerFile))
                return false;
            WriteAtomic(BrokerFile, File.ReadAllText(source));
            return true;
        }
    }
}
