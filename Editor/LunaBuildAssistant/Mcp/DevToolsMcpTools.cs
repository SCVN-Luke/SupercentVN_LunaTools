using System;
using System.Collections.Generic;
using System.Linq;

namespace Supercent.Util.Editor
{
    // MCP tools that run in a Build Develop page through the Dev Tools agent (DevTools/LunaDevTools.js).
    // Reading tools only touch engine-side data and raw fields: they call no C# method of the game or of Luna's
    // UnityEngine, so Luna's runtime analysis (what the cloud build strips) sees nothing of them.
    public static class DevToolsMcpTools
    {
        public const string Instructions =
            "Debug a Luna playable (Unity Playworks) while it runs in Build Develop.\n" +
            "- Pages connect when the Build Develop page is opened through the Dev Tools (Luna Build Assistant). Start with luna_status: " +
            "it lists connected pages (sessions; a tab keeps its session across reloads); tools use the most recently loaded page unless " +
            "'session' is given. unity_build_develop starts a build (poll unity_build_report); unity_compile checks C# changes first.\n" +
            "- Errors: luna_get_errors gives deduplicated console errors with the C# method of each stack frame, and static constructors that " +
            "failed while Luna started (Luna hides those; the class keeps null statics). unity_find_source turns a C# name into file:line.\n" +
            "- Reproduce: luna_reload for a fresh start, luna_simulate to play touch / drag steps frame by frame (works in hidden tabs), " +
            "then luna_get_errors / luna_screenshot.\n" +
            "- Performance: luna_get_stats (frame time, draw calls, heap), luna_record_hot_paths (cost per script callback / coroutine and the " +
            "methods called inside, while the game is played), luna_capture_frame (draw calls of one frame).\n" +
            "- State: luna_find_objects / luna_get_object read the scene; luna_watch_field records who changes a field (with C# stack).\n" +
            "- Size: unity_build_size lists what makes the playable heavy (per kind, largest assets with their Luna settings, duplicates); " +
            "unity_asset_size_try estimates other texture / sound settings (images to judge quality); unity_asset_settings_apply writes " +
            "luna.json only after the user agreed to the change.\n" +
            "- Upload: only when the user asks. unity_upload_targets lists applications / concepts and the pre-upload warnings; confirm the " +
            "application and concept with the user, then unity_build_upload and poll unity_build_report (upload.state).\n" +
            "- Tools marked as changing the page (luna_control, luna_eval) can run game code: Luna's runtime analysis then counts that code as " +
            "used, so the cloud build may keep a little more code. Prefer reading tools; tell the user to replay normally before uploading.";

        public static void Register(McpServer server, DevToolsChannel channel, Func<Dictionary<string, object>> editorStatus = null)
        {
            var session = MiniJsonSchema.Session;

            server.Register(new McpServer.Tool
            {
                Name = "luna_status",
                Description = "Connected Build Develop pages (sessions: url, title, browser, engine ready, frame, seconds since loaded, " +
                              "failed static constructors, which one tools use by default) and the Editor state (Dev Tools URL, last build). Call first.",
                ReadOnly = true,
                InputSchema = McpServer.Schema(),
                Run = args =>
                {
                    DateTime now = DateTime.UtcNow;
                    var data = new Dictionary<string, object>
                    {
                        { "sessions", channel.Sessions().Select(s => (object)new Dictionary<string, object>
                            {
                                { "session", s.Id }, { "default", s.IsDefault }, { "url", s.Url }, { "title", s.Title }, { "browser", s.UserAgent },
                                { "engineReady", s.EngineReady }, { "frame", s.Frame },
                                { "loadedSecondsAgo", Math.Round((now - s.LoadedUtc).TotalSeconds, 1) },
                                { "secondsSinceSeen", Math.Round(s.SecondsSinceSeen, 1) },
                                { "staticConstructorFailures", s.StaticConstructorFailures },
                            }).ToList() },
                    };
                    if (editorStatus != null)
                        data["editor"] = editorStatus();
                    if (channel.Sessions().Count == 0)
                        data["hint"] = "No page connected: open the Build Develop page through the Dev Tools (or call unity_build_develop).";
                    return McpServer.ToolResult.Json(data);
                },
            });

            Page(server, channel, "luna_get_stats", "stats", 10000, true,
                "Frame time (avg / p95 / worst), FPS, time per engine section (scripts, animation, physics, render), engine and WebGL draw calls, " +
                "triangles, shader switches, GPU uploads and memory estimate, JS heap and allocation rate.",
                McpServer.Schema(new Dictionary<string, object> { { "session", session } }));

            Page(server, channel, "luna_get_errors", "errors", 10000, true,
                "Console errors and warnings of the page, deduplicated with counts, first/last frame, the stack and the C# method of each " +
                "frame of the stack (from Luna's compiled scripts). Errors repeated every frame show a high count. staticConstructorFailures " +
                "lists C# static constructors that threw while the page started: Luna hides them, and the class's static fields stay null.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "level", McpServer.Prop("string", "Which messages", "all", new[] { "all", "error", "warning" }) },
                    { "limit", McpServer.Prop("integer", "Most frequent first", 30) },
                    { "clear", McpServer.Prop("boolean", "Clear the list after reading (to see only new ones next time)", false) },
                }));

            server.Register(new McpServer.Tool
            {
                Name = "luna_record_hot_paths",
                Description = "Records for N seconds while the game runs (have the user play, or use luna_control), then returns: scripts (each " +
                              "MonoBehaviour callback and coroutine: self / total ms per frame, worst frame, calls made inside per frame and the most " +
                              "called methods inside, engine work included) and the most called methods (project code only by default).",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "seconds", McpServer.Prop("number", "Recording time, 1-30", 5) },
                    { "filter", McpServer.Prop("string", "Methods list: project code only, or engine too", "project", new[] { "project", "all" }) },
                    { "top", McpServer.Prop("integer", "Rows per table", 20) },
                }),
                Run = args =>
                {
                    double seconds = Math.Max(1, Math.Min(30, MiniJson.Num(args, "seconds", 5)));
                    args["seconds"] = seconds;
                    return McpServer.ToolResult.Json(channel.Send(MiniJson.Str(args, "session"), "hotpaths", args, (int)(seconds * 1000) + 15000));
                },
            });

            Page(server, channel, "luna_capture_frame", "capture", 15000, true,
                "Captures the draw calls of the next rendered frame: shader, vertex count, instances, render target, blend/depth/cull, bound " +
                "textures, plus totals per shader and per target. Use to find what dominates draw calls.",
                McpServer.Schema(new Dictionary<string, object> { { "session", session }, { "limit", McpServer.Prop("integer", "Draw rows to return", 60) } }));

            Page(server, channel, "luna_find_objects", "find", 15000, true,
                "Searches the running scene (engine data, no game code runs): GameObjects by name and/or script class, with path, active state, " +
                "world position and scripts. Paths feed luna_get_object and luna_watch_field.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "name", McpServer.Prop("string", "Part of the GameObject name (case-insensitive)") },
                    { "script", McpServer.Prop("string", "Part of a MonoBehaviour class name, e.g. TradingPostOreCargo") },
                    { "activeOnly", McpServer.Prop("boolean", "Only objects active in the hierarchy", false) },
                    { "limit", McpServer.Prop("integer", "Maximum results", 50) },
                }));

            Page(server, channel, "luna_get_object", "object", 15000, true,
                "One GameObject by path: active state, position, children, components, and the fields of its MonoBehaviours (raw field values; " +
                "references shown by type, lists by size). No getter or game method runs.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "path", McpServer.Prop("string", "Path from luna_find_objects, e.g. 'World/Player/Ore Stacker'") },
                    { "script", McpServer.Prop("string", "Only this MonoBehaviour (part of its class name)") },
                }, "path"));

            Page(server, channel, "luna_simulate", "simulate", 90000, false,
                "Plays input steps as engine frames of a fixed delta, synchronously (same result in a hidden tab or a slow browser), through " +
                "the engine's own input like a player's touch: Unity Input / EventSystem / joysticks see it. Coordinates are fractions of " +
                "the canvas (0,0 top-left, 1,1 bottom-right). A step with x,y holds the finger there (the first one touches down, the next " +
                "ones drag to their point over their frames); a step without x,y releases and waits. Example, drag a joystick up for 2 s " +
                "then wait 1 s: [{x:0.5,y:0.7,frames:1},{x:0.5,y:0.5,frames:120},{frames:60}]. At most 3600 frames per call. Returns the " +
                "frames run and how many new errors appeared. Runs game code like real play.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "steps", new Dictionary<string, object>
                        {
                            { "type", "array" },
                            { "description", "Steps in order: {x, y, frames} = finger held at / dragged to (x, y); {frames} = released" },
                            { "items", McpServer.Schema(new Dictionary<string, object>
                                {
                                    { "x", McpServer.Prop("number", "0-1 across the canvas") },
                                    { "y", McpServer.Prop("number", "0-1 down the canvas") },
                                    { "frames", McpServer.Prop("integer", "Engine frames for this step") },
                                }, "frames") },
                        } },
                    { "dt", McpServer.Prop("number", "Seconds per frame", 1.0 / 60) },
                    { "keepPressed", McpServer.Prop("boolean", "Leave the finger down after the last step (released by default)", false) },
                }, "steps"));

            server.Register(new McpServer.Tool
            {
                Name = "luna_reload",
                Description = "Reloads the page (a fresh start of the playable, like F5) and waits until it is back with its engine running. The " +
                              "session id stays the same. Use before reproducing a bug; static constructor failures of the new load then show in " +
                              "luna_get_errors.",
                ReadOnly = false,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "waitSeconds", McpServer.Prop("number", "How long to wait for the page to come back", 60) },
                }),
                Run = args =>
                {
                    string id = channel.Resolve(MiniJson.Str(args, "session"));
                    var answer = MiniJson.Obj(channel.Send(id, "reload", new Dictionary<string, object>(), 10000));
                    DateTime started = DateTime.UtcNow;
                    var info = channel.WaitForLoad(id, MiniJson.Str(answer, "previousLoadId"),
                        (int)(Math.Max(5, Math.Min(180, MiniJson.Num(args, "waitSeconds", 60))) * 1000));
                    return McpServer.ToolResult.Json(new Dictionary<string, object>
                    {
                        { "session", info.Id }, { "loadId", info.LoadId }, { "engineReady", info.EngineReady }, { "frame", info.Frame },
                        { "secondsToReady", Math.Round((DateTime.UtcNow - started).TotalSeconds, 1) },
                        { "staticConstructorFailures", info.StaticConstructorFailures },
                    });
                },
            });

            Page(server, channel, "luna_control", "control", 10000, false,
                "Changes the page: pause / resume / step one frame / speed (engine time scale x0.25-x8) / restart (reload). Pausing and speed " +
                "use the engine's own pause and time scale and are undone by resume and speed 1.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "action", McpServer.Prop("string", "What to do", null, new[] { "pause", "resume", "step", "speed", "restart" }) },
                    { "value", McpServer.Prop("number", "Speed multiplier for action=speed") },
                }, "action"));

            server.Register(new McpServer.Tool
            {
                Name = "luna_screenshot",
                Description = "Image of the playable as it is now (redraws the current state, does not advance the game).",
                ReadOnly = true,
                InputSchema = McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "maxWidth", McpServer.Prop("integer", "Downscale to this width", 540) },
                }),
                Run = args =>
                {
                    var shot = MiniJson.Obj(channel.Send(MiniJson.Str(args, "session"), "screenshot", args, 15000));
                    string data = MiniJson.Str(shot, "base64");
                    if (string.IsNullOrEmpty(data))
                        return McpServer.ToolResult.Text("No image: " + MiniJson.Str(shot, "error", "the page returned nothing"), true);
                    return McpServer.ToolResult.Text($"{MiniJson.Num(shot, "width", 0)}x{MiniJson.Num(shot, "height", 0)}, frame {MiniJson.Num(shot, "frame", 0)}")
                        .WithImage(data, MiniJson.Str(shot, "mimeType", "image/jpeg"));
                },
            });

            Page(server, channel, "luna_watch_field", "watch", 10000, true,
                "Records every assignment of a field with the C# call stack, without running game code: a static field (staticType + field, e.g. " +
                "'Supercent.ArcadeIdle.SimpleTransform.ST' / '_coroutineHelper') or a MonoBehaviour field (path + script + field). Returns a watch " +
                "id; read hits with luna_watch_hits. Use it to find who resets or nulls a value.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "field", McpServer.Prop("string", "C# field name as compiled (private fields keep their name, e.g. _view)") },
                    { "staticType", McpServer.Prop("string", "Full C# type name for a static field") },
                    { "path", McpServer.Prop("string", "GameObject path for an instance field") },
                    { "script", McpServer.Prop("string", "MonoBehaviour class on that object (part of the name)") },
                    { "maxHits", McpServer.Prop("integer", "Hits kept (the first ones)", 30) },
                }, "field"));

            Page(server, channel, "luna_watch_hits", "watchHits", 10000, true,
                "Hits of a watch (frame, new value, C# stack). remove=true stops watching and restores the field.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "id", McpServer.Prop("string", "Watch id from luna_watch_field") },
                    { "remove", McpServer.Prop("boolean", "Stop watching", false) },
                }, "id"));

            Page(server, channel, "luna_eval", "eval", 30000, false,
                "Last resort: runs JavaScript in the page and returns the result (promises are awaited). C# classes are globals by full name " +
                "(e.g. Supercent.BulldozerMaster.Aron.Input.InputManager.Instance), Vector2 is new pc.Vec2(x, y), the engine is " +
                "pc.Application.getApplication(), the Dev Tools agent is window.__lunaDevTools. Calling game code marks it used for Luna's runtime " +
                "analysis: prefer the other tools.",
                McpServer.Schema(new Dictionary<string, object>
                {
                    { "session", session },
                    { "code", McpServer.Prop("string", "Function body; use 'return' for the result") },
                }, "code"));
        }

        static void Page(McpServer server, DevToolsChannel channel, string name, string method, int timeoutMs, bool readOnly,
                         string description, Dictionary<string, object> schema)
        {
            server.Register(new McpServer.Tool
            {
                Name = name,
                Description = description,
                ReadOnly = readOnly,
                InputSchema = schema,
                Run = args => McpServer.ToolResult.Json(channel.Send(MiniJson.Str(args, "session"), method, args, timeoutMs)),
            });
        }

        static class MiniJsonSchema
        {
            public static readonly Dictionary<string, object> Session =
                McpServer.Prop("string", "Page session from luna_status (default: the most recently active page)");
        }
    }
}
