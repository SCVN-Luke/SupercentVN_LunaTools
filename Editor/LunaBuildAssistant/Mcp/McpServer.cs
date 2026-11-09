using System;
using System.Collections.Generic;
using System.Linq;

namespace Supercent.Util.Editor
{
    // Model Context Protocol server over Streamable HTTP (one JSON response per POST, no server-initiated stream):
    // initialize, ping, tools/list, tools/call. Tools are registered by the Dev Tools (page commands) and by the
    // Editor (builds, checks, sources). No Unity API here: tested outside the Editor.
    public sealed class McpServer
    {
        public const string Name = "luna-devtools";
        static readonly string[] Versions = { "2025-06-18", "2025-03-26", "2024-11-05" };

        public sealed class Tool
        {
            public string Name;
            public string Description;
            public Dictionary<string, object> InputSchema;
            public bool ReadOnly;                                        // Annotation: only reads the page or the project.
            public Func<Dictionary<string, object>, ToolResult> Run;
        }

        public sealed class ToolResult
        {
            public readonly List<object> Content = new List<object>();
            public bool IsError;

            public static ToolResult Text(string text, bool error = false)
            {
                var r = new ToolResult { IsError = error };
                r.Content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", text } });
                return r;
            }

            // Data as pretty-enough JSON text: what models read best.
            public static ToolResult Json(object data) => Text(MiniJson.Write(data));

            public ToolResult WithImage(string base64, string mimeType)
            {
                Content.Add(new Dictionary<string, object> { { "type", "image" }, { "data", base64 }, { "mimeType", mimeType } });
                return this;
            }
        }

        readonly Dictionary<string, Tool> tools = new Dictionary<string, Tool>();
        readonly object gate = new object();
        public string Version = "1.1.0";
        public string Instructions;

        // Every tool takes an optional "project" argument (the Unity project the call is meant for); CheckProject returns
        // why it does not match this server (the call is then refused), or null. Unset: no such argument.
        public const string ProjectArgument = "project";
        public Func<string, string> CheckProject;

        public void Register(Tool tool)
        {
            lock (gate)
                tools[tool.Name] = tool;
        }

        public void Unregister(string name)
        {
            lock (gate)
                tools.Remove(name);
        }

        public IReadOnlyCollection<string> ToolNames
        {
            get { lock (gate) return tools.Keys.ToList(); }
        }

        // One HTTP POST body (a message or a batch). Returns the response body, or null for 202 Accepted (notifications only).
        public string Handle(string body)
        {
            object message;
            try
            {
                message = MiniJson.Parse(body);
            }
            catch (FormatException e)
            {
                return MiniJson.Write(Error(null, -32700, "Parse error: " + e.Message));
            }

            if (message is List<object> batch)
            {
                var responses = batch.Select(m => HandleOne(MiniJson.Obj(m))).Where(r => r != null).ToList();
                return responses.Count == 0 ? null : MiniJson.Write(responses);
            }
            object response = HandleOne(MiniJson.Obj(message));
            return response == null ? null : MiniJson.Write(response);
        }

        object HandleOne(Dictionary<string, object> request)
        {
            if (request == null)
                return Error(null, -32600, "Invalid request");
            string method = MiniJson.Str(request, "method");
            bool isNotification = !request.ContainsKey("id");
            object id = request.TryGetValue("id", out object rawId) ? rawId : null;
            if (method == null)
                return isNotification ? null : Error(id, -32600, "Invalid request: no method");
            if (isNotification)
                return null;                         // notifications/initialized, cancelled, ...: nothing to answer.

            var parameters = MiniJson.Obj(request.TryGetValue("params", out object p) ? p : null) ?? new Dictionary<string, object>();
            switch (method)
            {
                case "initialize":
                    string asked = MiniJson.Str(parameters, "protocolVersion");
                    var result = new Dictionary<string, object>
                    {
                        { "protocolVersion", Versions.Contains(asked) ? asked : Versions[0] },
                        { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object> { { "listChanged", false } } } } },
                        { "serverInfo", new Dictionary<string, object> { { "name", Name }, { "version", Version } } },
                    };
                    if (!string.IsNullOrEmpty(Instructions))
                        result["instructions"] = Instructions;
                    return Result(id, result);
                case "ping":
                    return Result(id, new Dictionary<string, object>());
                case "tools/list":
                    List<Tool> list;
                    lock (gate)
                        list = tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                    return Result(id, new Dictionary<string, object>
                    {
                        { "tools", list.Select(t => (object)new Dictionary<string, object>
                            {
                                { "name", t.Name },
                                { "description", t.Description },
                                { "inputSchema", WithProject(t.InputSchema ?? Schema()) },
                                { "annotations", new Dictionary<string, object> { { "readOnlyHint", t.ReadOnly }, { "openWorldHint", false } } },
                            }).ToList() },
                    });
                case "tools/call":
                    return Result(id, Call(parameters));
                default:
                    return Error(id, -32601, "Method not found: " + method);
            }
        }

        Dictionary<string, object> Call(Dictionary<string, object> parameters)
        {
            string name = MiniJson.Str(parameters, "name");
            Tool tool;
            lock (gate)
                tools.TryGetValue(name ?? string.Empty, out tool);
            ToolResult result;
            if (tool == null)
                result = ToolResult.Text("Unknown tool: " + name, true);
            else
            {
                try
                {
                    var args = MiniJson.Obj(parameters.TryGetValue("arguments", out object a) ? a : null) ?? new Dictionary<string, object>();
                    string wrongProject = CheckProject?.Invoke(MiniJson.Str(args, ProjectArgument));
                    result = wrongProject != null ? ToolResult.Text(wrongProject, true) : tool.Run(args) ?? ToolResult.Text("Done.");
                }
                catch (Exception e)
                {
                    result = ToolResult.Text(e.Message, true);
                }
            }
            var body = new Dictionary<string, object> { { "content", result.Content } };
            if (result.IsError)
                body["isError"] = true;
            return body;
        }

        static Dictionary<string, object> Result(object id, object result) =>
            new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } };

        static Dictionary<string, object> Error(object id, int code, string message) =>
            new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" }, { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } },
            };

        Dictionary<string, object> WithProject(Dictionary<string, object> schema)
        {
            if (CheckProject == null || !(schema.TryGetValue("properties", out object p) && p is Dictionary<string, object> properties)
                || properties.ContainsKey(ProjectArgument))
                return schema;
            var copy = new Dictionary<string, object>(schema)
            {
                ["properties"] = new Dictionary<string, object>(properties)
                {
                    [ProjectArgument] = Prop("string", "Unity project this call is for (path or folder name). Optional: the call is refused " +
                                                       "when this Editor has another project open."),
                },
            };
            return copy;
        }

        // JSON Schema helpers for tool inputs.
        public static Dictionary<string, object> Schema(Dictionary<string, object> properties = null, params string[] required)
        {
            var schema = new Dictionary<string, object> { { "type", "object" }, { "properties", properties ?? new Dictionary<string, object>() } };
            if (required.Length > 0)
                schema["required"] = required.ToList();
            return schema;
        }

        public static Dictionary<string, object> Prop(string type, string description, object @default = null, IEnumerable<string> values = null)
        {
            var p = new Dictionary<string, object> { { "type", type }, { "description", description } };
            if (@default != null)
                p["default"] = @default;
            if (values != null)
                p["enum"] = values.ToList();
            return p;
        }
    }
}
