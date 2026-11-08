using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Supercent.Util.Editor
{
    // Commands for the Dev Tools agents running in playable pages, over HTTP long-polling:
    //   page  -> POST /__luna_devtools/channel?session=ID {hello, results}  (held until a command or the poll timeout)
    //   page  -> POST /__luna_devtools/result?session=ID  {results}         (answers right away)
    //   tools -> Send(session, method, params)  waits for that page's result.
    // Every open tab (desktop, phone) is a session and keeps its id across reloads; each load has its own loadId.
    // A page that stops polling is dropped. Tools default to the most recently loaded page.
    // No Unity API: runs on the proxy's connection threads and is tested outside the Editor.
    public sealed class DevToolsChannel
    {
        public const int PollWaitMs = 20000;
        const double SessionTimeoutSeconds = 45;
        const double AliveSeconds = PollWaitMs / 1000.0 + 5;   // A live page polls at least this often.

        public sealed class SessionInfo
        {
            public string Id;
            public string Url;
            public string Title;
            public string UserAgent;
            public double SecondsSinceSeen;
            public bool EngineReady;
            public int Frame;
            public DateTime ConnectedUtc;
            public string LoadId;
            public DateTime LoadedUtc;
            public int StaticConstructorFailures;
            public bool IsDefault;
        }

        sealed class Pending
        {
            public string Id;
            public string Method;
            public Dictionary<string, object> Params;
            public object Result;
            public string Error;
            public bool Done;
            public bool Sent;
        }

        sealed class Session
        {
            public string Id;
            public Dictionary<string, object> Hello = new Dictionary<string, object>();
            public DateTime LastSeenUtc;
            public DateTime ConnectedUtc;
            public string LoadId;
            public DateTime LoadedUtc;
            public readonly Queue<Pending> Queue = new Queue<Pending>();
            public readonly Dictionary<string, Pending> Waiting = new Dictionary<string, Pending>();
            public int PollGeneration;
        }

        readonly object gate = new object();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        int nextCommand;

        public List<SessionInfo> Sessions()
        {
            lock (gate)
            {
                Expire();
                DateTime now = DateTime.UtcNow;
                Session preferred = Default();
                return sessions.Values.OrderByDescending(s => s.LoadedUtc).Select(s => new SessionInfo
                {
                    Id = s.Id,
                    Url = MiniJson.Str(s.Hello, "url"),
                    Title = MiniJson.Str(s.Hello, "title"),
                    UserAgent = MiniJson.Str(s.Hello, "userAgent"),
                    EngineReady = MiniJson.Bool(s.Hello, "engineReady", false),
                    Frame = (int)MiniJson.Num(s.Hello, "frame", 0),
                    SecondsSinceSeen = (now - s.LastSeenUtc).TotalSeconds,
                    ConnectedUtc = s.ConnectedUtc,
                    LoadId = s.LoadId,
                    LoadedUtc = s.LoadedUtc,
                    StaticConstructorFailures = (int)MiniJson.Num(s.Hello, "staticConstructorFailures", 0),
                    IsDefault = s == preferred,
                }).ToList();
            }
        }

        // The session a tool uses: the given one, or the most recently loaded live page. Throws when there is none.
        public string Resolve(string sessionId)
        {
            lock (gate)
            {
                Expire();
                return Find(sessionId).Id;
            }
        }

        // Waits until the page of a session has loaded again (loadId differs from previousLoadId) and its engine runs.
        public SessionInfo WaitForLoad(string sessionId, string previousLoadId, int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                SessionInfo info = Sessions().FirstOrDefault(s => s.Id == sessionId);
                if (info != null && info.LoadId != previousLoadId && info.EngineReady)
                    return info;
                if (DateTime.UtcNow >= until)
                    throw new TimeoutException(info == null || info.LoadId == previousLoadId
                        ? $"The page did not come back within {timeoutMs / 1000}s (still loading, in a background tab, or closed)."
                        : $"The page reloaded but its engine did not start within {timeoutMs / 1000}s. Check luna_get_errors.");
                lock (gate)
                    Monitor.Wait(gate, 250);
            }
        }

        // Under the lock.
        Session Default()
        {
            DateTime now = DateTime.UtcNow;
            return sessions.Values.Where(s => (now - s.LastSeenUtc).TotalSeconds <= AliveSeconds).OrderByDescending(s => s.LoadedUtc).FirstOrDefault()
                   ?? sessions.Values.OrderByDescending(s => s.LastSeenUtc).FirstOrDefault();
        }

        // Under the lock.
        Session Find(string sessionId)
        {
            Session session = string.IsNullOrEmpty(sessionId) ? Default()
                : sessions.TryGetValue(sessionId, out Session found) ? found : null;
            if (session == null)
                throw new InvalidOperationException(string.IsNullOrEmpty(sessionId)
                    ? "No playable page is connected. Open the Build Develop page through the Dev Tools (Luna Build Assistant > Build Develop > Open with Dev Tools), then retry."
                    : "No connected page with session " + sessionId + ". Call luna_status for the list.");
            return session;
        }

        // Page side: records results and hello, then waits for commands. Returns the JSON reply.
        public string Poll(string sessionId, string body, int waitMs = PollWaitMs)
        {
            Session session;
            int generation;
            lock (gate)
            {
                session = Touch(sessionId, body);
                if (session == null)
                    return "{\"error\":\"missing session\"}";
                generation = ++session.PollGeneration;
                Monitor.PulseAll(gate);          // An older poll of this page returns now: one waiter per page.
                DateTime until = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (session.Queue.Count == 0 && session.PollGeneration == generation)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0)
                        break;
                    Monitor.Wait(gate, left);
                }
                var commands = new List<object>();
                if (session.PollGeneration == generation)
                    while (session.Queue.Count > 0)
                    {
                        Pending p = session.Queue.Dequeue();
                        p.Sent = true;
                        commands.Add(new Dictionary<string, object> { { "id", p.Id }, { "method", p.Method }, { "params", p.Params } });
                    }
                return MiniJson.Write(new Dictionary<string, object> { { "commands", commands } });
            }
        }

        // Page side: results only, answered immediately.
        public string Results(string sessionId, string body)
        {
            lock (gate)
            {
                Session session = Touch(sessionId, body);
                Monitor.PulseAll(gate);         // A new hello (e.g. engine started) can end a WaitForLoad.
                return session == null ? "{\"error\":\"missing session\"}" : "{\"ok\":true}";
            }
        }

        // Tool side: runs a command in a page and waits for its result. session null = the most recently loaded page.
        public object Send(string sessionId, string method, Dictionary<string, object> parameters, int timeoutMs)
        {
            Pending p;
            Session session;
            lock (gate)
            {
                Expire();
                session = Find(sessionId);
                p = new Pending { Id = "c" + (++nextCommand), Method = method, Params = parameters ?? new Dictionary<string, object>() };
                session.Queue.Enqueue(p);
                session.Waiting[p.Id] = p;
                Monitor.PulseAll(gate);
                DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (!p.Done)
                {
                    int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0)
                        break;
                    Monitor.Wait(gate, left);
                }
                session.Waiting.Remove(p.Id);
                if (!p.Done)
                {
                    if (!p.Sent)
                        RemoveQueued(session, p);
                    throw new TimeoutException(p.Sent
                        ? $"The page did not answer '{method}' within {timeoutMs / 1000}s."
                        : $"The page did not pick up '{method}' within {timeoutMs / 1000}s (tab in the background, reloading, or closed).");
                }
            }
            if (p.Error != null)
                throw new InvalidOperationException(p.Error);
            return p.Result;
        }

        static void RemoveQueued(Session session, Pending p)
        {
            var keep = session.Queue.Where(q => q != p).ToList();
            session.Queue.Clear();
            foreach (Pending q in keep)
                session.Queue.Enqueue(q);
        }

        // Under the lock: registers the page, stores hello and results.
        Session Touch(string sessionId, string body)
        {
            if (string.IsNullOrEmpty(sessionId) || sessionId.Length > 64)
                return null;
            if (!sessions.TryGetValue(sessionId, out Session session))
                sessions[sessionId] = session = new Session { Id = sessionId, ConnectedUtc = DateTime.UtcNow };
            session.LastSeenUtc = DateTime.UtcNow;

            Dictionary<string, object> message;
            try
            {
                message = MiniJson.Obj(MiniJson.Parse(string.IsNullOrEmpty(body) ? "{}" : body));
            }
            catch (FormatException)
            {
                return session;
            }
            if (message == null)
                return session;
            if (MiniJson.Obj(message.TryGetValue("hello", out object hello) ? hello : null) is Dictionary<string, object> info)
            {
                session.Hello = info;
                string loadId = MiniJson.Str(info, "loadId") ?? "";
                if (session.LoadId != loadId)
                {
                    if (session.LoadId != null)
                        foreach (Pending p in session.Waiting.Values.Where(p => p.Sent && !p.Done))
                        {
                            p.Error = "The page reloaded before answering.";    // Its old load can't answer any more.
                            p.Done = true;
                        }
                    session.LoadId = loadId;
                    // The page's own load time survives a proxy restart (Unity reload); clamp a phone's skewed clock.
                    double loadedAt = MiniJson.Num(info, "loadedAt", 0);
                    DateTime now = DateTime.UtcNow, at = now;
                    if (loadedAt > 0)
                    {
                        try { at = DateTimeOffset.FromUnixTimeMilliseconds((long)loadedAt).UtcDateTime; }
                        catch (ArgumentOutOfRangeException) { at = now; }
                    }
                    session.LoadedUtc = at > now ? now : at;
                }
            }
            if (message.TryGetValue("results", out object results) && results is List<object> list)
            {
                foreach (object item in list)
                {
                    var r = MiniJson.Obj(item);
                    string id = MiniJson.Str(r, "id");
                    if (id == null || !session.Waiting.TryGetValue(id, out Pending p))
                        continue;
                    if (MiniJson.Bool(r, "ok", false))
                        p.Result = r.TryGetValue("result", out object value) ? value : null;
                    else
                        p.Error = MiniJson.Str(r, "error", "Command failed in the page");
                    p.Done = true;
                }
                Monitor.PulseAll(gate);
            }
            return session;
        }

        void Expire()
        {
            DateTime now = DateTime.UtcNow;
            foreach (Session s in sessions.Values.Where(s => (now - s.LastSeenUtc).TotalSeconds > SessionTimeoutSeconds).ToList())
            {
                foreach (Pending p in s.Waiting.Values)
                {
                    p.Error = "The page disconnected.";
                    p.Done = true;
                }
                sessions.Remove(s.Id);
            }
            Monitor.PulseAll(gate);
        }
    }
}
