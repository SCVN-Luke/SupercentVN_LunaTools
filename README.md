# SupercentVN Luna Tools

Luna Build Assistant (Tools/Luna/Build Assistant: Build Develop, Build & upload, pre-upload checks,
build size, asset settings), Luna cache cleaner and Playground cookie fix, shared by the luna_*
projects as a git submodule.

## Add to a project

From the repository root (the Unity project is at `unity_project/<project>`):

```sh
git submodule add https://github.com/SCVN-Luke/SupercentVN_LunaTools.git unity_project/<project>/Assets/LunaTools
```

Mount it at `Assets/LunaTools`, not under `Assets/SupercentVN/`. Unity 2022.3 matches script
paths to asmdef folders with a prefix tree: an asmdef folder starting with `Assets/SupercentVN`
next to one under `Assets/Supercent/` (e.g. Toony Colors Pro 2) splits the tree at
`Assets/Supercent`, and every runtime script under `Assets/Supercent/` then belongs to no
assembly ("will not be compiled because it exists outside the Assets folder").

Delete any old copy under `Assets/SupercentVN/Tools/Editor` in the same step, before Unity
refreshes: the scripts keep their GUIDs, and Unity re-GUIDs one side if it sees both copies.
Commit the submodule, `.gitmodules` and `Assets/LunaTools.meta`.

Cloning a project: `git clone --recurse-submodules …`, or `git submodule update --init` afterwards.

## Dev Tools (Build Develop debug panel)

Build Develop → "Open with Dev Tools" (on by default) opens the playable through a local proxy
(`DevToolsProxy`, port 8100+) that adds `Editor/LunaBuildAssistant/DevTools/LunaDevTools.js` to the
page while it loads. Press `` ` `` or the "Dev Tools" pill: frame time (p95/worst), engine and WebGL
draw calls, shader switches, GPU uploads and memory estimate, JS heap and allocation rate; a draw-call
slider and frame capture (Unity Frame Debugger style) plus Spector.js; pause, step, speed ×0.25–×8,
restart, activate a GameObject by name (end card). The phone URL is shown in the Assistant.

- Hot paths: cost of each script callback and coroutine (self / total ms, worst frame, calls made
  inside, engine work such as `Transform.handle` included and the methods behind it); most-called
  methods, project code only by default or grouped by class.
- Errors: console errors and warnings deduplicated with counts, stack frames mapped back to the C#
  method (from the `/*Class.Method start.*/` markers in Luna's compiled scripts); Copy all as text.

It cannot change a build: nothing is written to the build output (the size estimate reads the same
files), the script sits in an Editor folder Luna never exports, call counts come from the `TRACE`
calls Luna already makes (forwarded unchanged, so runtime analysis and code stripping see the same
calls), and the engine's time scale / pause are only touched after a button press.

## MCP server for AI agents

Build Develop → "MCP server for AI agents" serves an MCP endpoint (Streamable HTTP) from the Dev Tools
proxy, to this machine only: `http://localhost:8100/mcp` (next free port if taken). It starts again with
Unity while enabled. Register it once ("Copy Claude Code command" in the Assistant):

```sh
claude mcp add --transport http luna-devtools http://localhost:8100/mcp
```

Pages opened through the Dev Tools connect as sessions (desktop tabs, phones on the LAN) by
long-polling `/__luna_devtools/channel`; they reconnect after a Unity reload.

- Page: `luna_status`, `luna_get_stats`, `luna_get_errors` (deduplicated, C# stack), `luna_record_hot_paths`,
  `luna_capture_frame`, `luna_find_objects`, `luna_get_object`, `luna_watch_field` / `luna_watch_hits`
  (who assigns a field, with C# stack), `luna_screenshot`, `luna_control` (pause / step / speed /
  restart), `luna_eval`.
- Editor: `unity_build_develop` (waits, returns errors / warnings), `unity_build_report`,
  `unity_luna_checks`, `unity_find_source` (C# name → file:line + code), `unity_open_source`.

Reading tools use engine data and raw field values only (no C# method or getter runs), so Luna's
runtime analysis is unchanged; `luna_control` and `luna_eval` can run game code and say so.
The protocol, channel and JSON code (`Mcp/`, `DevToolsProxy`) use no Unity API and are tested outside
the Editor.

## Changing the tool

Edit it inside any project (`Assets/LunaTools`), then commit and push **in the
submodule**, and commit the new submodule pointer in that project. Other projects get the change
with `git submodule update --remote Assets/LunaTools` (then commit their pointer).
Keep fixes generic: a problem met in one project usually hits the others too.

The Editor asmdef stays `autoReferenced: false`: its `Supercent.Util.Editor` namespace would
otherwise break `: Editor` base classes in project code.
