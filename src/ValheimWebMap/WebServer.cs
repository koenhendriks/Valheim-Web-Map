using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using BepInEx.Logging;

namespace ValheimWebMap
{
    internal interface IMapApi
    {
        string InfoJson();
        string StateJson();
        string HistoryJson();
        bool TryGetTile(int z, int x, int y, out byte[] png, out string etag);
    }

    /// <summary>
    /// HttpListener front end. Everything served here comes from snapshots and buffers that are safe
    /// to read off the main thread; handlers never touch Unity objects.
    /// </summary>
    internal sealed class WebServer : IDisposable
    {
        private static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" },
            { ".js", "application/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" },
            { ".png", "image/png" },
            { ".svg", "image/svg+xml" },
            { ".ico", "image/x-icon" },
            { ".txt", "text/plain; charset=utf-8" },
            { ".json", "application/json; charset=utf-8" },
        };

        private readonly ManualLogSource _log;
        private readonly IMapApi _api;
        private readonly string _overrideDir;
        private readonly Dictionary<string, string> _etags = new Dictionary<string, string>();
        private HttpListener _listener;
        private volatile bool _stopping;

        public WebServer(ManualLogSource log, IMapApi api)
        {
            _log = log;
            _api = api;
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _overrideDir = pluginDir != null ? Path.Combine(pluginDir, "web") : null;
        }

        public void Start(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) host = "*";
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://" + host + ":" + port + "/");
            _listener.Start();
            _listener.BeginGetContext(OnContext, null);
            _log.LogInfo("Web map listening on http://" + host + ":" + port + "/");
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception)
            {
                // Listener already gone.
            }
        }

        private void OnContext(IAsyncResult ar)
        {
            if (_stopping) return;
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.EndGetContext(ar);
            }
            catch (Exception)
            {
                return;
            }
            finally
            {
                if (!_stopping)
                {
                    try { _listener.BeginGetContext(OnContext, null); }
                    catch (Exception) { /* shutting down */ }
                }
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception e)
            {
                _log.LogWarning("Web request failed: " + e.Message);
                try { ctx.Response.Abort(); } catch (Exception) { /* client is gone */ }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            res.Headers["X-Content-Type-Options"] = "nosniff";

            if (req.HttpMethod != "GET" && req.HttpMethod != "HEAD")
            {
                Status(res, 405);
                return;
            }

            string path = req.Url.AbsolutePath;
            if (path.StartsWith("/tiles/", StringComparison.Ordinal))
            {
                ServeTile(req, res, path);
                return;
            }

            switch (path)
            {
                case "/api/info":
                    SendText(req, res, _api.InfoJson(), "application/json; charset=utf-8", "no-store", null);
                    return;
                case "/api/state":
                    SendText(req, res, _api.StateJson(), "application/json; charset=utf-8", "no-store", null);
                    return;
                case "/api/history":
                    SendText(req, res, _api.HistoryJson(), "application/json; charset=utf-8", "no-store", null);
                    return;
                case "/":
                    path = "/index.html";
                    break;
            }

            ServeStatic(req, res, path.Substring(1));
        }

        private void ServeTile(HttpListenerRequest req, HttpListenerResponse res, string path)
        {
            // /tiles/{z}/{x}/{y}.png
            string[] parts = path.Substring("/tiles/".Length).Split('/');
            int z, x, y;
            if (parts.Length != 3 || !parts[2].EndsWith(".png", StringComparison.Ordinal)
                || !int.TryParse(parts[0], out z) || !int.TryParse(parts[1], out x)
                || !int.TryParse(parts[2].Substring(0, parts[2].Length - 4), out y))
            {
                Status(res, 404);
                return;
            }

            byte[] png;
            string etag;
            if (!_api.TryGetTile(z, x, y, out png, out etag))
            {
                Status(res, 404);
                return;
            }
            SendBytes(req, res, png, "image/png", "no-cache", etag);
        }

        private void ServeStatic(HttpListenerRequest req, HttpListenerResponse res, string name)
        {
            if (name.Length == 0 || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains(".."))
            {
                Status(res, 404);
                return;
            }

            string contentType;
            if (!ContentTypes.TryGetValue(Path.GetExtension(name), out contentType))
            {
                Status(res, 404);
                return;
            }

            byte[] data = null;
            if (_overrideDir != null)
            {
                string file = Path.Combine(_overrideDir, name);
                if (File.Exists(file)) data = File.ReadAllBytes(file);
            }
            if (data == null)
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name))
                {
                    if (s == null)
                    {
                        Status(res, 404);
                        return;
                    }
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        data = ms.ToArray();
                    }
                }
            }
            // Content-based so a browser always sees new files after a plugin update, even within the same version.
            string etag;
            lock (_etags)
            {
                if (!_etags.TryGetValue(name, out etag))
                {
                    etag = "\"" + Fnv1a64(data).ToString("x16") + "\"";
                    _etags[name] = etag;
                }
            }
            string cache = name == "index.html" ? "no-cache" : "public, max-age=300";
            SendBytes(req, res, data, contentType, cache, etag);
        }

        private static ulong Fnv1a64(byte[] data)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in data) h = (h ^ b) * 1099511628211UL;
            return h;
        }

        private static void SendText(HttpListenerRequest req, HttpListenerResponse res, string text, string contentType, string cacheControl, string etag)
        {
            SendBytes(req, res, Encoding.UTF8.GetBytes(text), contentType, cacheControl, etag);
        }

        private static void SendBytes(HttpListenerRequest req, HttpListenerResponse res, byte[] data, string contentType, string cacheControl, string etag)
        {
            if (etag != null)
            {
                res.Headers["ETag"] = etag;
                if (req.Headers["If-None-Match"] == etag)
                {
                    res.StatusCode = 304;
                    res.Headers["Cache-Control"] = cacheControl;
                    res.Close();
                    return;
                }
            }
            res.StatusCode = 200;
            res.ContentType = contentType;
            res.Headers["Cache-Control"] = cacheControl;
            res.ContentLength64 = data.Length;
            if (req.HttpMethod == "HEAD")
            {
                res.Close();
                return;
            }
            using (Stream o = res.OutputStream)
            {
                o.Write(data, 0, data.Length);
            }
            res.Close();
        }

        private static void Status(HttpListenerResponse res, int code)
        {
            res.StatusCode = code;
            res.ContentLength64 = 0;
            res.Close();
        }
    }
}
