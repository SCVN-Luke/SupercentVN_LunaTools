/*
 * Luna Dev Tools: debug overlay for the Build Develop page.
 *
 * Served only by the Luna Build Assistant's local proxy (DevToolsProxy.cs), which adds it to the HTML on the way
 * to the browser. It lives in an Editor folder, which Luna never exports, and nothing is written to the build
 * output, so the uploaded playable, luna.json and the size estimate are unaffected.
 *
 *   role=agent  the playable page (iframe.html): collects data. Installed before any Luna script.
 *   role=shell  the Dev Environment page: shows the panel and reads the agent inside its iframe.
 *   A playable opened directly (e.g. on a phone) shows the panel itself.
 *
 * Rules that keep Luna's runtime analysis (which decides what code the cloud build strips) exactly as without it:
 *   - no game function is wrapped or replaced; call counts come from the TRACE hook Luna already calls,
 *     forwarded unchanged with the same arguments and return value;
 *   - the engine's time scale / pause are only touched after a button press, and restored afterwards;
 *   - WebGL calls are wrapped on the context only to count them; a draw is skipped only while the
 *     "draw limit" slider is set.
 */
(function () {
    'use strict';

    var src = (document.currentScript && document.currentScript.src) || '';
    var role = /[?&]role=shell\b/.test(src) ? 'shell' : 'agent';
    if (window.__lunaDevTools)
        return;

    var realNow = performance.now.bind(performance);
    var nativeRaf = window.requestAnimationFrame.bind(window);
    var STORE = 'lunaDevTools.';

    function load(key, fallback) {
        try {
            var v = localStorage.getItem(STORE + key);
            return v === null ? fallback : JSON.parse(v);
        } catch (e) {
            return fallback;
        }
    }

    function save(key, value) {
        try {
            localStorage.setItem(STORE + key, JSON.stringify(value));
        } catch (e) { /* Storage blocked: settings just don't persist. */ }
    }

    // Fixed-size history of numbers.
    function Ring(size) {
        this.data = new Float64Array(size);
        this.size = size;
        this.count = 0;
        this.next = 0;
    }
    Ring.prototype.push = function (v) {
        this.data[this.next] = v;
        this.next = (this.next + 1) % this.size;
        if (this.count < this.size) this.count++;
    };
    Ring.prototype.values = function () {
        var out = new Array(this.count);
        var start = (this.next - this.count + this.size) % this.size;
        for (var i = 0; i < this.count; i++) out[i] = this.data[(start + i) % this.size];
        return out;
    };
    Ring.prototype.stats = function () {
        var v = this.values();
        if (!v.length) return { avg: 0, p95: 0, max: 0, last: 0 };
        var sum = 0, max = 0;
        for (var i = 0; i < v.length; i++) { sum += v[i]; if (v[i] > max) max = v[i]; }
        var sorted = v.slice().sort(function (a, b) { return a - b; });
        return { avg: sum / v.length, p95: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))], max: max, last: v[v.length - 1] };
    };

    // =====================================================================================================
    // Agent: runs inside the playable page.
    // =====================================================================================================
    function createAgent(win) {
        var agent = { errors: [] };

        function fail(where, e) {
            if (agent.errors.length < 20) agent.errors.push(where + ': ' + (e && e.message || e));
        }

        // ---------- Engine application ----------
        var app = null, appTriedAt = -1e9;

        function getApp() {
            if (app) return app;
            var t = realNow();
            if (t - appTriedAt < 250) return null;
            appTriedAt = t;
            try {
                var pc = win.pc;
                var a = pc && pc.Application && pc.Application.getApplication && pc.Application.getApplication();
                if (a && a.counters && typeof a.frame === 'number') app = a;
            } catch (e) { /* Not created yet. */ }
            return app;
        }

        agent.getApp = getApp;

        // ---------- Hot paths: count TRACE("Class#method", this) calls ----------
        // Luna's develop compiler starts every method with `if (TRACE) { TRACE("Class#method", this); }`.
        // The accessor keeps `TRACE` exactly what Luna's runtime analysis sets; it only counts before forwarding.
        var hot = { recording: false, current: new Map(), total: new Map(), peak: new Map(), frames: 0, installed: false, seq: 0 };
        // The last few traced names (always, it is one array write): names the coroutine a script just started.
        var recent = new Array(16), recentAt = 0;
        try {
            var traceTarget = win.TRACE;
            var traceProxy = function (name, self) {
                if (typeof name === 'string') {
                    recent[recentAt = (recentAt + 1) & 15] = name;
                    if (hot.recording) {
                        hot.current.set(name, (hot.current.get(name) || 0) + 1);
                        hot.seq++;
                        if (scripts.stack.length) scopeTrace(name);
                    }
                }
                return traceTarget(name, self);
            };
            Object.defineProperty(win, 'TRACE', {
                configurable: true,
                enumerable: true,
                get: function () { return traceTarget ? traceProxy : traceTarget; },
                set: function (v) { traceTarget = v; }
            });
            hot.installed = true;
        } catch (e) {
            fail('TRACE hook', e);
        }

        function hotEndFrame() {
            if (!hot.recording) return;
            if (hot.current.size === 0 && !scripts.frameDirty) return;
            hot.frames++;
            hot.current.forEach(function (count, name) {
                hot.total.set(name, (hot.total.get(name) || 0) + count);
                if (count > (hot.peak.get(name) || 0)) hot.peak.set(name, count);
            });
            hot.current.clear();
            scriptsEndFrame();
        }

        // Luna's own runtime (compiled C# of UnityEngine, System, ...) next to the project's code.
        var ENGINE_NAME = /^(UnityEngine|UnityEditor|System|Bridge|TMPro|DG\.Tweening|Newtonsoft|Luna\.|LunaUnity|Unity\.|Spine|Cinemachine|Microsoft)\b/;
        function isProject(name) { return !ENGINE_NAME.test(name); }
        function classOf(name) { var i = name.indexOf('#'); return i < 0 ? name : name.substring(0, i); }

        agent.hot = {
            available: function () { return hot.installed && !!traceTarget; },
            recording: function () { return hot.recording; },
            setRecording: function (on) {
                hot.recording = !!on;
                if (!on) { hot.current.clear(); scripts.stack.length = 0; }
            },
            reset: function () { hot.current.clear(); hot.total.clear(); hot.peak.clear(); hot.frames = 0; scriptsReset(); },
            frames: function () { return hot.frames; },
            isProject: isProject,
            // filter: 'all' | 'project'; byClass: sum the methods of each class.
            top: function (n, filter, byClass) {
                var rows = [], groups = byClass ? new Map() : null;
                hot.total.forEach(function (total, name) {
                    if (filter === 'project' && !isProject(name)) return;
                    if (groups) {
                        var key = classOf(name), g = groups.get(key);
                        if (!g) groups.set(key, g = { name: key, total: 0, peak: 0, methods: 0 });
                        g.total += total;
                        g.peak = Math.max(g.peak, hot.peak.get(name) || 0);
                        g.methods++;
                        return;
                    }
                    rows.push({ name: name, total: total, perFrame: hot.frames ? total / hot.frames : 0, peak: hot.peak.get(name) || 0 });
                });
                if (groups) groups.forEach(function (g) { g.perFrame = hot.frames ? g.total / hot.frames : 0; rows.push(g); });
                rows.sort(function (a, b) { return b.total - a.total; });
                return rows.slice(0, n);
            }
        };

        // ---------- Scripts: cost of each MonoBehaviour callback and coroutine ----------
        // The engine calls Awake/Start/OnEnable/Update/FixedUpdate/LateUpdate through its MonoBehaviour component
        // (onAwake, onUpdate, ...) and steps coroutines with coroutine.Update(dt). Those are the engine's own JS
        // functions (no TRACE, not project code), wrapped once: while recording, each call opens a scope that collects
        // its time and every traced call made inside it (engine calls included), so engine work lands on the script
        // that asked for it.
        var CALLBACKS = { onAwake: 'Awake', onStart: 'Start', onEnable: 'OnEnable', onDisable: 'OnDisable',
            onUpdate: 'Update', onFixedUpdate: 'FixedUpdate', onPostUpdate: 'LateUpdate' };
        var COROUTINE_STARTS = ['addCoroutine', 'addFixedUpdateCoroutine', 'addEndOfFrameCoroutine'];
        var scripts = { installed: false, triedAt: -1e9, stack: [], stats: new Map(), frameDirty: false, note: 'Waiting for the first script…' };

        function scriptStat(key) {
            var st = scripts.stats.get(key);
            if (!st) scripts.stats.set(key, st = { key: key, self: 0, total: 0, calls: 0, traces: 0, frameSelf: 0, worst: 0, inner: new Map(), objects: [] });
            return st;
        }

        function scopeEnter(key, entity) {
            var st = scriptStat(key);
            if (entity && st.objects.length < 6 && entity.name && st.objects.indexOf(entity.name) < 0) st.objects.push(entity.name);
            scripts.stack.push({ st: st, t0: realNow(), child: 0, seq: hot.seq });
            scripts.frameDirty = true;
        }

        function scopeExit() {
            var s = scripts.stack.pop();
            if (!s) return;
            var dt = realNow() - s.t0;
            s.st.total += dt;
            s.st.self += dt - s.child;
            s.st.frameSelf += dt - s.child;
            s.st.calls++;
            s.st.traces += hot.seq - s.seq;
            var parent = scripts.stack[scripts.stack.length - 1];
            if (parent) parent.child += dt;
        }

        function scopeTrace(name) {
            var inner = scripts.stack[scripts.stack.length - 1].st.inner;
            inner.set(name, (inner.get(name) || 0) + 1);
        }

        function scriptsEndFrame() {
            scripts.stats.forEach(function (st) {
                if (st.frameSelf > st.worst) st.worst = st.frameSelf;
                st.frameSelf = 0;
            });
            scripts.frameDirty = false;
        }

        function scriptsReset() {
            scripts.stats.clear();
            scripts.stack.length = 0;
            scripts.frameDirty = false;
        }

        function scriptName(code) {
            var full = code && (code.$$fullname || (code.constructor && code.constructor.$$fullname)) || '?';
            return full.substring(full.lastIndexOf('.') + 1);
        }

        // A coroutine is named after the project method traced just before the engine received it: Co_X() runs its
        // TRACE when called, then StartCoroutine hands the enumerator over.
        function coroutineName(code) {
            for (var i = 0; i < 16; i++) {
                var name = recent[(recentAt - i + 16) & 15];
                if (name && isProject(name)) return name.replace('#', '.').replace(/^.*\.([^.]+\.[^.]+)$/, '$1');
            }
            return scriptName(code) + '.coroutine';
        }

        function wrapCoroutine(co, component) {
            if (!co || typeof co.Update !== 'function' || co.__lunaDevTools) return;
            var update = co.Update, key = coroutineName(component.code) + ' (coroutine)', entity = component.entity;
            co.__lunaDevTools = true;
            co.Update = function () {
                if (!hot.recording) return update.apply(this, arguments);
                scopeEnter(key, entity);
                try { return update.apply(this, arguments); } finally { scopeExit(); }
            };
        }

        function patchComponentPrototype(proto) {
            Object.keys(CALLBACKS).forEach(function (method) {
                var original = proto[method];
                if (typeof original !== 'function') return;
                var label = CALLBACKS[method];
                proto[method] = function () {
                    if (!hot.recording || !this.code) return original.apply(this, arguments);
                    scopeEnter(scriptName(this.code) + '.' + label, this.entity);
                    try { return original.apply(this, arguments); } finally { scopeExit(); }
                };
            });
            COROUTINE_STARTS.forEach(function (method) {
                var original = proto[method];
                if (typeof original !== 'function') return;
                proto[method] = function (co) {
                    try { wrapCoroutine(co, this); } catch (e) { fail('coroutine hook', e); }
                    return original.apply(this, arguments);
                };
            });
        }

        function isScriptComponent(c) { return c && c.code && typeof c.onUpdate === 'function' && typeof c.addCoroutine === 'function'; }

        // Finds the engine's MonoBehaviour component class from any live script and patches it once.
        function installScriptHooks(a) {
            if (scripts.installed || !a) return;
            var t = realNow();
            if (t - scripts.triedAt < 500) return;
            scripts.triedAt = t;
            try {
                var found = null, queue = [a.root], seen = 0;
                while (queue.length && !found && seen < 4000) {
                    var node = queue.shift();
                    seen++;
                    if (!node) continue;
                    var comps = typeof node.collectAllUnityComponents === 'function' ? node.collectAllUnityComponents() : null;
                    if (comps) for (var i = 0; i < comps.length && !found; i++) if (isScriptComponent(comps[i])) found = comps[i];
                    if (node.children) for (var c = 0; c < node.children.length; c++) queue.push(node.children[c]);
                }
                if (!found) { scripts.note = 'No MonoBehaviour found yet'; return; }
                patchComponentPrototype(Object.getPrototypeOf(found));
                scripts.installed = true;
                scripts.note = null;
            } catch (e) {
                scripts.note = 'Script hooks unavailable: ' + (e && e.message || e);
                fail('script hooks', e);
            }
        }

        agent.scripts = {
            installed: function () { return scripts.installed; },
            note: function () { return scripts.note; },
            // Rows by self time per recorded frame; inner: the most called methods inside each scope.
            top: function (n, innerCount) {
                var frames = Math.max(1, hot.frames), rows = [];
                scripts.stats.forEach(function (st) {
                    var inner = [];
                    st.inner.forEach(function (count, name) { inner.push({ name: name, perFrame: count / frames, project: isProject(name) }); });
                    inner.sort(function (x, y) { return y.perFrame - x.perFrame; });
                    rows.push({
                        name: st.key, selfMs: st.self / frames, totalMs: st.total / frames, worstMs: st.worst,
                        callsPerFrame: st.calls / frames, tracesPerFrame: st.traces / frames,
                        objects: st.objects.slice(), inner: inner.slice(0, innerCount || 8)
                    });
                });
                rows.sort(function (x, y) { return y.selfMs - x.selfMs; });
                return rows.slice(0, n);
            }
        };

        // ---------- Console: errors and warnings, deduplicated, stack frames mapped back to C# ----------
        // Luna's compiled scripts (UnityScriptsCompiler.js) mark every method with /*Ns.Class.Method start.*/ and
        // /*... end.*/ comments; a stack frame's line falls between the two, which gives the C# method.
        var MAX_ENTRIES = 200;
        var logs = { list: [], byKey: new Map(), total: 0, maps: new Map() };
        var FRAME = /((?:https?:)?\/\/[^\s()]+?\/([^\/\s()?#]+\.js))(?:\?[^\s():]*)?:(\d+):(\d+)/g;

        function textOf(args) {
            var message = [], stack = '';
            for (var i = 0; i < args.length; i++) {
                var a = args[i];
                if (a === null || a === undefined) continue;
                if (typeof a === 'string' || typeof a === 'number' || typeof a === 'boolean') { message.push(String(a)); continue; }
                if (typeof a.message === 'string') message.push(a.message);
                else { try { message.push(JSON.stringify(a).slice(0, 300)); } catch (e) { message.push(String(a)); } }
                var s = a.errorStack || a.stack;
                if (!stack && typeof s === 'string') stack = s;
                if (!stack && a.innerException && typeof a.innerException.stack === 'string') stack = a.innerException.stack;
            }
            return { message: message.join(' ').slice(0, 1000), stack: stack.slice(0, 4000) };
        }

        function frames(stack) {
            var out = [], m;
            FRAME.lastIndex = 0;
            while ((m = FRAME.exec(stack)) && out.length < 30) out.push({ url: m[1], file: m[2], line: +m[3], col: +m[4] });
            return out;
        }

        function record(level, args) {
            try {
                var t = textOf(args), fs = frames(t.stack);
                var key = level + '|' + t.message + '|' + (fs[0] ? fs[0].file + ':' + fs[0].line : '');
                var entry = logs.byKey.get(key);
                if (!entry) {
                    if (logs.list.length >= MAX_ENTRIES) { logs.total++; return; }
                    var a = app;
                    entry = { level: level, message: t.message, stack: t.stack, frames: fs, count: 0, firstFrame: a ? a.frame : -1, csharp: null };
                    logs.byKey.set(key, entry);
                    logs.list.push(entry);
                    mapEntry(entry);
                }
                entry.count++;
                entry.lastFrame = app ? app.frame : -1;
                logs.total++;
            } catch (e) { /* Never let logging fail the page. */ }
        }

        // Method ranges of a compiled script, built once per file.
        function methodMap(url) {
            var cached = logs.maps.get(url);
            if (cached) return cached;
            var entry = { ready: false, starts: [], ends: [], names: [], classes: [] };
            logs.maps.set(url, entry);
            try {
                win.fetch(url).then(function (r) { return r.text(); }).then(function (text) {
                    var lines = text.split('\n'), open = {};
                    for (var i = 0; i < lines.length; i++) {
                        var line = lines[i], m = /\/\*([\w.$`+<>]+) (start|end)\.\*\//.exec(line);
                        if (m) {
                            if (m[2] === 'start') open[m[1]] = i + 1;
                            else if (open[m[1]]) { entry.starts.push(open[m[1]]); entry.ends.push(i + 1); entry.names.push(m[1]); delete open[m[1]]; }
                        }
                        var d = line.indexOf('Bridge.define("');
                        if (d >= 0) entry.classes.push({ line: i + 1, name: line.substring(d + 15, line.indexOf('"', d + 15)) });
                    }
                    entry.ready = true;
                    logs.list.forEach(mapEntry);
                }).catch(function () { entry.ready = true; });
            } catch (e) { entry.ready = true; }
            return entry;
        }

        // Innermost marked range around the line. Bridge also marks whole classes: a line only inside a class range
        // (or after a Bridge.define with no marker) is in a property, constructor or field initializer.
        function csharpAt(map, line) {
            var best = null;
            if (!map.classNames) { map.classNames = new Set(); map.classes.forEach(function (c) { map.classNames.add(c.name); }); }
            for (var i = 0; i < map.starts.length; i++)
                if (map.starts[i] <= line && line <= map.ends[i] && (!best || map.starts[i] > best.start)) best = { start: map.starts[i], name: map.names[i] };
            var cls = null;
            if (!best) for (var c = 0; c < map.classes.length && map.classes[c].line <= line; c++) cls = map.classes[c].name;
            var name = best ? best.name : cls;
            if (!name) return null;
            return map.classNames.has(name) ? name + ' (property / constructor)' : name;
        }

        // C# methods of the compiled-script frames (starts loading the map on first use; empty until it is ready).
        function csharpOf(stackFrames) {
            var names = [];
            stackFrames.forEach(function (f) {
                if (f.file !== 'UnityScriptsCompiler.js') return;
                var map = methodMap(f.url);
                if (!map.ready) return;
                var name = csharpAt(map, f.line);
                if (name && names[names.length - 1] !== name) names.push(name);
            });
            return names;
        }

        function mapsReady() {
            var ready = true;
            logs.maps.forEach(function (m) { if (!m.ready) ready = false; });
            return ready;
        }

        function mapEntry(entry) {
            var names = csharpOf(entry.frames);
            if (names.length) entry.csharp = names;
        }

        try {
            ['error', 'warn'].forEach(function (level) {
                var original = win.console[level];
                if (typeof original !== 'function') return;
                win.console[level] = function () {
                    record(level === 'warn' ? 'warning' : 'error', arguments);
                    return original.apply(this, arguments);
                };
            });
            win.addEventListener('error', function (ev) { record('error', [ev.error || ev.message]); });
            win.addEventListener('unhandledrejection', function (ev) { record('error', [ev.reason]); });
        } catch (e) {
            fail('console hook', e);
        }

        function shortName(name) {
            var parts = name.split('.');
            return parts.length > 2 ? parts.slice(-2).join('.') : name;
        }

        agent.console = {
            list: function () { return logs.list; },
            total: function () { return logs.total; },
            errorCount: function () { var n = 0; logs.list.forEach(function (e) { if (e.level === 'error') n++; }); return n; },
            clear: function () { logs.list.length = 0; logs.byKey.clear(); logs.total = 0; },
            shortName: shortName,
            // Plain text of everything collected, for an issue tracker or an agent.
            report: function () {
                return logs.list.map(function (e) {
                    return '[' + e.level + '] x' + e.count + ' (frame ' + e.firstFrame + ') ' + e.message +
                        (e.csharp ? '\n  C#: ' + e.csharp.join(' <- ') : '') + (e.stack ? '\n  ' + e.stack.split('\n').slice(0, 8).join('\n  ') : '');
                }).join('\n\n');
            }
        };

        // ---------- WebGL ----------
        var gl = {
            contexts: [],
            frameToken: null,
            redrawSeq: 0,
            limit: Infinity,
            // Per frame.
            draws: 0, skipped: 0, programs: 0, texBinds: 0, fbBinds: 0, uploadBytes: 0,
            last: null,                     // Last committed frame's counters.
            maxDraws: 0,
            // Memory estimate.
            textureBytes: 0, bufferBytes: 0,
            // Capture.
            captureArmed: false, capturing: false, captureList: null, capture: null, captureListeners: []
        };
        var history = { draws: new Ring(120), uploads: new Ring(120) };

        function frameToken() {
            var a = getApp();
            return (a ? a.frame : -1) + gl.redrawSeq * 1e7;
        }

        function commitFrame() {
            gl.last = { draws: gl.draws, skipped: gl.skipped, programs: gl.programs, texBinds: gl.texBinds, fbBinds: gl.fbBinds, uploadBytes: gl.uploadBytes };
            if (gl.draws > 0) {
                history.draws.push(gl.draws);
                gl.maxDraws = gl.draws;     // Skipped draws still count: this is the frame's full draw list.
            }
            history.uploads.push(gl.uploadBytes);
            gl.draws = gl.skipped = gl.programs = gl.texBinds = gl.fbBinds = gl.uploadBytes = 0;

            if (gl.capturing) {
                gl.capturing = false;
                gl.capture = gl.captureList;
                gl.captureList = null;
                var listeners = gl.captureListeners;
                gl.captureListeners = [];
                listeners.forEach(function (cb) { try { cb(gl.capture); } catch (e) { fail('capture listener', e); } });
            }
            if (gl.captureArmed) {
                gl.captureArmed = false;
                gl.capturing = true;
                gl.captureList = [];
            }
        }

        function frameCheck() {
            var token = frameToken();
            if (token !== gl.frameToken) {
                if (gl.frameToken !== null) commitFrame();
                gl.frameToken = token;
            }
        }

        var ids = new WeakMap(), nextId = 1;
        function idOf(obj) {
            if (!obj) return 0;
            var id = ids.get(obj);
            if (!id) { id = nextId++; ids.set(obj, id); }
            return id;
        }

        var shaderNames = new WeakMap(), programShaders = new WeakMap(), programNames = new WeakMap();
        var textureInfo = new WeakMap(), bufferInfo = new WeakMap(), fbInfo = new WeakMap();

        function shaderName(source) {
            var m = /#define\s+SHADER_NAME\s+(\S+)/.exec(source) || /\/\/\s*(?:Shader|SHADER)\s*:?\s*(.+)/.exec(source);
            return m ? m[1].trim() : null;
        }

        function programName(p) {
            if (!p) return '(none)';
            var name = programNames.get(p);
            if (name) return name;
            var shaders = programShaders.get(p) || [];
            for (var i = 0; i < shaders.length && !name; i++) name = shaderNames.get(shaders[i]);
            name = (name || 'Program') + ' #' + idOf(p);
            programNames.set(p, name);
            return name;
        }

        function bytesPerPixel(ctx, format, type) {
            var channels = 4;
            if (format === ctx.RGB) channels = 3;
            else if (format === ctx.LUMINANCE_ALPHA || format === 0x8227 /* RG */) channels = 2;
            else if (format === ctx.ALPHA || format === ctx.LUMINANCE || format === 0x1903 /* RED */ || format === ctx.DEPTH_COMPONENT) channels = 1;
            var size = 1;
            if (type === ctx.FLOAT || type === ctx.UNSIGNED_INT || type === ctx.INT) size = 4;
            else if (type === ctx.UNSIGNED_SHORT || type === ctx.SHORT || type === 0x140B /* HALF_FLOAT */ || type === 0x8D61 /* HALF_FLOAT_OES */) size = 2;
            else if (type === ctx.UNSIGNED_SHORT_5_6_5 || type === ctx.UNSIGNED_SHORT_4_4_4_4 || type === ctx.UNSIGNED_SHORT_5_5_5_1) return 2;
            return channels * size;
        }

        function sourceSize(s) {
            if (!s) return null;
            var w = s.naturalWidth || s.videoWidth || s.displayWidth || s.width;
            var h = s.naturalHeight || s.videoHeight || s.displayHeight || s.height;
            return w && h ? { w: w, h: h } : null;
        }

        function hookContext(ctx, canvas) {
            var isGL2 = typeof WebGL2RenderingContext !== 'undefined' && ctx instanceof WebGL2RenderingContext;
            var state = { program: null, unit: 0, units2D: [], fb: null, blend: false, depth: false, cull: false, arrayBuffer: null, elementBuffer: null };
            gl.contexts.push({ ctx: ctx, canvas: canvas, gl2: isGL2 });

            function wrap(target, name, fn) {
                var orig = target[name];
                if (typeof orig !== 'function') return;
                target[name] = function () { return fn(orig, this, arguments); };
            }

            // The texture an upload to `target` goes to: TEXTURE_2D, or a cube map face / 3D target on the unit.
            function boundTexture(target) {
                if (target === ctx.TEXTURE_2D) return state.units2D[state.unit];
                return state.unitsOther && state.unitsOther[state.unit];
            }

            // key: one image of the texture (target:level); base: level 0, whose size names the texture.
            function setTextureBytes(tex, key, bytes, w, h, base) {
                if (!tex) return;
                var info = textureInfo.get(tex);
                if (!info) { info = { parts: {}, total: 0, w: 0, h: 0 }; textureInfo.set(tex, info); }
                var delta = bytes - (info.parts[key] || 0);
                gl.textureBytes += delta;
                info.total += delta;
                info.parts[key] = bytes;
                if (base && w && h) { info.w = w; info.h = h; }
            }

            function texLabel(tex) {
                if (!tex) return '-';
                var info = textureInfo.get(tex);
                return '#' + idOf(tex) + (info && info.w ? ' ' + info.w + 'x' + info.h : '');
            }

            function fbLabel(fb) {
                if (!fb) return 'Screen';
                var info = fbInfo.get(fb);
                return 'RT #' + idOf(fb) + (info ? ' ' + info : '');
            }

            function draw(kind, orig, self, args) {
                frameCheck();
                var index = gl.draws++;
                if (gl.capturing && gl.captureList.length < 5000) {
                    var count, instances = 1;
                    if (kind === 'arrays') count = args[2];
                    else if (kind === 'elements') count = args[1];
                    else if (kind === 'arraysInstanced') { count = args[2]; instances = args[3]; }
                    else if (kind === 'elementsInstanced') { count = args[1]; instances = args[4]; }
                    else if (kind === 'rangeElements') count = args[3];
                    var textures = [];
                    for (var u = 0; u < 4; u++) if (state.units2D[u]) textures.push(u + ':' + texLabel(state.units2D[u]));
                    gl.captureList.push({
                        index: index, kind: kind, count: count, instances: instances,
                        program: programName(state.program), target: fbLabel(state.fb),
                        blend: state.blend, depth: state.depth, cull: state.cull, textures: textures.join('  ')
                    });
                }
                if (index >= gl.limit) {
                    gl.skipped++;
                    return undefined;
                }
                return orig.apply(self, args);
            }

            wrap(ctx, 'drawArrays', function (o, s, a) { return draw('arrays', o, s, a); });
            wrap(ctx, 'drawElements', function (o, s, a) { return draw('elements', o, s, a); });
            if (isGL2) {
                wrap(ctx, 'drawArraysInstanced', function (o, s, a) { return draw('arraysInstanced', o, s, a); });
                wrap(ctx, 'drawElementsInstanced', function (o, s, a) { return draw('elementsInstanced', o, s, a); });
                wrap(ctx, 'drawRangeElements', function (o, s, a) { return draw('rangeElements', o, s, a); });
            }
            wrap(ctx, 'getExtension', function (o, s, a) {
                var ext = o.apply(s, a);
                if (ext && /instanced_arrays/i.test(String(a[0])) && !ext.__lunaDevTools) {
                    ext.__lunaDevTools = true;
                    wrap(ext, 'drawArraysInstancedANGLE', function (o2, s2, a2) { return draw('arraysInstanced', o2, s2, a2); });
                    wrap(ext, 'drawElementsInstancedANGLE', function (o2, s2, a2) { return draw('elementsInstanced', o2, s2, a2); });
                }
                return ext;
            });

            // State, for the frame capture and the per-frame counters.
            wrap(ctx, 'useProgram', function (o, s, a) {
                frameCheck();
                if (a[0] !== state.program) gl.programs++;
                state.program = a[0];
                return o.apply(s, a);
            });
            wrap(ctx, 'activeTexture', function (o, s, a) {
                state.unit = a[0] - ctx.TEXTURE0;
                return o.apply(s, a);
            });
            wrap(ctx, 'bindTexture', function (o, s, a) {
                frameCheck();
                gl.texBinds++;
                if (a[0] === ctx.TEXTURE_2D) state.units2D[state.unit] = a[1];
                else { state.unitsOther = state.unitsOther || []; state.unitsOther[state.unit] = a[1]; }
                return o.apply(s, a);
            });
            wrap(ctx, 'bindFramebuffer', function (o, s, a) {
                frameCheck();
                gl.fbBinds++;
                if (a[0] === ctx.FRAMEBUFFER || a[0] === 0x8CA9 /* DRAW_FRAMEBUFFER */) state.fb = a[1];
                return o.apply(s, a);
            });
            wrap(ctx, 'framebufferTexture2D', function (o, s, a) {
                var info = a[3] && textureInfo.get(a[3]);
                if (state.fb && info && info.w) fbInfo.set(state.fb, info.w + 'x' + info.h);
                return o.apply(s, a);
            });
            wrap(ctx, 'enable', function (o, s, a) {
                if (a[0] === ctx.BLEND) state.blend = true;
                else if (a[0] === ctx.DEPTH_TEST) state.depth = true;
                else if (a[0] === ctx.CULL_FACE) state.cull = true;
                return o.apply(s, a);
            });
            wrap(ctx, 'disable', function (o, s, a) {
                if (a[0] === ctx.BLEND) state.blend = false;
                else if (a[0] === ctx.DEPTH_TEST) state.depth = false;
                else if (a[0] === ctx.CULL_FACE) state.cull = false;
                return o.apply(s, a);
            });

            // Shader names for the capture.
            wrap(ctx, 'shaderSource', function (o, s, a) {
                try { var n = shaderName(String(a[1])); if (n) shaderNames.set(a[0], n); } catch (e) { /* Name is optional. */ }
                return o.apply(s, a);
            });
            wrap(ctx, 'attachShader', function (o, s, a) {
                var list = programShaders.get(a[0]) || [];
                list.push(a[1]);
                programShaders.set(a[0], list);
                return o.apply(s, a);
            });

            // Uploads (bytes sent to the GPU this frame) and a texture/buffer memory estimate.
            function upload(bytes) {
                frameCheck();
                if (bytes > 0) gl.uploadBytes += bytes;
            }
            wrap(ctx, 'texImage2D', function (o, s, a) {
                // (target, level, internalformat, width, height, border, format, type, pixels) or
                // (target, level, internalformat, format, type, source).
                var bytes = 0, w = 0, h = 0;
                if (a.length >= 8) { w = a[3]; h = a[4]; bytes = w * h * bytesPerPixel(ctx, a[6], a[7]); }
                else { var size = sourceSize(a[5]); if (size) { w = size.w; h = size.h; bytes = w * h * bytesPerPixel(ctx, a[3], a[4]); } }
                upload(a.length >= 9 && a[8] === null ? 0 : bytes);     // null pixels: allocation only.
                setTextureBytes(boundTexture(a[0]), a[0] + ':' + a[1], bytes, w, h, a[1] === 0);
                return o.apply(s, a);
            });
            wrap(ctx, 'texSubImage2D', function (o, s, a) {
                var bytes = 0;
                if (a.length >= 9) bytes = a[4] * a[5] * bytesPerPixel(ctx, a[6], a[7]);
                else { var size = sourceSize(a[6]); if (size) bytes = size.w * size.h * bytesPerPixel(ctx, a[4], a[5]); }
                upload(bytes);
                return o.apply(s, a);
            });
            wrap(ctx, 'compressedTexImage2D', function (o, s, a) {
                var data = a[6];
                var bytes = data && data.byteLength || 0;
                upload(bytes);
                setTextureBytes(boundTexture(a[0]), a[0] + ':' + a[1], bytes, a[3], a[4], a[1] === 0);
                return o.apply(s, a);
            });
            wrap(ctx, 'compressedTexSubImage2D', function (o, s, a) {
                var data = a[7];
                upload(data && data.byteLength || 0);
                return o.apply(s, a);
            });
            if (isGL2) {
                wrap(ctx, 'texStorage2D', function (o, s, a) {
                    // Allocation only (no upload): levels x w x h, roughly 4 bytes per pixel.
                    var bytes = 0, w = a[3], h = a[4];
                    for (var l = 0; l < a[1]; l++) { bytes += Math.max(1, w >> l) * Math.max(1, h >> l) * 4; }
                    setTextureBytes(boundTexture(a[0]), a[0] + ':storage', bytes, w, h, true);
                    return o.apply(s, a);
                });
            }
            wrap(ctx, 'deleteTexture', function (o, s, a) {
                var info = a[0] && textureInfo.get(a[0]);
                if (info) { gl.textureBytes -= info.total; textureInfo.delete(a[0]); }
                return o.apply(s, a);
            });
            wrap(ctx, 'bindBuffer', function (o, s, a) {
                if (a[0] === ctx.ARRAY_BUFFER) state.arrayBuffer = a[1];
                else if (a[0] === ctx.ELEMENT_ARRAY_BUFFER) state.elementBuffer = a[1];
                return o.apply(s, a);
            });
            wrap(ctx, 'bufferData', function (o, s, a) {
                var data = a[1];
                var bytes = typeof data === 'number' ? data : (data && data.byteLength) || 0;
                upload(typeof data === 'number' ? 0 : bytes);
                var buf = a[0] === ctx.ARRAY_BUFFER ? state.arrayBuffer : a[0] === ctx.ELEMENT_ARRAY_BUFFER ? state.elementBuffer : null;
                if (buf) {
                    var old = bufferInfo.get(buf) || 0;
                    gl.bufferBytes += bytes - old;
                    bufferInfo.set(buf, bytes);
                }
                return o.apply(s, a);
            });
            wrap(ctx, 'bufferSubData', function (o, s, a) {
                var data = a[2];
                var bytes = data && data.byteLength || 0;
                if (a.length >= 5 && a[4]) bytes = a[4] * (data && data.BYTES_PER_ELEMENT || 1);
                upload(bytes);
                return o.apply(s, a);
            });
            wrap(ctx, 'deleteBuffer', function (o, s, a) {
                var old = a[0] && bufferInfo.get(a[0]);
                if (old) { gl.bufferBytes -= old; bufferInfo.delete(a[0]); }
                return o.apply(s, a);
            });
        }

        try {
            var hooked = new WeakSet();
            var proto = win.HTMLCanvasElement && win.HTMLCanvasElement.prototype;
            var origGetContext = proto && proto.getContext;
            if (origGetContext) {
                proto.getContext = function (type) {
                    var ctx = origGetContext.apply(this, arguments);
                    if (ctx && /webgl/i.test(String(type)) && !hooked.has(ctx)) {
                        hooked.add(ctx);
                        try { hookContext(ctx, this); } catch (e) { fail('WebGL hook', e); }
                    }
                    return ctx;
                };
            }
        } catch (e) {
            fail('getContext hook', e);
        }

        // Redraws the current scene without advancing the game (paused frame + draw limit / capture),
        // as a frame of its own: its counters (and an armed capture) are complete when this returns.
        function redraw() {
            var a = getApp();
            if (!a || typeof a.render !== 'function') return false;
            gl.redrawSeq++;
            var ok = true;
            try {
                a.render();
            } catch (e) {
                fail('redraw', e);
                ok = false;
            }
            gl.redrawSeq++;
            frameCheck();
            return ok;
        }

        agent.gl = {
            available: function () { return gl.contexts.length > 0; },
            last: function () { return gl.last; },
            limit: function () { return gl.limit; },
            maxDraws: function () { return gl.maxDraws; },
            setLimit: function (n) {
                gl.limit = n === null || n === undefined || n < 0 ? Infinity : n;
                if (controls.paused) redraw();
            },
            memory: function () { return { textures: gl.textureBytes, buffers: gl.bufferBytes }; },
            history: history,
            capture: function () { return gl.capture; },
            captureFrame: function (cb) {
                if (cb) gl.captureListeners.push(cb);
                gl.captureArmed = true;
                if (controls.paused)
                    redraw();           // Its first draw starts the frame the armed capture records.
            },
            canvas: function () { return gl.contexts.length ? gl.contexts[0].canvas : null; }
        };

        // ---------- Pause / step / time scale (engine application, only after a button press) ----------
        // app.paused skips update + render; app.timeScale scales the engine delta (0 keeps Time.time still while
        // paused). Both go back to the engine's own values when the controls return to normal.
        var controls = { paused: false, scale: 1, stepFrom: null, baseScale: null, pausedByUs: false };

        function apply() {
            var a = getApp();
            if (!a) return false;
            if (controls.baseScale === null) controls.baseScale = a.timeScale;
            var halt = controls.paused && controls.stepFrom === null;
            if (halt) {
                a.timeScale = 0;
                a.paused = true;
                controls.pausedByUs = true;
            } else {
                a.timeScale = controls.baseScale * controls.scale;
                if (controls.pausedByUs) {
                    a.paused = false;       // Only undo our own pause, never one Luna set itself.
                    controls.pausedByUs = false;
                }
            }
            if (!controls.paused && controls.scale === 1) {
                a.timeScale = controls.baseScale;
                controls.baseScale = null;  // Nothing of ours left on the engine.
            }
            return true;
        }

        agent.controls = {
            paused: function () { return controls.paused; },
            scale: function () { return controls.scale; },
            setPaused: function (p) { controls.paused = !!p; controls.stepFrom = null; return apply(); },
            setScale: function (s) { controls.scale = s; return apply(); },
            step: function () {
                var a = getApp();
                if (!a) return false;
                if (!controls.paused) { controls.paused = true; apply(); return true; }
                controls.stepFrom = a.frame;
                apply();
                return true;
            },
            restart: function () { win.location.reload(); },
            activate: function (name) { return activateByName(name); }
        };

        // Turns on a scene object by name (e.g. the end card). It runs the object's scripts like real play does.
        function activateByName(name) {
            var U = win.UnityEngine;
            if (!U || !U.SceneManagement || !U.SceneManagement.SceneManager) return 'UnityEngine API not available yet';
            try {
                var sm = U.SceneManagement.SceneManager;
                var scenes = [];
                var count = typeof sm.sceneCount === 'number' ? sm.sceneCount : 1;
                for (var i = 0; i < count; i++) {
                    try { scenes.push(sm.GetSceneAt ? sm.GetSceneAt(i) : sm.GetActiveScene()); } catch (e) { scenes.push(sm.GetActiveScene()); }
                }
                if (!scenes.length) scenes.push(sm.GetActiveScene());
                var target = null;
                var visit = function (t) {
                    if (target || !t) return;
                    if (t.gameObject && t.gameObject.name === name) { target = t.gameObject; return; }
                    for (var c = 0; c < t.childCount && !target; c++) visit(t.GetChild(c));
                };
                for (var s = 0; s < scenes.length && !target; s++) {
                    var scene = scenes[s];
                    var roots = scene.GetRootGameObjects ? scene.GetRootGameObjects() : null;
                    if (!roots && scene.GetRootGameObjects$1) roots = scene.GetRootGameObjects$1();
                    if (!roots) continue;
                    for (var r = 0; r < roots.length && !target; r++) visit(roots[r].transform);
                }
                if (!target) return 'No object named "' + name + '" in the open scenes';
                target.SetActive(true);
                var t2 = target.transform && target.transform.parent;
                while (t2) { if (t2.gameObject && !t2.gameObject.activeSelf) t2.gameObject.SetActive(true); t2 = t2.parent; }
                return 'Activated "' + name + '"';
            } catch (e) {
                return 'Failed: ' + (e && e.message || e);
            }
        }

        // ---------- Per frame sampling (own rAF loop, real clock) ----------
        var timing = {
            interval: new Ring(240), frame: new Ring(240), heap: new Ring(240), heapTimes: new Ring(240),
            lastRaf: 0, lastFrame: -1, lastHeapAt: 0, allocBytes: 0, allocSince: realNow(), allocRate: 0,
            breakdown: null, breakdownPrev: null
        };

        function readBreakdown(a) {
            var d = a.counters && a.counters.detailedBreakdown;
            if (!d || !d.timings || !d.metrics) return null;
            var flat = { frames: d.metrics.frames };
            for (var section in d.timings) {
                var t = d.timings[section];
                for (var key in t) flat[section + (key === 'total' ? '' : '.' + key)] = t[key];
            }
            return flat;
        }

        function loop(ts) {
            nativeRaf(loop);
            try {
                if (timing.lastRaf) timing.interval.push(ts - timing.lastRaf);
                timing.lastRaf = ts;
                var a = getApp();
                if (a) {
                    if (!scripts.installed) installScriptHooks(a);
                    if (a.frame !== timing.lastFrame) {
                        timing.lastFrame = a.frame;
                        var prev = a.counters && a.counters.previous;
                        if (prev && prev.times && prev.times.frame > 0) timing.frame.push(prev.times.frame);
                        hotEndFrame();
                        if (controls.stepFrom !== null && a.frame !== controls.stepFrom) {
                            controls.stepFrom = null;
                            apply();   // Back to paused after exactly one frame.
                        }
                    }
                    frameCheck();
                }
                var t = realNow();
                if (performance.memory && t - timing.lastHeapAt >= 250) {
                    var used = performance.memory.usedJSHeapSize;
                    var values = timing.heap.values();
                    var prevUsed = values.length ? values[values.length - 1] : used;
                    if (used > prevUsed) timing.allocBytes += used - prevUsed;
                    timing.heap.push(used);
                    timing.lastHeapAt = t;
                    if (t - timing.allocSince >= 2000) {
                        timing.allocRate = timing.allocBytes / ((t - timing.allocSince) / 1000);
                        timing.allocBytes = 0;
                        timing.allocSince = t;
                    }
                }
            } catch (e) {
                fail('sampling', e);
            }
        }
        nativeRaf(loop);

        agent.snapshot = function () {
            var a = getApp();
            var prev = a && a.counters && a.counters.previous;
            var breakdown = a ? readBreakdown(a) : null;
            var perFrame = null;
            if (breakdown) {
                var base = timing.breakdownPrev;
                if (base && breakdown.frames > base.frames) {
                    perFrame = {};
                    var frames = breakdown.frames - base.frames;
                    for (var k in breakdown) if (k !== 'frames') perFrame[k] = (breakdown[k] - (base[k] || 0)) / frames;
                    timing.breakdown = perFrame;
                }
                if (!base || breakdown.frames < base.frames || breakdown.frames - base.frames >= 30) timing.breakdownPrev = breakdown;
            }
            return {
                ready: !!a,
                frame: a ? a.frame : 0,
                interval: timing.interval.stats(),
                frameMs: timing.frame.stats(),
                frameHistory: timing.frame,
                intervalHistory: timing.interval,
                heapHistory: timing.heap,
                heap: performance.memory ? performance.memory.usedJSHeapSize : null,
                heapLimit: performance.memory ? performance.memory.jsHeapSizeLimit : null,
                allocRate: timing.allocRate,
                engine: prev ? {
                    scripts: prev.times.scripts, animations: prev.times.animations, animators: prev.times.animators,
                    physics: prev.times.physics + prev.times.physics2d, render: prev.times.render, frame: prev.times.frame,
                    drawCalls: prev.drawCalls, triangles: prev.trianglesCount, vertices: prev.verticesCount,
                    materialSwitches: prev.materialSwitches, uiElements: prev.uiElements, particles: prev.particles,
                    particleSystems: prev.particleSystems, skinned: prev.visibleSkinnedMeshes, animatorsCount: prev.animators,
                    shadowCasters: prev.shadowCasters
                } : null,
                breakdown: timing.breakdown,
                gl: gl.last,
                glMemory: { textures: gl.textureBytes, buffers: gl.bufferBytes },
                uploads: history.uploads.stats(),
                errors: agent.errors
            };
        };

        // ---------- Agents (MCP): commands from the Editor, over long-polling to the Dev Tools proxy ----------
        // Reading commands use engine data (entities, components) and raw field values only: no C# method or property
        // getter runs, so Luna's runtime analysis is unchanged. control / eval are the exceptions and say so.
        var CHANNEL_ROUTE = '/__luna_devtools/channel', RESULT_ROUTE = '/__luna_devtools/result';
        var channel = { id: Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4), backoff: 1000, pending: [] };
        var watches = {}, watchSeq = 0;

        function round(v, digits) {
            if (typeof v === 'number') { var k = Math.pow(10, digits || 3); return Math.round(v * k) / k; }
            if (v && typeof v === 'object' && !Array.isArray(v)) { var o = {}; for (var key in v) o[key] = round(v[key], digits); return o; }
            return v;
        }

        function typeName(v) {
            var c = v && v.constructor;
            return (c && (c.$$fullname || c.$$name || c.name)) || typeof v;
        }

        // Own data field only: reading a C# property would run its getter (and its TRACE).
        function own(o, key) {
            var d = o && Object.getOwnPropertyDescriptor(o, key);
            return d && 'value' in d ? d.value : undefined;
        }

        // Plain-JSON view of a value, depth-limited; never calls getters of C# objects.
        function summarize(v, depth) {
            if (v === null || v === undefined) return null;
            var t = typeof v;
            if (t === 'number') return isFinite(v) ? round(v, 4) : String(v);
            if (t === 'boolean') return v;
            if (t === 'string') return v.length > 200 ? v.slice(0, 200) + '…' : v;
            if (t === 'function') return { function: v.$$fullname || v.name || 'function' };
            if (Array.isArray(v) || ArrayBuffer.isView(v)) {
                var arr = { array: v.length };
                if (depth > 0) arr.items = Array.prototype.slice.call(v, 0, 8).map(function (x) { return summarize(x, depth - 1); });
                return arr;
            }
            var x = own(v, 'x'), y = own(v, 'y'), z = own(v, 'z'), w = own(v, 'w');
            if (typeof x === 'number' && typeof y === 'number' && Object.keys(v).length <= 6)
                return round(typeof z === 'number' ? (typeof w === 'number' ? { x: x, y: y, z: z, w: w } : { x: x, y: y, z: z }) : { x: x, y: y }, 4);
            var type = typeName(v), items = own(v, '_items'), size = own(v, '_size');
            if (Array.isArray(items) && typeof size === 'number') {
                var list = { type: type, count: size };
                if (depth > 0) list.items = items.slice(0, Math.min(8, size)).map(function (it) { return summarize(it, depth - 1); });
                return list;
            }
            var entity = own(v, 'entity'), name = own(v, 'name'), count = own(v, 'count');
            if (entity && typeof own(entity, 'name') === 'string') return { type: type, gameObject: own(entity, 'name') };   // Engine component.
            if (typeof name === 'string' && typeof v.getPosition === 'function') return { type: 'GameObject', name: name };   // Engine entity.
            if (typeof count === 'number') return { type: type, count: count };
            if (depth <= 0) return { type: type };
            return { type: type, fields: fieldsOf(v, depth - 1, 12) };
        }

        // Own data fields as compiled from C# (Luna engine internals end with '$' and are skipped).
        function fieldsOf(obj, depth, max) {
            var out = {}, n = 0;
            Object.keys(obj).forEach(function (key) {
                if (n >= (max || 60) || key.indexOf('$') >= 0 || key.indexOf('__') === 0) return;
                var d = Object.getOwnPropertyDescriptor(obj, key);
                if (!d || !('value' in d) || typeof d.value === 'function') return;
                out[key] = summarize(d.value, depth);
                n++;
            });
            return out;
        }

        function scriptsOf(entity) {
            var list = entity._unityComponents && entity._unityComponents.monoBehaviour;
            return list ? list.filter(function (c) { return c && c.code; }) : [];
        }

        function vec(p) { return p ? round({ x: p.x, y: p.y, z: p.z }, 3) : null; }

        function children(entity) { return entity.children || entity._children || []; }

        // Paths name each level; siblings sharing a name get [index] among them.
        function walk(visit, limit) {
            var a = getApp();
            if (!a || !a.root) throw new Error('The Luna engine has not started yet.');
            var queue = children(a.root).map(function (e) { return { e: e, path: null }; }), at = 0;
            label(queue, '');
            while (at < queue.length && at < (limit || 50000)) {
                var item = queue[at++];
                if (visit(item.e, item.path) === false) return;
                var next = children(item.e).map(function (e) { return { e: e }; });
                label(next, item.path + '/');
                for (var i = 0; i < next.length; i++) queue.push(next[i]);
            }
        }

        function label(items, prefix) {
            var counts = {};
            items.forEach(function (it) { var nm = it.e.name || '?'; counts[nm] = (counts[nm] || 0) + 1; });
            var used = {};
            items.forEach(function (it) {
                var nm = it.e.name || '?', k = used[nm] = (used[nm] === undefined ? 0 : used[nm] + 1);
                it.path = prefix + nm + (counts[nm] > 1 ? '[' + k + ']' : '');
            });
        }

        function entityAt(path) {
            var found = null;
            walk(function (e, p) { if (p === path) { found = e; return false; } });
            if (!found) throw new Error('No GameObject at path "' + path + '" (paths come from luna_find_objects).');
            return found;
        }

        function scriptOn(entity, script) {
            var list = scriptsOf(entity);
            var match = list.filter(function (c) { return !script || (c.code.$$fullname || '').toLowerCase().indexOf(script.toLowerCase()) >= 0; });
            if (!match.length) throw new Error('No MonoBehaviour' + (script ? ' matching "' + script + '"' : '') + ' on that object.');
            return match[0];
        }

        function stackHere() { return new Error().stack || ''; }

        function waitForMaps(ms) {
            return new Promise(function (resolve) {
                var until = realNow() + ms;
                (function check() { if (mapsReady() || realNow() > until) resolve(); else setTimeout(check, 50); })();
            });
        }

        var commands = {
            stats: function () {
                var s = agent.snapshot();
                return round({
                    engineReady: s.ready, frame: s.frame, paused: controls.paused, timeScale: controls.scale,
                    fps: s.interval.avg ? 1000 / s.interval.avg : 0,
                    frameMs: s.frameMs, betweenFramesMs: s.interval,
                    engine: s.engine, msPerFrameBySection: s.breakdown,
                    webglPerFrame: s.gl, gpuMemoryMB: (s.glMemory.textures + s.glMemory.buffers) / 1048576,
                    uploadsKB: { avg: s.uploads.avg / 1024, worst: s.uploads.max / 1024 },
                    heapMB: s.heap === null ? null : s.heap / 1048576, allocMBPerSecond: s.heap === null ? null : s.allocRate / 1048576,
                    devToolsProblems: agent.errors
                }, 3);
            },

            errors: function (p) {
                var level = p.level || 'all', limit = p.limit || 30;
                var list = agent.console.list().filter(function (e) { return level === 'all' || e.level === level; })
                    .sort(function (x, y) { return y.count - x.count; }).slice(0, limit);
                return waitForMaps(3000).then(function () {
                    var out = {
                        total: agent.console.total(), distinct: agent.console.list().length,
                        entries: list.map(function (e) {
                            mapEntry(e);
                            return { level: e.level, count: e.count, message: e.message, csharp: e.csharp || null,
                                firstFrame: e.firstFrame, lastFrame: e.lastFrame, stack: e.stack ? e.stack.split('\n').slice(0, 10).join('\n') : null };
                        })
                    };
                    if (p.clear) agent.console.clear();
                    return out;
                });
            },

            hotpaths: function (p) {
                var seconds = Math.max(1, Math.min(30, p.seconds || 5)), filter = p.filter === 'all' ? 'all' : 'project', top = p.top || 20;
                if (!agent.hot.available()) throw new Error('This page has no TRACE calls (not a Build Develop page?).');
                agent.hot.reset();
                agent.hot.setRecording(true);
                return new Promise(function (resolve) {
                    setTimeout(function () {
                        agent.hot.setRecording(false);
                        var out = {
                            seconds: seconds, frames: agent.hot.frames(),
                            scripts: agent.scripts.top(top, 8).map(function (r) { return round(r, 3); }),
                            methods: agent.hot.top(top, filter).map(function (r) { return round(r, 2); }),
                            classes: agent.hot.top(15, filter, true).map(function (r) { return round(r, 2); })
                        };
                        if (!agent.scripts.installed()) out.scriptHooks = agent.scripts.note();
                        if (!out.frames) out.note = 'No frame was recorded: the game is paused, or the tab is hidden (browsers stop rendering it).';
                        resolve(out);
                    }, seconds * 1000);
                });
            },

            capture: function (p) {
                var limit = p.limit || 60;
                return new Promise(function (resolve) {
                    var done = false;
                    agent.gl.captureFrame(function (list) {
                        done = true;
                        var byShader = {}, byTarget = {};
                        list.forEach(function (d) {
                            var s = byShader[d.program] = byShader[d.program] || { draws: 0, vertices: 0 };
                            s.draws++;
                            s.vertices += (d.count || 0) * (d.instances || 1);
                            byTarget[d.target] = (byTarget[d.target] || 0) + 1;
                        });
                        resolve({
                            draws: list.length, byShader: byShader, byTarget: byTarget,
                            rows: list.slice(0, limit).map(function (d) {
                                return { i: d.index, shader: d.program, count: d.count, instances: d.instances, target: d.target,
                                    blend: d.blend, depth: d.depth, cull: d.cull, textures: d.textures };
                            })
                        });
                    });
                    setTimeout(function () { if (!done) resolve({ error: 'No frame was rendered within 5 s (paused with no redraw, or the tab is hidden).' }); }, 5000);
                });
            },

            find: function (p) {
                var name = (p.name || '').toLowerCase(), script = (p.script || '').toLowerCase(), limit = p.limit || 50, out = [], scanned = 0;
                walk(function (e, path) {
                    scanned++;
                    if (p.activeOnly && !e.enabled) return;
                    if (name && (e.name || '').toLowerCase().indexOf(name) < 0) return;
                    var scripts = scriptsOf(e).map(function (c) { return c.code.$$fullname || '?'; });
                    if (script && !scripts.some(function (s) { return s.toLowerCase().indexOf(script) >= 0; })) return;
                    out.push({ path: path, activeSelf: e._activeSelf !== false, activeInHierarchy: !!e.enabled,
                        position: vec(e.getPosition && e.getPosition()), scripts: scripts });
                    if (out.length >= limit) return false;
                });
                return { found: out.length, scanned: scanned, objects: out };
            },

            object: function (p) {
                var e = entityAt(p.path);
                var kinds = {};
                for (var k in (e._unityComponents || {})) if (e._unityComponents[k].length) kinds[k] = e._unityComponents[k].length;
                var scripts = scriptsOf(e).filter(function (c) {
                    return !p.script || (c.code.$$fullname || '').toLowerCase().indexOf(p.script.toLowerCase()) >= 0;
                });
                return {
                    path: p.path, name: e.name, activeSelf: e._activeSelf !== false, activeInHierarchy: !!e.enabled,
                    position: vec(e.getPosition && e.getPosition()), localPosition: vec(e.getLocalPosition && e.getLocalPosition()),
                    children: children(e).slice(0, 60).map(function (c) { return c.name; }), childCount: children(e).length,
                    components: kinds, engineComponents: Object.keys(e.c || {}),
                    scripts: scripts.map(function (c) { return { class: c.code.$$fullname, enabled: c.enabled !== false, fields: fieldsOf(c.code, 1, 80) }; })
                };
            },

            control: function (p) {
                var c = agent.controls, ok = true;
                if (p.action === 'pause') ok = c.setPaused(true);
                else if (p.action === 'resume') ok = c.setPaused(false);
                else if (p.action === 'step') ok = c.step();
                else if (p.action === 'speed') ok = c.setScale(Math.max(0.05, Math.min(16, +p.value || 1)));
                else if (p.action === 'restart') { setTimeout(c.restart, 150); return { restarting: true, note: 'The page reloads and reconnects as a new session.' }; }
                else throw new Error('Unknown action ' + p.action);
                if (!ok) throw new Error('The Luna engine has not started yet.');
                var a = getApp();
                return { paused: c.paused(), timeScale: c.scale(), frame: a ? a.frame : 0 };
            },

            screenshot: function (p) {
                var canvas = agent.gl.canvas();
                if (!canvas) throw new Error('No WebGL canvas yet.');
                redraw();                          // The drawing buffer is valid for the rest of this task.
                var w = Math.max(16, Math.min(p.maxWidth || 540, canvas.width)), h = Math.round(canvas.height * w / canvas.width);
                var copy = win.document.createElement('canvas');
                copy.width = w;
                copy.height = h;
                copy.getContext('2d').drawImage(canvas, 0, 0, w, h);
                var url = copy.toDataURL('image/jpeg', 0.85), a = getApp();
                return { base64: url.slice(url.indexOf(',') + 1), mimeType: 'image/jpeg', width: w, height: h, frame: a ? a.frame : 0 };
            },

            watch: function (p) {
                if (!p.field) throw new Error('field is required');
                var target, where;
                if (p.staticType) {
                    target = p.staticType.split('.').reduce(function (o, k) { return o ? o[k] : o; }, win);
                    if (!target) throw new Error('No C# type ' + p.staticType + ' in this page.');
                    where = p.staticType;
                } else {
                    if (!p.path) throw new Error('Give staticType, or path (+ script) for a MonoBehaviour field.');
                    var comp = scriptOn(entityAt(p.path), p.script);
                    target = comp.code;
                    where = p.path + ' / ' + comp.code.$$fullname;
                }
                var desc = Object.getOwnPropertyDescriptor(target, p.field);
                if (!desc) throw new Error('No field "' + p.field + '" on ' + where + '. Fields: ' + Object.keys(fieldsOf(target, 0, 80)).join(', '));
                if (!('value' in desc)) throw new Error('"' + p.field + '" is a property or already watched; watch its backing field.');
                var id = 'w' + (++watchSeq), value = desc.value, hits = [], max = p.maxHits || 30, total = 0;
                Object.defineProperty(target, p.field, {
                    configurable: true, enumerable: desc.enumerable,
                    get: function () { return value; },
                    set: function (v) {
                        total++;
                        if (hits.length < max) {
                            var a = getApp();
                            hits.push({ frame: a ? a.frame : -1, previous: summarize(value, 1), value: summarize(v, 1), stack: stackHere() });
                        }
                        value = v;
                    }
                });
                watches[id] = {
                    where: where, field: p.field, hits: hits, total: function () { return total; },
                    restore: function () { Object.defineProperty(target, p.field, { value: value, writable: true, configurable: true, enumerable: desc.enumerable }); }
                };
                return { id: id, watching: where + '.' + p.field, current: summarize(value, 1) };
            },

            watchHits: function (p) {
                var w = watches[p.id];
                if (!w) throw new Error('No watch ' + p.id + ' (the page may have reloaded).');
                w.hits.forEach(function (h) { csharpOf(frames(h.stack)); });          // Starts loading the maps.
                return waitForMaps(3000).then(function () {
                    var out = {
                        watching: w.where + '.' + w.field, assignments: w.total(),
                        hits: w.hits.map(function (h) {
                            var cs = csharpOf(frames(h.stack));
                            return { frame: h.frame, previous: h.previous, value: h.value, csharp: cs.length ? cs : null,
                                stack: h.stack.split('\n').slice(2, 10).join('\n') };
                        })
                    };
                    if (p.remove) { w.restore(); delete watches[p.id]; out.removed = true; }
                    return out;
                });
            },

            eval: function (p) {
                var fn = new win.Function('agent', 'app', p.code || '');
                return Promise.resolve(fn(agent, getApp())).then(function (v) { return { result: summarize(v, 3) }; });
            }
        };

        function hello() {
            var a = getApp();
            return { url: win.location.href, title: win.document && win.document.title, userAgent: win.navigator && win.navigator.userAgent,
                engineReady: !!a, frame: a ? a.frame : 0 };
        }

        function post(route, payload) {
            return win.fetch(route + '?session=' + encodeURIComponent(channel.id), {
                method: 'POST', cache: 'no-store', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
            });
        }

        function deliver(result) {
            post(RESULT_ROUTE, { results: [result] }).catch(function () { channel.pending.push(result); });
        }

        function runCommand(cmd) {
            new Promise(function (resolve) {
                var handler = commands[cmd.method];
                if (!handler) throw new Error('Unknown command ' + cmd.method);
                resolve(handler(cmd.params || {}));
            }).then(function (result) {
                deliver({ id: cmd.id, ok: true, result: result === undefined ? null : result });
            }, function (e) {
                deliver({ id: cmd.id, ok: false, error: String(e && e.message || e) });
            });
        }

        function pollLoop() {
            var results = channel.pending;
            channel.pending = [];
            post(CHANNEL_ROUTE, { hello: hello(), results: results }).then(function (r) { return r.json(); }).then(function (reply) {
                channel.backoff = 1000;
                (reply.commands || []).forEach(runCommand);
                setTimeout(pollLoop, 0);
            }).catch(function () {
                channel.pending = results.concat(channel.pending);
                setTimeout(pollLoop, channel.backoff);      // Proxy restarting (Unity reload) or gone: retry, slower each time.
                channel.backoff = Math.min(channel.backoff * 2, 15000);
            });
        }

        agent.commands = commands;
        agent.session = channel.id;
        // Only through the Dev Tools proxy (same origin as this script), which is where the channel lives.
        try {
            var here = win.location && win.location.origin;
            if (here && src.indexOf(here + '/__luna_devtools/') === 0 && typeof win.fetch === 'function')
                setTimeout(pollLoop, 500);
        } catch (e) {
            fail('agent channel', e);
        }

        return agent;
    }

    // =====================================================================================================
    // Panel: in the Dev Environment page, or in the playable page when it is opened directly.
    // =====================================================================================================
    function mountPanel(hostWin, getAgent, inPlayable) {
        var doc = hostWin.document;
        var host = doc.createElement('div');
        host.setAttribute('data-luna-devtools', '');
        // Zero-size host: the pill and the panel position themselves (pill bottom-right, panel by its top edge,
        // so buttons stay put while the content changes height).
        host.style.cssText = 'position:fixed;z-index:2147483647;left:0;top:0;width:0;height:0;';
        var root = host.attachShadow ? host.attachShadow({ mode: 'open' }) : host;

        var css = [
            ':host{all:initial}',
            '*{box-sizing:border-box;font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif}',
            '.pill{position:fixed;right:8px;bottom:8px;background:rgba(20,22,28,.78);color:#cfd6e4;border:1px solid rgba(255,255,255,.12);border-radius:14px;padding:3px 9px;font-size:11px;cursor:pointer;user-select:none}',
            '.pill:hover{background:rgba(30,34,44,.95)}',
            '.panel{position:fixed;right:8px;top:8px;width:min(400px,94vw);max-height:calc(100vh - 16px);display:flex;flex-direction:column;background:rgba(18,20,26,.96);color:#d7dce6;border:1px solid rgba(255,255,255,.12);border-radius:8px;box-shadow:0 6px 24px rgba(0,0,0,.45);font-size:11.5px;overflow:hidden}',
            '.head{display:flex;align-items:center;gap:6px;padding:6px 8px;background:rgba(255,255,255,.04);cursor:move;user-select:none}',
            '.title{font-weight:600;font-size:12px;flex:1}',
            '.x{cursor:pointer;opacity:.7;padding:0 4px}.x:hover{opacity:1}',
            '.tabs{display:flex;border-bottom:1px solid rgba(255,255,255,.08)}',
            '.tab{flex:1;text-align:center;padding:5px 0;cursor:pointer;opacity:.65}.tab.on{opacity:1;box-shadow:inset 0 -2px #4f8ff7}.tab.bad{color:#ef6a5a}',
            '.body{overflow:auto;padding:8px;flex:1}',
            '.grid{display:grid;grid-template-columns:auto 1fr;gap:2px 10px;margin:4px 0 8px}',
            '.k{opacity:.62}.v{font-variant-numeric:tabular-nums;text-align:right}',
            '.sec{margin:8px 0 4px;font-weight:600;font-size:11px;opacity:.85;text-transform:uppercase;letter-spacing:.04em}',
            'canvas{width:100%;height:56px;display:block;background:rgba(255,255,255,.03);border-radius:4px}',
            'button{background:#2b3140;color:#dfe5f0;border:1px solid rgba(255,255,255,.12);border-radius:4px;padding:3px 8px;font-size:11px;cursor:pointer}',
            'button:hover{background:#364056}button.on{background:#2f5fb3;border-color:#4f8ff7}',
            'button:disabled{opacity:.4;cursor:default}',
            '.row{display:flex;gap:4px;flex-wrap:wrap;align-items:center;margin:4px 0}',
            'input[type=range]{width:100%}',
            'input[type=text]{flex:1;min-width:0;background:#11141a;color:#dfe5f0;border:1px solid rgba(255,255,255,.15);border-radius:4px;padding:3px 6px;font-size:11px}',
            'table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}',
            'td,th{padding:2px 4px;text-align:right;white-space:nowrap}th{opacity:.6;font-weight:500}',
            'td:first-child,th:first-child{text-align:left;max-width:150px;overflow:hidden;text-overflow:ellipsis}',
            'tr.click{cursor:pointer}tr.click:hover td{background:rgba(79,143,247,.15)}tr.sel td{background:rgba(79,143,247,.3)}',
            '.note{opacity:.6;font-size:10.5px;line-height:1.35;margin:4px 0}',
            '.warn{color:#f0b54a}.bad{color:#ef6a5a}.good{color:#6cc070}',
            '.scroll{max-height:260px;overflow:auto;border:1px solid rgba(255,255,255,.06);border-radius:4px}',
            '.log{border-left:3px solid #ef6a5a;padding:3px 6px;margin:4px 0;background:rgba(255,255,255,.03);border-radius:3px}',
            '.log.warning{border-left-color:#f0b54a}',
            '.log .msg{white-space:pre-wrap;word-break:break-word}',
            '.log .cs{color:#8fb8ff;margin-top:2px;word-break:break-word}',
            'details summary{cursor:pointer;opacity:.6;font-size:10.5px}',
            'pre{white-space:pre-wrap;word-break:break-all;font-size:10px;opacity:.75;margin:2px 0}',
            '.inner{padding:2px 0 4px 10px;font-size:10.5px;opacity:.8}'
        ].join('\n');

        var where = inPlayable ? 'playable.' : 'shell.';
        var open = load(where + 'open', false);
        var tab = load('tab', 'stats');
        var pos = load(where + 'pos', null);

        root.innerHTML = '<style>' + css + '</style><div class="pill" title="Luna Dev Tools (` key)">◆ Dev Tools</div>' +
            '<div class="panel" style="display:none">' +
            '<div class="head"><span class="title">Luna Dev Tools</span><span class="x" title="Hide (` key)">✕</span></div>' +
            '<div class="tabs"><div class="tab" data-tab="stats">Stats</div><div class="tab" data-tab="frame">Frame</div>' +
            '<div class="tab" data-tab="hot">Hot paths</div><div class="tab" data-tab="errors">Errors</div><div class="tab" data-tab="controls">Controls</div></div>' +
            '<div class="body"></div></div>';
        (doc.body || doc.documentElement).appendChild(host);

        var pill = root.querySelector('.pill'), panel = root.querySelector('.panel'), body = root.querySelector('.body');

        function setOpen(o) {
            open = o;
            save(where + 'open', o);
            pill.style.display = o ? 'none' : '';
            panel.style.display = o ? 'flex' : 'none';
            if (o) render(true);
        }
        pill.onclick = function () { setOpen(true); };
        root.querySelector('.x').onclick = function () { setOpen(false); };

        // Drag by the header; position (right, top) kept per page.
        (function () {
            var head = root.querySelector('.head'), drag = null;
            function place(r, t) {
                panel.style.right = Math.max(0, Math.min(hostWin.innerWidth - 80, r)) + 'px';
                panel.style.top = Math.max(0, Math.min(hostWin.innerHeight - 40, t)) + 'px';
            }
            if (pos && typeof pos.t === 'number') place(pos.r, pos.t);
            head.addEventListener('pointerdown', function (e) {
                if (e.target.classList.contains('x')) return;
                var rect = panel.getBoundingClientRect();
                drag = { x: e.clientX, y: e.clientY, r: hostWin.innerWidth - rect.right, t: rect.top };
                head.setPointerCapture(e.pointerId);
            });
            head.addEventListener('pointermove', function (e) {
                if (drag) place(drag.r - (e.clientX - drag.x), drag.t + (e.clientY - drag.y));
            });
            head.addEventListener('pointerup', function () {
                if (!drag) return;
                drag = null;
                save(where + 'pos', { r: parseFloat(panel.style.right), t: parseFloat(panel.style.top) });
            });
        })();

        root.querySelectorAll('.tab').forEach(function (t) {
            t.onclick = function () { tab = t.getAttribute('data-tab'); save('tab', tab); render(true); };
        });

        function onKey(e) {
            if (e.key === '`' && !e.ctrlKey && !e.metaKey && !e.altKey && !/input|textarea/i.test((e.target && e.target.tagName) || '')) setOpen(!open);
        }
        hostWin.addEventListener('keydown', onKey);
        var keyedWindows = new WeakSet();

        // ---------- Formatting ----------
        function ms(v) { return v ? v.toFixed(v < 10 ? 2 : 1) + ' ms' : '–'; }
        function n(v) { return v === undefined || v === null ? '–' : Math.round(v).toLocaleString(); }
        function mb(v) { return v === null || v === undefined ? 'n/a' : (v / 1048576).toFixed(1) + ' MB'; }
        function kb(v) { return v === null || v === undefined ? '–' : v >= 1048576 ? (v / 1048576).toFixed(2) + ' MB' : (v / 1024).toFixed(1) + ' KB'; }
        function cls(v, warn, bad) { return v >= bad ? 'bad' : v >= warn ? 'warn' : ''; }
        function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

        function grid(rows) {
            return '<div class="grid">' + rows.map(function (r) {
                return '<span class="k" title="' + esc(r[2] || '') + '">' + r[0] + '</span><span class="v ' + (r[3] || '') + '">' + r[1] + '</span>';
            }).join('') + '</div>';
        }

        function graph(canvas, ring, scaleMax, lines, color) {
            var w = canvas.clientWidth || 300, h = canvas.clientHeight || 56;
            if (canvas.width !== w) canvas.width = w;
            if (canvas.height !== h) canvas.height = h;
            var g = canvas.getContext('2d');
            g.clearRect(0, 0, w, h);
            var v = ring.values();
            if (!v.length) return;
            var max = scaleMax || 1;
            for (var i = 0; i < v.length; i++) if (v[i] > max) max = v[i];
            g.strokeStyle = 'rgba(255,255,255,.12)';
            (lines || []).forEach(function (y) {
                var py = h - (y / max) * h;
                g.beginPath(); g.moveTo(0, py); g.lineTo(w, py); g.stroke();
            });
            g.strokeStyle = color;
            g.beginPath();
            for (var j = 0; j < v.length; j++) {
                var x = (j / Math.max(1, ring.size - 1)) * w, y = h - (v[j] / max) * h;
                if (j) g.lineTo(x, y); else g.moveTo(x, y);
            }
            g.stroke();
        }

        // ---------- Tabs ----------
        var selectedDraw = -1, actionMessage = '', endCardName = load('endCard', 'EndCard'), seenCallbacks = [];
        var hotFilter = load('hotFilter', 'project'), hotByClass = load('hotByClass', false), expandedScript = null;

        function statsTab(a, s) {
            var e = s.engine || {};
            var fps = s.interval.avg ? 1000 / s.interval.avg : 0;
            var html = '<div class="sec">Frame time <span class="note">(lines: 16.7 / 33.3 ms)</span></div><canvas data-g="frame"></canvas>' +
                grid([
                    ['FPS', fps ? fps.toFixed(1) : '–', 'From the browser frame interval', cls(-fps, -50, -30)],
                    ['Frame (CPU)', ms(s.frameMs.avg), 'Engine frame: scripts + animation + physics + render submission'],
                    ['p95 / worst', ms(s.frameMs.p95) + ' / ' + ms(s.frameMs.max), 'Last 240 frames: spikes show here, not in the average', cls(s.frameMs.max, 33, 50)],
                    ['Interval p95 / worst', ms(s.interval.p95) + ' / ' + ms(s.interval.max), 'Time between displayed frames (includes GPU and browser)', cls(s.interval.max, 33, 50)]
                ]);
            html += '<div class="sec">Where the frame goes</div>' + grid([
                ['Scripts', ms(e.scripts)], ['Animations', ms(e.animations)], ['Animators', ms(e.animators)],
                ['Physics', ms(e.physics)], ['Render', ms(e.render)]
            ]);
            html += '<div class="sec">Rendering (engine)</div>' + grid([
                ['Draw calls', n(e.drawCalls), 'Recorded by the Luna renderer', cls(e.drawCalls, 100, 200)],
                ['Triangles', n(e.triangles)], ['Vertices', n(e.vertices)],
                ['Material switches', n(e.materialSwitches)], ['UI elements', n(e.uiElements)],
                ['Particles / systems', n(e.particles) + ' / ' + n(e.particleSystems)],
                ['Skinned meshes (visible)', n(e.skinned)], ['Animators', n(e.animatorsCount)], ['Shadow casters', n(e.shadowCasters)]
            ]);
            var g = s.gl || {};
            html += '<div class="sec">WebGL (per frame)</div>' + grid([
                ['GL draw calls', n(g.draws) + (g.skipped ? ' <span class="warn">(' + g.skipped + ' skipped)</span>' : ''), 'Every draw call sent to WebGL, including post effects and UI'],
                ['Shader switches', n(g.programs), 'useProgram with a different program'],
                ['Texture binds', n(g.texBinds)], ['Framebuffer binds', n(g.fbBinds)],
                ['Uploads this frame', kb(g.uploadBytes), 'Texture and buffer data sent to the GPU', cls(g.uploadBytes || 0, 262144, 1048576)],
                ['Uploads avg / worst', kb(s.uploads.avg) + ' / ' + kb(s.uploads.max), 'Spikes here mid-game cause hitches'],
                ['GPU memory (est.)', mb(s.glMemory.textures + s.glMemory.buffers), 'Textures ' + mb(s.glMemory.textures) + ', buffers ' + mb(s.glMemory.buffers) + ' (estimate from uploads)']
            ]);
            html += '<div class="sec">JS heap</div><canvas data-g="heap"></canvas>' + grid([
                ['Used', mb(s.heap), performance.memory ? '' : 'Chrome only (performance.memory)'],
                ['Allocation rate', s.heap === null ? 'n/a' : mb(s.allocRate) + '/s', 'Heap growth between garbage collections. A steady sawtooth means allocations every frame', cls(s.allocRate, 1048576, 4194304)]
            ]);
            if (s.errors && s.errors.length) html += '<div class="note warn">' + s.errors.map(esc).join('<br>') + '</div>';
            return html;
        }

        function frameTab(a, s) {
            var max = a.gl.maxDraws();
            var limit = a.gl.limit();
            var shown = limit === Infinity ? max : Math.min(limit, max);
            var html = '<div class="sec">Draw calls, step by step</div>' +
                '<div class="note">Drag to draw only the first N calls of each frame, like Unity\'s Frame Debugger. ' +
                'Pause first to step through one frame. The game itself keeps running normally.</div>' +
                '<input type="range" min="0" max="' + max + '" value="' + shown + '" data-act="limit">' +
                '<div class="row"><span>' + (limit === Infinity ? 'All ' + max + ' draws' : 'Draws 0–' + (shown - 1) + ' of ' + max) + '</span>' +
                '<span style="flex:1"></span><button data-act="prev">◀</button><button data-act="next">▶</button>' +
                '<button data-act="all"' + (limit === Infinity ? ' disabled' : '') + '>Show all</button></div>';
            html += '<div class="row"><button data-act="pause" class="' + (a.controls.paused() ? 'on' : '') + '">' + (a.controls.paused() ? 'Resume' : 'Pause') + '</button>' +
                '<button data-act="step">Step frame</button><button data-act="capture">Capture frame</button>' +
                '<button data-act="spector" title="Full WebGL capture (state, shaders, textures) with Spector.js, loaded from jsDelivr">Spector.js</button></div>';
            var capture = a.gl.capture();
            if (capture) {
                html += '<div class="sec">Captured frame: ' + capture.length + ' draws</div><div class="scroll"><table><tr><th>#  Shader</th><th>Count</th><th>Target</th><th>B/D/C</th></tr>';
                capture.forEach(function (d) {
                    html += '<tr class="click' + (d.index === selectedDraw ? ' sel' : '') + '" data-draw="' + d.index + '" title="' +
                        esc(d.kind + (d.instances > 1 ? ' ×' + d.instances : '') + '\nTextures: ' + (d.textures || '-')) + '"><td>' + d.index + '  ' + esc(d.program) + '</td><td>' +
                        n(d.count) + (d.instances > 1 ? '×' + d.instances : '') + '</td><td>' + esc(d.target) + '</td><td>' +
                        (d.blend ? 'B' : '·') + (d.depth ? 'D' : '·') + (d.cull ? 'C' : '·') + '</td></tr>';
                });
                html += '</table></div><div class="note">Click a row to show the frame up to that draw. B/D/C: blend, depth test, culling.</div>';
                if (selectedDraw >= 0 && capture[selectedDraw]) {
                    var d = capture[selectedDraw];
                    html += grid([['Draw', '#' + d.index + ' ' + esc(d.kind)], ['Shader', esc(d.program)], ['Count', n(d.count) + (d.instances > 1 ? ' × ' + d.instances : '')],
                        ['Target', esc(d.target)], ['Textures', esc(d.textures || '-')]]);
                }
            }
            if (actionMessage) html += '<div class="note">' + esc(actionMessage) + '</div>';
            return html;
        }

        function perFrame(v) { return v < 10 ? v.toFixed(2) : n(v); }

        function hotTab(a, s) {
            var html = '';
            if (!a.hot.available()) {
                return '<div class="note warn">Hot paths need the develop build\'s TRACE calls, which were not found.</div>';
            }
            html += '<div class="row"><button data-act="record" class="' + (a.hot.recording() ? 'on' : '') + '">' + (a.hot.recording() ? 'Stop' : 'Record') + '</button>' +
                '<button data-act="hotreset">Reset</button><span class="note">' + n(a.hot.frames()) + ' frames recorded</span></div>';

            // Scripts: where the frame's script time and engine calls come from.
            html += '<div class="sec">Scripts (per frame)</div>';
            if (!a.scripts.installed()) html += '<div class="note">' + esc(a.scripts.note() || 'Script hooks not installed') + '</div>';
            var scriptRows = a.scripts.top(30, 8);
            if (scriptRows.length) {
                html += '<div class="scroll"><table><tr><th>Script.callback</th><th title="Time in the callback itself">self ms</th>' +
                    '<th title="Including callbacks it triggered (OnEnable of objects it turned on, ...)">total</th>' +
                    '<th title="Worst frame, self time">worst</th><th title="Traced calls made inside, engine included">calls</th></tr>';
                scriptRows.forEach(function (r) {
                    var open = expandedScript === r.name;
                    html += '<tr class="click' + (open ? ' sel' : '') + '" data-script="' + esc(r.name) + '" title="' + esc(r.name +
                        (r.objects.length ? '\nOn: ' + r.objects.join(', ') : '') + '\n' + perFrame(r.callsPerFrame) + ' call(s) / frame') + '"><td>' + esc(r.name) +
                        '</td><td class="' + cls(r.selfMs, 1, 3) + '">' + r.selfMs.toFixed(3) + '</td><td>' + r.totalMs.toFixed(3) +
                        '</td><td>' + r.worstMs.toFixed(2) + '</td><td class="' + cls(r.tracesPerFrame, 500, 2000) + '">' + perFrame(r.tracesPerFrame) + '</td></tr>';
                    if (open) {
                        html += '<tr><td colspan="5"><div class="inner">' + (r.objects.length ? 'On: ' + esc(r.objects.join(', ')) + '<br>' : '') +
                            (r.inner.length ? r.inner.map(function (i) {
                                return (i.project ? '<b>' : '') + esc(i.name) + (i.project ? '</b>' : '') + ' — ' + perFrame(i.perFrame) + '/frame';
                            }).join('<br>') : 'No traced call inside') + '</div></td></tr>';
                    }
                });
                html += '</table></div><div class="note">Click a row for the calls made inside it (project methods in bold). ' +
                    'Engine work such as Transform.handle is counted on the script that triggered it.</div>';
            } else if (a.scripts.installed()) {
                html += '<div class="note">Press Record and play.</div>';
            }

            // Methods, project only by default.
            html += '<div class="sec">Most called methods</div><div class="row">' +
                '<button data-act="hotfilter" class="' + (hotFilter === 'project' ? 'on' : '') + '">Project only</button>' +
                '<button data-act="hotclass" class="' + (hotByClass ? 'on' : '') + '">By class</button></div>';
            var rows = a.hot.top(40, hotFilter, hotByClass);
            if (rows.length) {
                html += '<div class="scroll"><table><tr><th>' + (hotByClass ? 'Class' : 'Class#method') + '</th><th>/frame</th><th>peak</th><th>total</th></tr>';
                rows.forEach(function (r) {
                    html += '<tr title="' + esc(r.name + (r.methods ? '\n' + r.methods + ' method(s)' : '')) + '"><td>' + esc(r.name) + '</td><td class="' + cls(r.perFrame, 100, 1000) + '">' +
                        perFrame(r.perFrame) + '</td><td>' + n(r.peak) + '</td><td>' + n(r.total) + '</td></tr>';
                });
                html += '</table></div>';
            } else {
                html += '<div class="note">' + (a.hot.frames() ? 'No ' + (hotFilter === 'project' ? 'project ' : '') + 'call recorded.' : 'Press Record and play: every method call is counted per frame.') + '</div>';
            }
            var b = s.breakdown;
            if (b) {
                // A callback that showed up once keeps its row (fixed order), so the list does not jump.
                Object.keys(b).forEach(function (k) {
                    if (k.indexOf('.') > 0 && b[k] > 0.005 && seenCallbacks.indexOf(k) < 0) seenCallbacks.push(k);
                });
                seenCallbacks.sort();
                html += '<div class="sec">Script &amp; engine callbacks (ms / frame)</div>' +
                    (seenCallbacks.length ? grid(seenCallbacks.map(function (k) { return [esc(k), ms(b[k])]; })) : '<div class="note">No callback time yet.</div>');
            }
            html += '<div class="note">Time per method: Chrome DevTools (F12) → Performance → Record while playing, then Bottom-Up. ' +
                'Project methods keep their C# names (Namespace.Class.Method).</div>';
            return html;
        }

        function errorsTab(a) {
            var list = a.console.list().slice().sort(function (x, y) { return (x.level === y.level ? 0 : x.level === 'error' ? -1 : 1) || y.count - x.count; });
            var html = '<div class="row"><button data-act="copylogs"' + (list.length ? '' : ' disabled') + '>Copy all</button>' +
                '<button data-act="clearlogs"' + (list.length ? '' : ' disabled') + '>Clear</button>' +
                '<span class="note">' + n(a.console.total()) + ' message(s), ' + list.length + ' distinct</span></div>';
            if (!list.length) return html + '<div class="note">No error or warning logged by the playable.</div>';
            list.slice(0, 60).forEach(function (e) {
                html += '<div class="log ' + e.level + '"><div class="msg">×' + n(e.count) + '  ' + esc(e.message) + '</div>' +
                    (e.csharp ? '<div class="cs">C#: ' + e.csharp.slice(0, 4).map(function (c) { return esc(a.console.shortName(c)); }).join(' ← ') + '</div>' : '') +
                    (e.stack ? '<details><summary>stack · first at frame ' + n(e.firstFrame) + '</summary><pre>' + esc(e.stack.split('\n').slice(0, 12).join('\n')) + '</pre>' +
                        (e.csharp ? '<pre>' + esc(e.csharp.join('\n')) + '</pre>' : '') + '</details>' : '') + '</div>';
            });
            if (actionMessage) html += '<div class="note">' + esc(actionMessage) + '</div>';
            return html;
        }

        function controlsTab(a) {
            var paused = a.controls.paused(), scale = a.controls.scale();
            var scales = [0.25, 0.5, 1, 2, 4, 8];
            var html = '<div class="sec">Time</div><div class="row"><button data-act="pause" class="' + (paused ? 'on' : '') + '">' + (paused ? 'Resume' : 'Pause') + '</button>' +
                '<button data-act="step">Step frame</button></div><div class="row">' +
                scales.map(function (x) { return '<button data-scale="' + x + '" class="' + (x === scale ? 'on' : '') + '">×' + x + '</button>'; }).join('') + '</div>' +
                '<div class="note">Speeds the engine clock up or down (×4/×8 to reach the end quickly). Audio keeps its normal speed.</div>';
            html += '<div class="sec">Playable</div><div class="row"><button data-act="restart">Restart</button></div>';
            html += '<div class="sec">End card / any object</div><div class="row"><input type="text" data-in="endcard" value="' + esc(endCardName) + '" placeholder="GameObject name">' +
                '<button data-act="activate">Activate</button></div>' +
                '<div class="note">Turns the named object on (with its inactive parents) and runs its scripts as real play would. ' +
                'Luna\'s own GameEnded / CTA calls only log in Build Develop, so they cannot open a game\'s end card.</div>';
            html += '<div class="sec">Panel</div><div class="note">` (backquote) shows or hides the panel. It starts hidden and costs almost nothing while hidden.</div>';
            if (actionMessage) html += '<div class="note">' + esc(actionMessage) + '</div>';
            return html;
        }

        var lastRender = 0, renderedTab = null, sliding = false;

        // Updates `from` in place to match `to`: elements under the pointer, focus, scroll positions,
        // tooltips and half-done clicks survive the periodic refresh. Returns false when the node kinds differ.
        function morph(from, to) {
            if (from.nodeType !== to.nodeType || from.nodeName !== to.nodeName) return false;
            if (from.nodeType === 3) {
                if (from.nodeValue !== to.nodeValue) from.nodeValue = to.nodeValue;
                return true;
            }
            if (from.nodeType !== 1) return true;
            var i, attr;
            for (i = 0; i < to.attributes.length; i++) {
                attr = to.attributes[i];
                if (attr.name === 'value' && from.tagName === 'INPUT') {
                    var busy = from === root.activeElement && (from.type === 'text' || sliding);
                    if (!busy && from.value !== attr.value) from.value = attr.value;
                    continue;
                }
                if (from.getAttribute(attr.name) !== attr.value) from.setAttribute(attr.name, attr.value);
            }
            for (i = from.attributes.length - 1; i >= 0; i--) {
                attr = from.attributes[i];
                // A <details> the user opened stays open across refreshes.
                if (!to.hasAttribute(attr.name) && !(attr.name === 'open' && from.tagName === 'DETAILS')) from.removeAttribute(attr.name);
            }
            // Common children are updated in place; only the tail is added or removed.
            var a = from.childNodes, b = to.childNodes, common = Math.min(a.length, b.length);
            for (i = 0; i < common; i++) if (!morph(a[i], b[i])) from.replaceChild(b[i].cloneNode(true), a[i]);
            while (from.childNodes.length > b.length) from.removeChild(from.lastChild);
            for (i = common; i < b.length; i++) from.appendChild(b[i].cloneNode(true));
            return true;
        }

        function show(html) {
            var next = doc.createElement('div');
            next.innerHTML = html;
            if (renderedTab === tab && body.firstChild && morph(body.firstChild, next)) return;
            while (body.firstChild) body.removeChild(body.firstChild);
            body.appendChild(next);
            renderedTab = tab;
        }

        function render() {
            if (!open) return;
            root.querySelectorAll('.tab').forEach(function (t) { t.classList.toggle('on', t.getAttribute('data-tab') === tab); });
            var a = getAgent();
            if (!a) { show('<div class="note">Waiting for the playable…</div>'); return; }
            if (a.window !== hostWin && !keyedWindows.has(a.window)) {
                keyedWindows.add(a.window);
                try { a.window.addEventListener('keydown', onKey); } catch (e) { /* Hotkey then only works here. */ }
            }
            var s = a.snapshot();
            if (!s.ready) { show('<div class="note">Waiting for the Luna engine to start…</div>'); return; }

            var errorCount = a.console.errorCount(), errorTab = root.querySelector('.tab[data-tab=errors]');
            var errorLabel = errorCount ? 'Errors (' + errorCount + ')' : 'Errors';
            if (errorTab.textContent !== errorLabel) errorTab.textContent = errorLabel;
            errorTab.classList.toggle('bad', errorCount > 0);

            show(tab === 'frame' ? frameTab(a, s) : tab === 'hot' ? hotTab(a, s) : tab === 'errors' ? errorsTab(a) :
                tab === 'controls' ? controlsTab(a) : statsTab(a, s));

            var fg = body.querySelector('canvas[data-g=frame]');
            if (fg) graph(fg, s.frameHistory, 34, [16.7, 33.3], '#4f8ff7');
            var hg = body.querySelector('canvas[data-g=heap]');
            if (hg) graph(hg, s.heapHistory, 1, [], '#6cc070');
            bind(a);
        }

        function bind(a) {
            body.querySelectorAll('[data-act]').forEach(function (el) {
                var act = el.getAttribute('data-act');
                if (act === 'limit') {
                    el.onpointerdown = function () { sliding = true; };
                    el.oninput = function () {
                        var v = parseInt(el.value, 10);
                        selectedDraw = v - 1;
                        a.gl.setLimit(v >= a.gl.maxDraws() ? null : v);
                    };
                    el.onchange = function () { sliding = false; render(); };
                    return;
                }
                el.onclick = function () { act && action(a, act, el); };
            });
            body.querySelectorAll('[data-scale]').forEach(function (el) {
                el.onclick = function () { a.controls.setScale(parseFloat(el.getAttribute('data-scale'))); render(true); };
            });
            body.querySelectorAll('tr[data-script]').forEach(function (el) {
                el.onclick = function () {
                    var name = el.getAttribute('data-script');
                    expandedScript = expandedScript === name ? null : name;
                    render(true);
                };
            });
            body.querySelectorAll('tr[data-draw]').forEach(function (el) {
                el.onclick = function () {
                    selectedDraw = parseInt(el.getAttribute('data-draw'), 10);
                    a.gl.setLimit(selectedDraw + 1);
                    render(true);
                };
            });
            var input = body.querySelector('[data-in=endcard]');
            if (input) input.oninput = function () { endCardName = input.value; save('endCard', endCardName); };
        }

        function action(a, act) {
            actionMessage = '';
            if (act === 'pause') a.controls.setPaused(!a.controls.paused()) || (actionMessage = 'Engine not ready');
            else if (act === 'step') a.controls.step() || (actionMessage = 'Engine not ready');
            else if (act === 'restart') a.controls.restart();
            else if (act === 'all') { selectedDraw = -1; a.gl.setLimit(null); }
            else if (act === 'prev' || act === 'next') {
                var max = a.gl.maxDraws(), cur = a.gl.limit() === Infinity ? max : a.gl.limit();
                var next = Math.max(0, Math.min(max, cur + (act === 'next' ? 1 : -1)));
                selectedDraw = next - 1;
                a.gl.setLimit(next >= max ? null : next);
            }
            else if (act === 'capture') a.gl.captureFrame(function () { selectedDraw = -1; render(true); });
            else if (act === 'record') a.hot.setRecording(!a.hot.recording());
            else if (act === 'hotreset') { a.hot.reset(); expandedScript = null; }
            else if (act === 'hotfilter') { hotFilter = hotFilter === 'project' ? 'all' : 'project'; save('hotFilter', hotFilter); }
            else if (act === 'hotclass') { hotByClass = !hotByClass; save('hotByClass', hotByClass); }
            else if (act === 'clearlogs') a.console.clear();
            else if (act === 'copylogs') copyText(a.console.report());
            else if (act === 'activate') actionMessage = a.controls.activate(endCardName.trim());
            else if (act === 'spector') loadSpector(a);
            render(true);
        }

        function copyText(text) {
            var done = function () { actionMessage = 'Copied ' + text.length + ' characters.'; render(true); };
            var fallback = function () {
                var area = doc.createElement('textarea');
                area.value = text;
                area.style.cssText = 'position:fixed;left:-9999px;top:0';
                root.appendChild(area);
                area.select();
                try { doc.execCommand('copy'); done(); } catch (e) { actionMessage = 'Copy failed: ' + (e && e.message || e); }
                root.removeChild(area);
            };
            try {
                if (hostWin.navigator.clipboard && hostWin.isSecureContext) hostWin.navigator.clipboard.writeText(text).then(done, fallback);
                else fallback();
            } catch (e) { fallback(); }
        }

        // Spector.js has to run in the playable's own page: it does not recognise a canvas from another window.
        function loadSpector(a) {
            var w = a.window, canvas = a.gl.canvas();
            if (!canvas) { actionMessage = 'No WebGL canvas yet'; return; }
            var start = function () {
                try {
                    var spector = w.__lunaSpector || (w.__lunaSpector = new w.SPECTOR.Spector());
                    spector.displayUI();
                    spector.captureCanvas(canvas);
                    actionMessage = 'Spector.js is capturing the next frame' + (a.controls.paused() ? ' (resume to let it draw one)' : '') +
                        '. Its window opens inside the playable: for more room, open ' + w.location.origin + '/iframe.html on its own.';
                } catch (e) { actionMessage = 'Spector.js failed: ' + (e && e.message || e); }
                render(true);
            };
            if (w.SPECTOR) return start();
            actionMessage = 'Loading Spector.js…';
            var tag = w.document.createElement('script');
            tag.src = 'https://cdn.jsdelivr.net/npm/spectorjs@0.9.30/dist/spector.bundle.js';
            tag.onload = start;
            tag.onerror = function () { actionMessage = 'Could not load Spector.js (needs internet access).'; render(true); };
            w.document.head.appendChild(tag);
        }

        // ~4 refreshes a second, only while open: the panel shares the main thread with the game.
        setInterval(function () {
            if (!open) return;
            var t = realNow();
            if (t - lastRender < 240) return;
            lastRender = t;
            try { render(false); } catch (e) { body.textContent = 'Dev Tools error: ' + (e && e.message || e); }
        }, 250);

        setOpen(open);
        return { setOpen: setOpen };
    }

    // =====================================================================================================
    // Wiring
    // =====================================================================================================
    if (role === 'agent') {
        var agent = createAgent(window);
        agent.window = window;
        window.__lunaDevTools = agent;
        var standalone;
        try { standalone = window.top === window; } catch (e) { standalone = false; }
        if (standalone) {
            var mount = function () { mountPanel(window, function () { return agent; }, true); };
            if (document.body) mount(); else document.addEventListener('DOMContentLoaded', mount);
        }
    } else {
        window.__lunaDevTools = { shell: true };
        // The playable runs in a same-origin iframe of the Dev Environment page; it can be reloaded at any time.
        var findAgent = function () {
            var frames = document.querySelectorAll('iframe');
            for (var i = 0; i < frames.length; i++) {
                try {
                    var w = frames[i].contentWindow;
                    if (w && w.__lunaDevTools && w.__lunaDevTools.snapshot) return w.__lunaDevTools;
                } catch (e) { /* Other origin. */ }
            }
            return null;
        };
        var mountShell = function () { mountPanel(window, findAgent, false); };
        if (document.body) mountShell(); else document.addEventListener('DOMContentLoaded', mountShell);
    }
})();
