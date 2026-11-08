using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Supercent.Util.Editor
{
    // A local HTTP proxy in front of the plugin's develop server. It changes exactly two responses on their
    // way to the browser: the Dev Environment page ("/", "/index.html") and the playable page (".../iframe.html")
    // get one <script> tag for the Dev Tools, served from scriptFile. Everything else, including the
    // runtime-analysis POSTs to /tracing and WebSocket upgrades, is piped through byte for byte.
    // Nothing is written to disk: the build output, luna.json and the size estimate never see the Dev Tools.
    // No Unity API here, so it runs (and is tested) outside the Editor too.
    public sealed class DevToolsProxy : IDisposable
    {
        public const string ScriptRoute = "/__luna_devtools/devtools.js";
        const int MaxHeadBytes = 64 * 1024;
        const int ConnectTimeoutMs = 3000;
        const int ConnectAttempts = 4;
        const int CloseGraceMs = 5000;

        static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");   // Header bytes round-trip unchanged.
        static readonly Regex HeadTag = new Regex(@"<head\b[^>]*>", RegexOptions.IgnoreCase);

        readonly string scriptFile;
        readonly TcpListener listener;
        volatile int upstreamPort;
        volatile bool stopped;

        public int Port { get; }
        public int UpstreamPort { get => upstreamPort; set => upstreamPort = value; }
        public string LastError { get; private set; }

        DevToolsProxy(TcpListener listener, int port, int upstreamPort, string scriptFile)
        {
            this.listener = listener;
            Port = port;
            this.upstreamPort = upstreamPort;
            this.scriptFile = scriptFile;
            new Thread(AcceptLoop) { IsBackground = true, Name = "Luna Dev Tools proxy" }.Start();
        }

        // Listens on every interface (phones on the LAN) at the first free port from firstPort, on IPv4 and IPv6:
        // "localhost" tries ::1 first, and Windows retries a refused connection for seconds before falling back.
        public static DevToolsProxy Start(int firstPort, int portCount, int upstreamPort, string scriptFile)
        {
            for (int port = firstPort; port < firstPort + portCount; port++)
            {
                TcpListener listener = Listen(port);
                if (listener != null)
                    return new DevToolsProxy(listener, port, upstreamPort, scriptFile);
            }
            throw new IOException($"No free port between {firstPort} and {firstPort + portCount - 1}");
        }

        static TcpListener Listen(int port)
        {
            try
            {
                var dual = new TcpListener(IPAddress.IPv6Any, port);
                dual.Server.DualMode = true;
                dual.Start();
                return dual;
            }
            catch (Exception)
            {
                // No IPv6 here, or the port is taken: try IPv4 alone.
            }
            try
            {
                var v4 = new TcpListener(IPAddress.Any, port);
                v4.Start();
                return v4;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            stopped = true;
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }
        }

        // True when the develop server accepts connections.
        public static bool IsListening(int port, int timeoutMs = 300)
        {
            try
            {
                foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                    using (var client = new TcpClient(address.AddressFamily))
                    {
                        try
                        {
                            if (client.ConnectAsync(address, port).Wait(timeoutMs) && client.Connected)
                                return true;
                        }
                        catch (Exception)
                        {
                            // Next address.
                        }
                    }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void AcceptLoop()
        {
            while (!stopped)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;     // Listener stopped.
                }
                // A thread per connection: piped connections (WebSockets) may stay open for the whole session.
                new Thread(() => Handle(client)) { IsBackground = true, Name = "Luna Dev Tools connection" }.Start();
            }
        }

        void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    NetworkStream browser = client.GetStream();
                    if (!ReadHead(browser, out string head, out byte[] rest))
                        return;
                    string[] requestLine = head.Substring(0, Math.Max(0, head.IndexOf("\r\n", StringComparison.Ordinal))).Split(' ');
                    if (requestLine.Length < 3)
                        return;
                    string method = requestLine[0];
                    string path = requestLine[1].Split('?')[0];

                    if (method == "GET" && path == ScriptRoute)
                        ServeScript(browser);
                    else if (method == "GET" && path.EndsWith("/iframe.html", StringComparison.OrdinalIgnoreCase))
                        ServeInjected(browser, head, "agent");
                    else if (method == "GET" && (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
                        ServeInjected(browser, head, "shell");
                    else
                    {
                        Pipe(client, browser, head, rest);
                        return;
                    }
                    CloseGracefully(client);
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
                {
                    // Browser or develop server closed the connection.
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                }
            }
        }

        // Request line + headers, up to the blank line; rest = body bytes already read.
        static bool ReadHead(Stream stream, out string head, out byte[] rest)
        {
            head = null;
            rest = new byte[0];
            var buffer = new byte[MaxHeadBytes];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = stream.Read(buffer, length, buffer.Length - length);
                if (read <= 0)
                    return false;
                length += read;
                int end = IndexOf(buffer, length, HeaderEnd);
                if (end >= 0)
                {
                    head = Latin1.GetString(buffer, 0, end + HeaderEnd.Length);
                    rest = new byte[length - end - HeaderEnd.Length];
                    Buffer.BlockCopy(buffer, end + HeaderEnd.Length, rest, 0, rest.Length);
                    return true;
                }
            }
            return false;
        }

        static readonly byte[] HeaderEnd = { 13, 10, 13, 10 };

        static int IndexOf(byte[] data, int length, byte[] pattern)
        {
            for (int i = 0; i <= length - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j])
                    j++;
                if (j == pattern.Length)
                    return i;
            }
            return -1;
        }

        // Same request with "Connection: close" (one request per connection, so every page load comes back
        // through Handle). WebSocket upgrades keep their own Connection header.
        static string Rewrite(string head, bool identityEncoding)
        {
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            bool upgrade = IsUpgrade(head);
            var sb = new StringBuilder();
            sb.Append(lines[0]).Append("\r\n");
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                    continue;
                string name = line.Split(':')[0].Trim();
                if (!upgrade && (Is(name, "Connection") || Is(name, "Keep-Alive") || Is(name, "Proxy-Connection")))
                    continue;
                if (identityEncoding && Is(name, "Accept-Encoding"))
                    continue;
                if (identityEncoding && (Is(name, "If-None-Match") || Is(name, "If-Modified-Since")))
                    continue;   // Always a full page to inject into, never a 304.
                sb.Append(line).Append("\r\n");
            }
            if (!upgrade)
                sb.Append("Connection: close\r\n");
            if (identityEncoding)
                sb.Append("Accept-Encoding: identity\r\n");
            return sb.Append("\r\n").ToString();
        }

        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // The develop server listens on localhost: IPv4 or IPv6. A refused connection is retried briefly
        // (a busy server's accept queue overflows when the page loads many files at once).
        TcpClient Connect()
        {
            for (int attempt = 0; attempt < ConnectAttempts; attempt++)
            {
                foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                {
                    var client = new TcpClient(address.AddressFamily) { NoDelay = true };
                    try
                    {
                        if (client.ConnectAsync(address, upstreamPort).Wait(ConnectTimeoutMs) && client.Connected)
                            return client;
                    }
                    catch (Exception)
                    {
                        // Next address / attempt.
                    }
                    client.Dispose();
                }
                Thread.Sleep(100 * (attempt + 1));
            }
            return null;
        }

        TcpClient ConnectUpstream(Stream browser)
        {
            TcpClient upstream = Connect();
            if (upstream != null)
                return upstream;
            string html = "<!doctype html><meta charset=utf-8><title>Luna Dev Tools</title>" +
                          "<body style=\"font:14px system-ui;padding:24px\"><h3>Luna develop server is not running</h3>" +
                          $"<p>Nothing answers on port {upstreamPort}. Run Build Develop in the Luna Build Assistant, " +
                          "or press <b>Open with Dev Tools</b> there, then reload this page.</p>";
            Respond(browser, "502 Bad Gateway", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            return null;
        }

        void ServeScript(Stream browser)
        {
            byte[] body;
            try
            {
                body = File.ReadAllBytes(scriptFile);
            }
            catch (Exception e)
            {
                Respond(browser, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Dev Tools script not found: " + e.Message));
                return;
            }
            Respond(browser, "200 OK", "application/javascript; charset=utf-8", body);
        }

        static void Respond(Stream browser, string status, string contentType, byte[] body)
        {
            string head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                          "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] headBytes = Latin1.GetBytes(head);
            browser.Write(headBytes, 0, headBytes.Length);
            browser.Write(body, 0, body.Length);
            browser.Flush();
        }

        // Fetches the page from the develop server, adds the Dev Tools <script> right after <head>
        // (so it runs before any Luna script) and returns it. Anything but a 200 HTML page passes unchanged.
        void ServeInjected(Stream browser, string head, string role)
        {
            byte[] response;
            using (TcpClient upstream = ConnectUpstream(browser))
            {
                if (upstream == null)
                    return;
                NetworkStream server = upstream.GetStream();
                byte[] request = Latin1.GetBytes(Rewrite(head, true));
                server.Write(request, 0, request.Length);
                response = ReadToEnd(server);
            }

            byte[] page = Inject(response, ScriptRoute + "?role=" + role);
            if (ReferenceEquals(page, response))
                page = WithClose(response);         // Not injected (e.g. a 404): still one response per connection.
            browser.Write(page, 0, page.Length);
            browser.Flush();
        }

        static byte[] WithClose(byte[] response)
        {
            int headEnd = IndexOf(response, response.Length, HeaderEnd);
            if (headEnd < 0)
                return response;
            byte[] head = Latin1.GetBytes(CloseAfterResponse(Latin1.GetString(response, 0, headEnd + HeaderEnd.Length)));
            int bodyStart = headEnd + HeaderEnd.Length;
            byte[] result = new byte[head.Length + response.Length - bodyStart];
            Buffer.BlockCopy(head, 0, result, 0, head.Length);
            Buffer.BlockCopy(response, bodyStart, result, head.Length, response.Length - bodyStart);
            return result;
        }

        // FIN first, then wait briefly for the browser to close: closing with unread input would send a reset
        // that can cut off the end of the response.
        static void CloseGracefully(TcpClient client)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
                client.Client.ReceiveTimeout = CloseGraceMs;
                var buffer = new byte[4096];
                while (client.Client.Receive(buffer) > 0) { }
            }
            catch (Exception)
            {
                // Closed or timed out.
            }
        }

        // Pure transformation, exposed for tests.
        public static byte[] Inject(byte[] response, string scriptSrc)
        {
            int headEnd = IndexOf(response, response.Length, HeaderEnd);
            if (headEnd < 0)
                return response;
            string head = Latin1.GetString(response, 0, headEnd);
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] status = lines[0].Split(' ');
            var headers = new List<string>();
            bool chunked = false, gzip = false, deflate = false, html = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (Is(name, "Transfer-Encoding")) { chunked = value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0; continue; }
                if (Is(name, "Content-Encoding")) { gzip = Is(value, "gzip"); deflate = Is(value, "deflate"); continue; }
                if (Is(name, "Content-Type")) html = value.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0;
                if (Is(name, "Content-Length") || Is(name, "Connection") || Is(name, "ETag") || Is(name, "Last-Modified")
                    || Is(name, "Cache-Control") || Is(name, "Expires") || Is(name, "Pragma"))
                    continue;
                headers.Add(line);
            }
            if (status.Length < 2 || status[1] != "200" || !html)
                return response;

            int bodyStart = headEnd + HeaderEnd.Length;
            byte[] body = new byte[response.Length - bodyStart];
            Buffer.BlockCopy(response, bodyStart, body, 0, body.Length);
            if (chunked)
                body = Dechunk(body);
            if (gzip || deflate)
                body = Decompress(body, gzip);

            string text = Encoding.UTF8.GetString(body);
            string tag = "<script src=\"" + scriptSrc + "\"></script>";
            Match m = HeadTag.Match(text);
            text = m.Success ? text.Insert(m.Index + m.Length, tag) : tag + text;
            byte[] newBody = Encoding.UTF8.GetBytes(text);

            var sb = new StringBuilder(lines[0]).Append("\r\n");
            foreach (string h in headers)
                sb.Append(h).Append("\r\n");
            sb.Append("Content-Length: ").Append(newBody.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            byte[] newHead = Latin1.GetBytes(sb.ToString());
            byte[] result = new byte[newHead.Length + newBody.Length];
            Buffer.BlockCopy(newHead, 0, result, 0, newHead.Length);
            Buffer.BlockCopy(newBody, 0, result, newHead.Length, newBody.Length);
            return result;
        }

        static byte[] Dechunk(byte[] body)
        {
            var output = new MemoryStream();
            int pos = 0;
            while (pos < body.Length)
            {
                int lineEnd = IndexOf(Slice(body, pos), body.Length - pos, new byte[] { 13, 10 });
                if (lineEnd < 0)
                    break;
                string sizeText = Latin1.GetString(body, pos, lineEnd).Split(';')[0].Trim();
                int size = Convert.ToInt32(sizeText, 16);
                pos += lineEnd + 2;
                if (size == 0)
                    break;
                output.Write(body, pos, Math.Min(size, body.Length - pos));
                pos += size + 2;
            }
            return output.ToArray();
        }

        static byte[] Slice(byte[] data, int start)
        {
            var slice = new byte[data.Length - start];
            Buffer.BlockCopy(data, start, slice, 0, slice.Length);
            return slice;
        }

        static byte[] Decompress(byte[] body, bool gzip)
        {
            using (var input = new MemoryStream(body))
            using (Stream z = gzip ? (Stream)new GZipStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                z.CopyTo(output);
                return output.ToArray();
            }
        }

        static byte[] ReadToEnd(Stream stream)
        {
            using (var output = new MemoryStream())
            {
                stream.CopyTo(output);
                return output.ToArray();
            }
        }

        // Any other request: forwarded and answered byte for byte, both directions, until either side closes.
        void Pipe(TcpClient client, NetworkStream browser, string head, byte[] rest)
        {
            bool upgrade = IsUpgrade(head);
            using (TcpClient upstream = ConnectUpstream(browser))
            {
                if (upstream == null)
                    return;
                NetworkStream server = upstream.GetStream();
                byte[] request = Latin1.GetBytes(Rewrite(head, false));
                server.Write(request, 0, request.Length);
                if (rest.Length > 0)
                    server.Write(rest, 0, rest.Length);

                // Server -> browser on its own thread. The response says "Connection: close", so the browser sends
                // nothing more on this socket and closes it after reading: closing gracefully (FIN, never a reset
                // with unread data) keeps the end of the response from being cut off.
                var browserDone = new ManualResetEventSlim();
                var toBrowser = new Thread(() =>
                {
                    try
                    {
                        if (!upgrade && ReadHead(server, out string response, out byte[] body))
                        {
                            byte[] responseHead = Latin1.GetBytes(CloseAfterResponse(response));
                            browser.Write(responseHead, 0, responseHead.Length);
                            if (body.Length > 0)
                                browser.Write(body, 0, body.Length);
                        }
                        Copy(server, browser);
                    }
                    catch (Exception)
                    {
                        // One side closed.
                    }
                    try
                    {
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception)
                    {
                        // Already closed.
                    }
                    if (!browserDone.Wait(CloseGraceMs))
                        client.Close();     // Browser kept the socket open: end the copy below.
                }) { IsBackground = true, Name = "Luna Dev Tools pipe" };
                toBrowser.Start();
                Copy(browser, server);
                browserDone.Set();
                try
                {
                    upstream.Client.Shutdown(SocketShutdown.Send);   // Browser finished sending: let the server finish.
                }
                catch (Exception)
                {
                    // Already closed.
                }
                toBrowser.Join();
            }
        }

        static bool IsUpgrade(string head) =>
            Array.Exists(head.Split(new[] { "\r\n" }, StringSplitOptions.None), l => l.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));

        // Response head with "Connection: close" (101 Switching Protocols unchanged).
        static string CloseAfterResponse(string head)
        {
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines[0].Contains(" 101 "))
                return head;
            var sb = new StringBuilder(lines[0]).Append("\r\n");
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                    continue;
                string name = line.Split(':')[0].Trim();
                if (Is(name, "Connection") || Is(name, "Keep-Alive"))
                    continue;
                sb.Append(line).Append("\r\n");
            }
            return sb.Append("Connection: close\r\n\r\n").ToString();
        }

        static void Copy(Stream from, Stream to)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                int read;
                while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    to.Write(buffer, 0, read);
                    to.Flush();
                }
            }
            catch (Exception)
            {
                // One side closed.
            }
        }
    }
}
