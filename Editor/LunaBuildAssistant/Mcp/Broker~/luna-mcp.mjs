#!/usr/bin/env node
// luna-mcp-broker version: 3
//
// MCP server (stdio) for AI agents, in front of the Luna Build Assistant's MCP endpoint in each open Unity Editor.
// The agent starts it, so its tools exist even before Unity is open, and every call goes to the Editor of the
// agent's project while several Luna projects are open side by side. The project is found from LUNA_PROJECT, else the
// folders the agent shares as MCP roots (Cursor, VS Code ... start servers outside the project), else the working directory.
//
// Editors publish themselves in ~/.supercent/luna-mcp (see LunaMachine.cs / LunaInstance.cs):
//   instances/*.json  one per running Editor: projectPath, pid, mcpUrl, state
//   tools.json        the last tool list, used while no Editor of the project is open
// Installed there as luna-mcp.mjs by the Build Assistant (newest version wins). Node 18+, no packages.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const VERSION = '1.1.0';
const STALL_SECONDS = 15;          // No heartbeat for this long: the Editor's main thread is blocked.
const ROOT = path.join(os.homedir(), '.supercent', 'luna-mcp');
const INSTANCES = path.join(ROOT, 'instances');
const TOOLS_FILE = path.join(ROOT, 'tools.json');
const PORTS_FILE = path.join(ROOT, 'ports.json');
const PROTOCOLS = ['2025-06-18', '2025-03-26', '2024-11-05'];
const WAIT_FOR_EDITOR_MS = 60000;     // Domain reloads / compiles: the Editor's endpoint comes back by itself.
const CALL_TIMEOUT_MS = 10 * 60000;
const WATCH_MS = 3000;
const BROKER_TOOL = 'luna_instances';
const CASE_INSENSITIVE = process.platform === 'darwin' || process.platform === 'win32';

const BROKER_INSTRUCTIONS =
  'Calls go to the Unity Editor that has this agent\'s project open (found from the working directory), also with several ' +
  'Luna projects open at once; pass "project" (path or folder name) to target another one, and call luna_instances to ' +
  'see the open Editors. When the project\'s Editor is not open (or still loading / reloading scripts), tools say so: ' +
  'ask the user to open it in Unity, then call again (no reconnect needed).';

// ---- Paths and Editors ----

function norm(p) {
  let full = path.resolve(p);
  try { full = fs.realpathSync.native(full); } catch { /* keep as is */ }
  full = full.replace(/\\/g, '/').replace(/\/+$/, '');
  return full;
}

const same = (a, b) => (CASE_INSENSITIVE ? a.toLowerCase() === b.toLowerCase() : a === b);
const inside = (child, parent) => (CASE_INSENSITIVE ? child.toLowerCase() : child).startsWith((CASE_INSENSITIVE ? parent.toLowerCase() : parent) + '/');

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

function alive(pid) {
  if (!pid) return false;
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}

function instances() {
  let files = [];
  try { files = fs.readdirSync(INSTANCES).filter((f) => f.endsWith('.json')); } catch { return []; }
  const list = [];
  for (const f of files) {
    const data = readJson(path.join(INSTANCES, f));
    if (!data || !data.projectPath) continue;
    if (!alive(data.pid)) {
      try { fs.unlinkSync(path.join(INSTANCES, f)); } catch { /* another broker removed it */ }
      continue;
    }
    data.projectPath = norm(data.projectPath);
    list.push(data);
  }
  return list;
}

// LUNA_PROJECT from the agent's config; ignored when the agent left a variable such as ${workspaceFolder} unresolved.
const envProject = (() => {
  const value = (process.env.LUNA_PROJECT || '').trim();
  if (!value || value.includes('${')) return null;
  try { return fs.statSync(value).isDirectory() ? norm(value) : null; } catch { return null; }
})();
let rootDirs = [];      // From the agent's MCP roots (roots/list), when it shares them.

// Where this agent works: the folders whose Unity project calls go to by default.
function workDirs() {
  if (envProject) return [envProject];
  if (rootDirs.length > 0) return rootDirs;
  return [norm(process.cwd())];
}

// Unity projects at or under a working directory (a repository usually holds one under unity_project/).
function unityProjectsHere(workDir) {
  const found = [];
  const isProject = (dir) => fs.existsSync(path.join(dir, 'ProjectSettings', 'ProjectVersion.txt'));
  const scan = (dir, depth) => {
    if (isProject(dir)) { found.push(norm(dir)); return; }
    if (depth === 0) return;
    let entries = [];
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries)
      if (e.isDirectory() && !e.name.startsWith('.') && !['Library', 'node_modules', 'Assets', 'Temp'].includes(e.name))
        scan(path.join(dir, e.name), depth - 1);
  };
  // The working directory may also be inside a project (e.g. its Assets folder).
  for (let dir = workDir; dir && dir !== path.dirname(dir); dir = path.dirname(dir))
    if (isProject(dir)) return [norm(dir)];
  scan(workDir, 2);
  return found;
}

// The Editor a call goes to: { instance } or { error }.
function resolve(project) {
  const open = instances();
  if (project && String(project).trim()) {
    const wanted = String(project).trim();
    const asPath = /[\\/]/.test(wanted) ? norm(wanted) : null;
    const matches = open.filter((i) => asPath
      ? same(i.projectPath, asPath) || inside(i.projectPath, asPath)
      : same(path.basename(i.projectPath), wanted) || i.projectPath.toLowerCase().includes(wanted.toLowerCase()));
    if (matches.length === 1) return { instance: matches[0] };
    if (matches.length > 1)
      return { error: `Several open Editors match "${wanted}": ${matches.map((m) => m.projectPath).join(', ')}. Pass the full path.` };
    return { error: `No open Unity Editor has "${wanted}" open. ${describeOpen(open)}` };
  }
  const dirs = workDirs();
  for (const workDir of dirs) {
    const here = open.filter((i) => same(i.projectPath, workDir) || inside(i.projectPath, workDir) || inside(workDir, i.projectPath));
    if (here.length === 1) return { instance: here[0] };
    if (here.length > 1)
      return { error: `Several open Editors are under ${workDir}: ${here.map((m) => m.projectPath).join(', ')}. Pass "project".` };
  }
  const local = dirs.flatMap(unityProjectsHere);
  if (local.length > 0)
    return { error: `The Unity Editor of ${local.join(' / ')} is not open (or still loading). Ask the user to open it in Unity ` +
                    `(Luna Build Assistant in Assets/SupercentVN/LunaTools), then call again. ${describeOpen(open)}` };
  if (open.length === 1) return { instance: open[0] };
  return { error: `No Unity project in ${dirs.join(', ')}. ${describeOpen(open)} Pass "project".` };
}

function describeOpen(open) {
  return open.length === 0 ? 'No Unity Editor with the Luna Build Assistant is open.'
    : 'Open: ' + open.map((i) => `${path.basename(i.projectPath)} (${i.projectPath}, ${i.state})`).join('; ') + '.';
}

// ---- Calls to an Editor ----

let nextId = 1;

async function post(url, method, params, timeoutMs) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: nextId++, method, params }),
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (res.status === 404) throw Object.assign(new Error('MCP server is off in that Editor'), { off: true });
  if (!res.ok) throw new Error(`HTTP ${res.status} from ${url}`);
  const body = await res.json();
  if (body.error) throw new Error(body.error.message || 'MCP error');
  return body.result;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Forwards one tool call, waiting while the Editor reloads its scripts (its endpoint is down for a few seconds).
async function forward(name, args) {
  const deadline = Date.now() + WAIT_FOR_EDITOR_MS;
  let last = null;
  for (;;) {
    const target = resolve(args.project);
    if (target.error) return errorResult(target.error);
    const inst = target.instance;
    if (inst.state === 'off' || (inst.mcpUrl == null && inst.state !== 'reloading' && inst.state !== 'compiling'))
      return errorResult(`The MCP server is off in the Editor of ${inst.projectPath}: turn on "MCP server for AI agents" in ` +
                         'Tools/Luna/Build Assistant > Build Develop.' + (inst.note ? ' ' + inst.note : ''));
    if (inst.mcpUrl) {
      try {
        const result = await post(inst.mcpUrl, 'tools/call', { name, arguments: { ...args, project: inst.projectPath } }, CALL_TIMEOUT_MS);
        const stalled = stalledSeconds(inst.projectPath);
        if (result?.isError && stalled >= STALL_SECONDS)
          result.content = [...(result.content || []), { type: 'text', text:
            `Unity has not updated for ${stalled} s: its main thread is blocked, most likely by a dialog waiting in the Editor ` +
            '(e.g. save a scene, a plugin popup). Ask the user to answer it in Unity; agents cannot.' }];
        return result;
      } catch (e) {
        if (e.off) return errorResult(`The MCP server is off in the Editor of ${inst.projectPath}.`);
        if (e.name === 'TimeoutError') return errorResult(`${name} got no answer in time from the Editor of ${inst.projectPath}.`);
        last = e;     // Connection refused: reloading scripts, or the port is changing.
      }
    }
    if (Date.now() > deadline)
      return errorResult(`The Editor of ${inst.projectPath} does not answer (state: ${inst.state}` +
                         (last ? `, ${last.cause?.code || last.message}` : '') + '). It may be compiling, importing or showing a dialog: ' +
                         'call again in a moment, or ask the user to check Unity.');
    await sleep(1000);
  }
}

// Seconds since the Editor last wrote its instance file (it does every few seconds from its main thread).
function stalledSeconds(projectPath) {
  const inst = instances().find((i) => same(i.projectPath, projectPath));
  const at = inst?.updatedAt ? Date.parse(inst.updatedAt) : NaN;
  return Number.isNaN(at) ? 0 : Math.round((Date.now() - at) / 1000);
}

const errorResult = (text) => ({ content: [{ type: 'text', text }], isError: true });

// ---- Tools ----

const brokerTool = {
  name: BROKER_TOOL,
  description: 'Unity Editors open on this machine with the Luna Build Assistant (project, state, MCP / Dev Tools / develop ' +
               'server addresses), the one this agent\'s calls go to by default, and the ports each project keeps.',
  inputSchema: { type: 'object', properties: {} },
  annotations: { readOnlyHint: true, openWorldHint: false },
};

function cachedTools() {
  const data = readJson(TOOLS_FILE);
  return { tools: Array.isArray(data?.tools) ? data.tools : [], instructions: data?.instructions || '' };
}

async function listTools() {
  const target = resolve(null);
  let tools = null;
  if (target.instance?.mcpUrl) {
    try { tools = (await post(target.instance.mcpUrl, 'tools/list', {}, 3000)).tools; } catch { /* use the cache */ }
  }
  tools = tools || cachedTools().tools;
  return [brokerTool, ...tools.filter((t) => t.name !== BROKER_TOOL)];
}

function listInstances() {
  const open = instances();
  const target = resolve(null);
  const ports = readJson(PORTS_FILE)?.projects || {};
  return {
    content: [{
      type: 'text', text: JSON.stringify({
        workingDirectory: workDirs().join(', '),
        workingDirectoryFrom: envProject ? 'LUNA_PROJECT' : rootDirs.length > 0 ? 'MCP roots' : 'process working directory',
        defaultProject: target.instance ? target.instance.projectPath : null,
        defaultProblem: target.error || null,
        instances: open.map((i) => ({
          project: path.basename(i.projectPath), projectPath: i.projectPath, state: i.state, unity: i.unity, pid: i.pid,
          mcpUrl: i.mcpUrl, devToolsUrl: i.devToolsUrl, developServerPort: i.developServerPort, note: i.note,
          secondsSinceUpdate: stalledSeconds(i.projectPath),
        })),
        projectPorts: ports,
        broker: VERSION,
      }, null, 2),
    }],
  };
}

// ---- stdio JSON-RPC ----

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

// ---- MCP roots: the folders the agent works in ----

let clientRoots = false;
let rootsRequest = 0;

function askRoots() {
  if (clientRoots && !envProject) send({ jsonrpc: '2.0', id: `luna-roots-${++rootsRequest}`, method: 'roots/list' });
}

function takeRoots(result) {
  const dirs = [];
  for (const root of Array.isArray(result?.roots) ? result.roots : []) {
    try { if (String(root.uri).startsWith('file:')) dirs.push(norm(fileURLToPath(root.uri))); } catch { /* not a local folder */ }
  }
  rootDirs = dirs;
}

async function handle(msg) {
  if (!msg || typeof msg !== 'object') return;
  const { id, method, params } = msg;
  if (method === undefined && id !== undefined) {         // The agent's answer to roots/list.
    if (String(id).startsWith('luna-roots-')) takeRoots(msg.result);
    return;
  }
  if (id === undefined || id === null) {
    if (method === 'notifications/initialized') { initialized = true; askRoots(); }
    else if (method === 'notifications/roots/list_changed') askRoots();
    return;
  }
  try {
    switch (method) {
      case 'initialize': {
        const asked = params?.protocolVersion;
        clientRoots = !!params?.capabilities?.roots;
        const cached = cachedTools().instructions;
        send({ jsonrpc: '2.0', id, result: {
          protocolVersion: PROTOCOLS.includes(asked) ? asked : PROTOCOLS[0],
          capabilities: { tools: { listChanged: true } },
          serverInfo: { name: 'luna-devtools', version: VERSION },
          instructions: (cached ? cached + '\n' : '') + BROKER_INSTRUCTIONS,
        } });
        return;
      }
      case 'ping':
        send({ jsonrpc: '2.0', id, result: {} });
        return;
      case 'tools/list':
        send({ jsonrpc: '2.0', id, result: { tools: await listTools() } });
        return;
      case 'tools/call': {
        const name = params?.name;
        const args = params?.arguments && typeof params.arguments === 'object' ? params.arguments : {};
        const result = name === BROKER_TOOL ? listInstances() : await forward(name, args);
        send({ jsonrpc: '2.0', id, result });
        return;
      }
      default:
        send({ jsonrpc: '2.0', id, error: { code: -32601, message: 'Method not found: ' + method } });
    }
  } catch (e) {
    send({ jsonrpc: '2.0', id, error: { code: -32603, message: String(e?.message || e) } });
  }
}

// Tell the agent when the tool list may have changed (an Editor opened, or a newer Build Assistant published its tools).
let initialized = false;
let signature = null;
function watch() {
  let sig = '';
  try { sig = String(fs.statSync(TOOLS_FILE).mtimeMs); } catch { /* none yet */ }
  sig += '|' + (resolve(null).instance?.projectPath || '');
  if (signature !== null && sig !== signature && initialized)
    send({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' });
  signature = sig;
}

if (typeof fetch !== 'function') {
  process.stderr.write('luna-mcp: Node 18 or newer is required (fetch).\n');
  process.exit(1);
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch {
    send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } });
    return;
  }
  for (const m of Array.isArray(msg) ? msg : [msg]) handle(m);
});
rl.on('close', () => process.exit(0));
watch();
setInterval(watch, WATCH_MS).unref();
