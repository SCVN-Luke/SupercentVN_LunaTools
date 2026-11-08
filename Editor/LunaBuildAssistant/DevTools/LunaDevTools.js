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
        var hot = { recording: false, current: new Map(), total: new Map(), peak: new Map(), frames: 0, installed: false };
        try {
            var traceTarget = win.TRACE;
            var traceProxy = function (name, self) {
                if (hot.recording && typeof name === 'string') hot.current.set(name, (hot.current.get(name) || 0) + 1);
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
            if (!hot.recording || hot.current.size === 0) return;
            hot.frames++;
            hot.current.forEach(function (count, name) {
                hot.total.set(name, (hot.total.get(name) || 0) + count);
                if (count > (hot.peak.get(name) || 0)) hot.peak.set(name, count);
            });
            hot.current.clear();
        }

        agent.hot = {
            available: function () { return hot.installed && !!traceTarget; },
            recording: function () { return hot.recording; },
            setRecording: function (on) { hot.recording = !!on; if (!on) hot.current.clear(); },
            reset: function () { hot.current.clear(); hot.total.clear(); hot.peak.clear(); hot.frames = 0; },
            frames: function () { return hot.frames; },
            top: function (n) {
                var rows = [];
                hot.total.forEach(function (total, name) {
                    rows.push({ name: name, total: total, perFrame: hot.frames ? total / hot.frames : 0, peak: hot.peak.get(name) || 0 });
                });
                rows.sort(function (a, b) { return b.total - a.total; });
                return rows.slice(0, n);
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
            '.tab{flex:1;text-align:center;padding:5px 0;cursor:pointer;opacity:.65}.tab.on{opacity:1;box-shadow:inset 0 -2px #4f8ff7}',
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
            '.scroll{max-height:260px;overflow:auto;border:1px solid rgba(255,255,255,.06);border-radius:4px}'
        ].join('\n');

        var where = inPlayable ? 'playable.' : 'shell.';
        var open = load(where + 'open', false);
        var tab = load('tab', 'stats');
        var pos = load(where + 'pos', null);

        root.innerHTML = '<style>' + css + '</style><div class="pill" title="Luna Dev Tools (` key)">◆ Dev Tools</div>' +
            '<div class="panel" style="display:none">' +
            '<div class="head"><span class="title">Luna Dev Tools</span><span class="x" title="Hide (` key)">✕</span></div>' +
            '<div class="tabs"><div class="tab" data-tab="stats">Stats</div><div class="tab" data-tab="frame">Frame</div>' +
            '<div class="tab" data-tab="hot">Hot paths</div><div class="tab" data-tab="controls">Controls</div></div>' +
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

        function hotTab(a, s) {
            var html = '<div class="sec">Most called methods</div>';
            if (!a.hot.available()) {
                html += '<div class="note warn">Call counts need the develop build\'s TRACE calls, which were not found.</div>';
            } else {
                html += '<div class="row"><button data-act="record" class="' + (a.hot.recording() ? 'on' : '') + '">' + (a.hot.recording() ? 'Stop' : 'Record') + '</button>' +
                    '<button data-act="hotreset">Reset</button><span class="note">' + n(a.hot.frames()) + ' frames recorded</span></div>';
                var rows = a.hot.top(40);
                if (rows.length) {
                    html += '<div class="scroll"><table><tr><th>Class#method</th><th>/frame</th><th>peak</th><th>total</th></tr>';
                    rows.forEach(function (r) {
                        html += '<tr title="' + esc(r.name) + '"><td>' + esc(r.name) + '</td><td class="' + cls(r.perFrame, 100, 1000) + '">' +
                            (r.perFrame < 10 ? r.perFrame.toFixed(2) : n(r.perFrame)) + '</td><td>' + n(r.peak) + '</td><td>' + n(r.total) + '</td></tr>';
                    });
                    html += '</table></div>';
                } else {
                    html += '<div class="note">Press Record and play: every project method call is counted per frame.</div>';
                }
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
                if (!to.hasAttribute(attr.name)) from.removeAttribute(attr.name);
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

            show(tab === 'frame' ? frameTab(a, s) : tab === 'hot' ? hotTab(a, s) : tab === 'controls' ? controlsTab(a) : statsTab(a, s));

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
            else if (act === 'hotreset') a.hot.reset();
            else if (act === 'activate') actionMessage = a.controls.activate(endCardName.trim());
            else if (act === 'spector') loadSpector(a);
            render(true);
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
