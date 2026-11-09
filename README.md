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

Build Develop → "MCP server for AI agents" (on by default) serves an MCP endpoint (Streamable HTTP)
from the Dev Tools proxy, to this machine only. It starts with Unity, before any Build Develop, and
again after every script reload.

### Several projects on one machine

Each project keeps its own ports on this machine (`~/.supercent/luna-mcp/ports.json`): the Dev Tools
/ MCP proxy (8100+) and the plugin's develop server (8000+, written to the plugin's per-project
setting). A port another project keeps is never taken, so several Luna projects open at once each
keep the same addresses across restarts. When a port has to change, the Assistant says so under the
MCP line (restart the develop server if it already ran on the old port). The proxy also checks that
the develop server serves this project (luna.json `projectId`) and shows a page saying so if another
project's server answers on the port.

Each running Editor writes `~/.supercent/luna-mcp/instances/<hash>.json` (project, pid, MCP URL,
state: ready / compiling / building / uploading / reloading / off) every few seconds, and deletes it
when Unity quits. The last tool list is kept in `tools.json`.

### Registering the agent (once per machine)

Agents start the broker `~/.supercent/luna-mcp/luna-mcp.mjs` (Node 18+, no packages; installed and
updated there by the Assistant from `Editor/LunaBuildAssistant/Mcp/Broker~`, newest version wins).

Build Develop → "AI agents" lists the agents found on this machine and whether each has the broker;
**Auto configure** writes the `luna-devtools` entry into each one's user-level MCP config
(`Mcp/LunaAgents.cs`), with node's full path (agents started from the Dock do not see the shell's PATH):

| Agent | Config written |
| --- | --- |
| Claude Code | `claude mcp add -s user` (its CLI owns `~/.claude.json`) |
| Codex (CLI, IDE extension, app) | `~/.codex/config.toml` → `[mcp_servers.luna-devtools]` (`$CODEX_HOME` if set) |
| Cursor | `~/.cursor/mcp.json` → `mcpServers` (+ `LUNA_PROJECT=${workspaceFolder}`) |
| Antigravity | `~/.gemini/antigravity/mcp_config.json` → `mcpServers` |
| Gemini CLI | `~/.gemini/settings.json` → `mcpServers` |
| Windsurf | `~/.codeium/windsurf/mcp_config.json` → `mcpServers` |
| VS Code (Copilot agent mode) | `<user dir>/Code/User/mcp.json` → `servers` (+ `LUNA_PROJECT=${workspaceFolder}`) |

Only that entry changes (Codex: only its `command` / `args`, tool approvals kept); the rest of each file
is kept, and the file is copied to `<file>.luna-backup` before its first change. A file with comments
(not plain JSON) is left alone and the window shows the entry to add by hand. Restart the agent (or
reload its MCP servers) afterwards. Daily use so far: Claude Code; the other agents are configured the
same way but less tested.

By hand, Claude Code: "Copy Claude Code command" copies

```sh
claude mcp add -s user luna-devtools -- /opt/homebrew/bin/node "$HOME/.supercent/luna-mcp/luna-mcp.mjs"
```

Remove an older per-project registration first: `claude mcp remove luna-devtools -s local`.

The broker runs with the agent, so its tools exist even before Unity is open. Each call goes to the
Editor whose project is at or under the agent's folder (or `project`: path or folder name;
`luna_instances` lists the open Editors and the ports each project keeps). The agent's folder is
`LUNA_PROJECT` when set (ignored while it still holds an unresolved `${...}`), else the folders the agent
shares as MCP roots (agents such as VS Code start servers outside the project), else the working directory.
While that Editor is not
open, the call says so; while it reloads scripts, the broker waits for it (up to 60 s); when its main
thread stops updating (a dialog waiting in Unity), the error says so. Every Editor tool also takes
`project` and refuses calls meant for another project, so a call never builds or uploads the wrong one.

Without the broker, the Editor's endpoint still answers directly: `luna_status` and `luna_instances`
show its URL (`http://localhost:<port>/mcp`).

### Tools

Pages opened through the Dev Tools connect as sessions (desktop tabs, phones on the LAN) by
long-polling `/__luna_devtools/channel`; they reconnect after a Unity reload. A tab keeps its session
across reloads; tools default to the most recently loaded page.

- Page: `luna_status`, `luna_get_stats`, `luna_get_errors` (deduplicated, C# stack, and static
  constructors that threw while Luna started, which Luna hides), `luna_record_hot_paths`,
  `luna_capture_frame`, `luna_find_objects`, `luna_get_object`, `luna_watch_field` / `luna_watch_hits`
  (who assigns a field, with C# stack), `luna_screenshot`, `luna_reload` (fresh start, waits for the
  engine), `luna_simulate` (touch / drag steps played frame by frame with a fixed delta, through the
  engine's input; same result in a hidden tab), `luna_control` (pause / step / speed / restart), `luna_eval`.
- Editor: `unity_compile` (imports changed scripts, C# errors with file:line), `unity_build_develop`
  (starts the build and returns; poll `unity_build_report`, which keeps the result across a Unity
  reload), `unity_luna_checks`, `unity_find_source` (C# name → file:line + code), `unity_open_source`.
- Upload: `unity_upload_targets` (sign-in, applications, concepts, the concept picked for the branch, a
  suggested new concept name, pre-upload warnings) and `unity_build_upload` (the window's Build &
  Upload without its dialog: refused while building / compiling, with unsaved Luna scenes, when not
  signed in, or when a pre-upload check warns unless `force`; poll `unity_build_report` → `upload`).
  Agents upload only when the user asks. Playground sign-in stays in the Unity Playworks Plugin.
- Size: `unity_build_size` (the Build size page for any network: total and change, size per kind, the
  largest assets with their luna.json rule and source facts, biggest changes, duplicates; `estimate`
  re-runs the plugin's breakdown over the last build), `unity_asset_size_try` (a texture or sound with
  other settings, encoded with Luna's own tools without applying; `compare` tries Luna's usual
  alternatives, `images` returns the encoded images to judge quality) and `unity_asset_settings_apply`
  (writes an asset or folder rule into luna.json, like Apply in the window; only after the user agreed;
  `reset` drops the path's own rule; `asset: "default"` edits the kind's default rule; `keepCurrent` gives a path
  its own rule with its current settings, so a later folder or default change leaves it as it is).
- Project: `unity_console` (Editor Console, newest first, duplicates collapsed, optional stacks),
  `unity_asset_references` (why an asset ships: who references it, with GameObject / component / property and
  prefab overrides, and reference chains up to a Luna scene or force-include), `unity_luna_asset_lists`
  (force-include / force-exclude / excluded scripts in luna.json; add and remove only after the user agreed).
- Fonts (TextMeshPro, compiled when the project has it: `LUNA_TMP`): `unity_font_check` (each font asset texts use:
  static / dynamic, atlas, point size, padding, characters, source font and whether it is force-excluded, materials,
  characters texts show that the atlas lacks, texts whose material samples another atlas) and
  `unity_font_atlas_rebuild` (rebuilds a static atlas in place with more characters at the largest point size that
  fits, keeping the padding / point ratio and every reference; updates the materials' gradient scale).

Agent builds and uploads never save scenes: with a modified Luna scene they are refused, because
saving would show a dialog that stops the Editor until someone answers it.

Reading tools use engine data and raw field values only (no C# method or getter runs), so Luna's
runtime analysis is unchanged; `luna_control`, `luna_simulate` and `luna_eval` run game code and say so.
Static initializers are wrapped to report their exceptions (called through unchanged, rethrown).
The protocol, channel and JSON code (`Mcp/`, `DevToolsProxy`, `LunaMachine`) use no Unity API and are
tested outside the Editor.

## Changing the tool

Edit it inside any project (`Assets/LunaTools`), then commit and push **in the
submodule**, and commit the new submodule pointer in that project. Other projects get the change
with `git submodule update --remote Assets/LunaTools` (then commit their pointer).
Keep fixes generic: a problem met in one project usually hits the others too.

The Editor asmdef stays `autoReferenced: false`: its `Supercent.Util.Editor` namespace would
otherwise break `: Editor` base classes in project code.
