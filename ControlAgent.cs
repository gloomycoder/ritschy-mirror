using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RitschyMirror;

/// <summary>
/// Local HTTP control agent (HttpListener, lightweight, self-contained friendly).
/// The cockpit (or any client) controls the engine via IP:port — the response shapes
/// are IDENTICAL to the former WinRM contract (cockpit/services/mirror_control.py), so that
/// only the transport has to be swapped there and the frontend/status dot stay unchanged.
///
/// Routes:
///   GET  /health    → {"ok":true}
///   GET  /status    → {"running":bool,"state":"running|stopped","config":{...}}
///   GET  /config    → mirror_config.json
///   POST /config    → whitelist merge (body = JSON patch), then status
///   POST /start | /stop | /restart → control the engine, then status
///   GET  /displays  → [{index,name,resolution,hdr,adapter}, ...]
///   GET  /windows   → [{title,exe,pid}, ...]  (capturable windows for the window picker)
/// </summary>
public sealed class ControlAgent
{
    private readonly MirrorEngine _engine;
    private readonly string _bind;
    private readonly int _port;
    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _run;

    public string? BoundPrefix { get; private set; }

    public ControlAgent(MirrorEngine engine, string bind, int port)
    {
        _engine = engine;
        _bind = string.IsNullOrWhiteSpace(bind) ? "+" : bind.Trim();
        _port = port;
    }

    public void Start()
    {
        // Try the configured bind first ("+" = all interfaces, needs URL ACL/install);
        // if that fails (no ACL/no rights), fall back to localhost only.
        foreach (var host in new[] { _bind, "localhost" })
        {
            var prefix = $"http://{host}:{_port}/";
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add(prefix);
                l.Start();
                _listener = l;
                BoundPrefix = prefix;
                _run = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "MirrorAgent" };
                _thread.Start();
                _engine.Log($"HTTP agent listening on {prefix}");
                return;
            }
            catch (Exception ex)
            {
                _engine.Log($"HTTP agent bind {prefix} failed: {ex.Message}");
                if (host == "localhost") throw;
            }
        }
    }

    public void Stop()
    {
        _run = false;
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
    }

    private void Loop()
    {
        while (_run && _listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; } // Stop() → listener closed
            try { Handle(ctx); }
            catch (Exception ex)
            {
                try { WriteJson(ctx, 500, new JsonObject { ["error"] = ex.Message }); } catch { }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string path = (ctx.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
        string method = ctx.Request.HttpMethod.ToUpperInvariant();
        if (path.Length == 0) path = "/";

        switch (path)
        {
            case "/":
            case "/dock":
                WriteHtml(ctx, DockHtml());
                break;

            case "/health":
                WriteJson(ctx, 200, new JsonObject { ["ok"] = true, ["running"] = _engine.IsRunning });
                break;

            case "/status":
                WriteJson(ctx, 200, StatusObject());
                break;

            case "/config" when method == "GET":
                WriteJson(ctx, 200, LoadConfigNode());
                break;

            case "/config" when method == "POST":
            {
                var body = ReadBody(ctx);
                var patch = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) as JsonObject ?? new JsonObject();
                MirrorConfig.MergePatch(_engine.ConfigPath, patch);
                WriteJson(ctx, 200, StatusObject());
                break;
            }

            case "/start":
                _engine.Start();
                WriteJson(ctx, 200, StatusObject());
                break;

            case "/stop":
                _engine.Stop();
                WriteJson(ctx, 200, StatusObject());
                break;

            case "/restart":
                _engine.Restart();
                WriteJson(ctx, 200, StatusObject());
                break;

            case "/displays":
            {
                var arr = new JsonArray();
                foreach (var d in MirrorEngine.EnumerateDisplays())
                    arr.Add(new JsonObject
                    {
                        ["index"] = d.Index, ["name"] = d.Name, ["friendly"] = d.Friendly,
                        ["key"] = d.Key,
                        ["resolution"] = d.Resolution, ["hdr"] = d.Hdr, ["adapter"] = d.Adapter,
                    });
                WriteJson(ctx, 200, arr);
                break;
            }

            case "/windows":
            {
                var arr = new JsonArray();
                foreach (var w in WindowEnum.List())
                    arr.Add(new JsonObject { ["title"] = w.Title, ["exe"] = w.Exe, ["pid"] = w.Pid });
                WriteJson(ctx, 200, arr);
                break;
            }

            default:
                WriteJson(ctx, 404, new JsonObject { ["error"] = "unknown route" });
                break;
        }
    }

    private JsonObject StatusObject()
    {
        bool running = _engine.IsRunning;
        return new JsonObject
        {
            ["running"] = running,
            ["state"] = running ? "running" : "stopped",
            ["config"] = LoadConfigNode(),
            ["error"] = string.IsNullOrEmpty(_engine.LastError) ? null : _engine.LastError,
        };
    }

    private JsonNode LoadConfigNode()
    {
        try { return JsonNode.Parse(File.ReadAllText(_engine.ConfigPath)) ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static string ReadBody(HttpListenerContext ctx)
    {
        using var r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Embedded OBS dock (web/dock.html) — same origin as the JSON routes ⇒ no CORS.
    private static string? _dock;
    private static string DockHtml()
    {
        if (_dock != null) return _dock;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("dock.html");
            if (s != null) { using var r = new StreamReader(s); _dock = r.ReadToEnd(); }
        }
        catch { }
        return _dock ??= "<!doctype html><meta charset=utf-8><h1>RitschyMirror</h1><p>dock.html fehlt.</p>";
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteJson(HttpListenerContext ctx, int status, JsonNode node)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
