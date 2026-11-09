using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Supercent.Util.Editor
{
    // This Editor's entry in ~/.supercent/luna-mcp/instances (see LunaMachine): what the MCP broker needs to route an
    // agent's calls to the Editor of its project. Written when something changes and every few seconds (heartbeat),
    // marked "reloading" during a domain reload, removed when Unity quits. Only Debug from Unity: callable off the main thread.
    public static class LunaInstance
    {
        const double HeartbeatSeconds = 5.0;

        static string lastContent;
        static DateTime lastWriteUtc;
        static string lastProject;

        public static void Heartbeat(string projectPath, int pid, string mcpUrl, string state, string devToolsUrl, int developPort,
                                     string unityVersion, string note)
        {
            var data = new Dictionary<string, object>
            {
                { "projectPath", projectPath },
                { "projectName", Path.GetFileName(projectPath) },
                { "pid", pid },
                { "unity", unityVersion },
                { "mcpUrl", mcpUrl },
                { "devToolsUrl", devToolsUrl },
                { "developServerPort", developPort },
                { "state", state },
                { "note", note },
            };
            string content = MiniJson.Write(data);
            if (content == lastContent && (DateTime.UtcNow - lastWriteUtc).TotalSeconds < HeartbeatSeconds)
                return;
            data["updatedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (TryWrite(projectPath, MiniJson.Write(data)))
                lastContent = content;
        }

        // A state change that cannot wait for the next heartbeat (e.g. a domain reload starting).
        public static void Write(string projectPath, int pid, string mcpUrl, string state)
        {
            string file = LunaMachine.InstanceFile(projectPath);
            try
            {
                var data = File.Exists(file) ? MiniJson.Obj(MiniJson.Parse(File.ReadAllText(file))) : null;
                data = data ?? new Dictionary<string, object> { { "projectPath", projectPath }, { "pid", pid } };
                data["state"] = state;
                if (mcpUrl != null)
                    data["mcpUrl"] = mcpUrl;
                data["updatedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                TryWrite(projectPath, MiniJson.Write(data));
            }
            catch (Exception)
            {
                // Next heartbeat writes it again.
            }
            lastContent = null;
        }

        public static void Remove()
        {
            try
            {
                if (lastProject != null)
                    File.Delete(LunaMachine.InstanceFile(lastProject));
            }
            catch (Exception)
            {
                // The broker drops entries whose process is gone.
            }
        }

        static bool TryWrite(string projectPath, string json)
        {
            try
            {
                LunaMachine.WriteAtomic(LunaMachine.InstanceFile(projectPath), json);
                lastWriteUtc = DateTime.UtcNow;
                lastProject = projectPath;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The tool list (so the broker lists tools while no Editor is open) and the broker itself.
        public static void Publish(McpServer server, string brokerSource)
        {
            try
            {
                var list = MiniJson.Obj(MiniJson.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}")));
                var result = MiniJson.Obj(list?["result"]);
                string json = MiniJson.Write(new Dictionary<string, object>
                {
                    { "serverVersion", server.Version },
                    { "instructions", server.Instructions },
                    { "tools", result?["tools"] },
                });
                if (!File.Exists(LunaMachine.ToolsFile) || File.ReadAllText(LunaMachine.ToolsFile) != json)
                    LunaMachine.WriteAtomic(LunaMachine.ToolsFile, json);
                LunaMachine.InstallBroker(brokerSource);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[LunaBuildAssistant] MCP broker files not written: " + e.Message);
            }
        }
    }
}
