using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReoBridge.Rtsp;

namespace ReoBridge.Bridge;

/// <summary>A tiny HTTP endpoint: "/" is a status page, "/status" the same data as JSON. Read-only, no secrets.</summary>
public sealed class StatusServer
{
    private readonly int _port;
    private readonly IReadOnlyList<CameraWorker> _cameras;
    private readonly RtspServer _rtsp;
    private readonly int _rtspPort;
    private readonly ILogger _log;
    private readonly DateTime _started = DateTime.UtcNow;

    public StatusServer(int port, IReadOnlyList<CameraWorker> cameras, RtspServer rtsp, int rtspPort, ILogger log)
    {
        _port = port;
        _cameras = cameras;
        _rtsp = rtsp;
        _rtspPort = rtspPort;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            _log.LogWarning("Status page disabled: cannot listen on port {Port} ({Error})", _port, ex.Message);
            return;
        }
        _log.LogInformation("Status page on http://*:{Port}/", _port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    public object Snapshot() => new
    {
        version = typeof(StatusServer).Assembly.GetName().Version?.ToString(3),
        uptimeSeconds = (int)(DateTime.UtcNow - _started).TotalSeconds,
        rtspConnections = _rtsp.Connections,
        cameras = _cameras.Select(c => new
        {
            name = c.Name,
            host = c.Host,
            state = c.State,
            model = c.Model,
            encryption = c.Encryption?.ToString(),
            connectedSince = c.ConnectedSinceUtc,
            reconnects = c.Reconnects,
            lastError = c.LastError,
            streams = c.Paths.Select(p => new
            {
                path = p.Name,
                url = $"rtsp://<host>:{_rtspPort}/{p.Name}",
                codec = p.Codec?.ToString(),
                width = p.Width,
                height = p.Height,
                fps = Math.Round(p.MeasuredFps, 1),
                kbps = Math.Round(p.MeasuredKbps),
                audio = p.Audio is { } a ? $"AAC {a.SampleRate} Hz x{a.Channels}" : null,
                viewers = p.ClientCount,
                lastFrame = p.LastFrameUtc,
            }),
        }),
    };

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                var buf = new byte[4096];
                int n = 0;
                while (n < buf.Length)
                {
                    int r = await stream.ReadAsync(buf.AsMemory(n), timeout.Token);
                    if (r == 0) break;
                    n += r;
                    if (buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8) >= 0) break;
                }
                var first = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
                string path = first.Length > 1 ? first[1] : "/";

                string body, type;
                int code = 200;
                if (path.StartsWith("/status"))
                {
                    body = JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true });
                    type = "application/json";
                }
                else if (path == "/" || path.StartsWith("/?"))
                {
                    body = Html();
                    type = "text/html; charset=utf-8";
                }
                else
                {
                    code = 404;
                    body = "Not found";
                    type = "text/plain";
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
                await stream.WriteAsync(bytes, timeout.Token);
            }
            catch (Exception) { }
        }
    }

    private string Html()
    {
        static string E(object? s) => WebUtility.HtmlEncode(s?.ToString() ?? "");
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="refresh" content="5"><title>ReoBridge</title>
            <style>
            :root{--bg:#fff;--fg:#1d2329;--mut:#66707a;--line:#e3e6ea;--ok:#1a7f37;--bad:#c62828}
            @media (prefers-color-scheme:dark){:root{--bg:#15181c;--fg:#e6e9ec;--mut:#9aa3ad;--line:#2b3138;--ok:#4cc26b;--bad:#ff6b6b}}
            body{background:var(--bg);color:var(--fg);font:15px/1.45 system-ui,sans-serif;margin:0;padding:24px 16px;max-width:900px;margin:auto}
            h1{font-size:20px;margin:0 0 4px}.mut{color:var(--mut)}table{border-collapse:collapse;width:100%;margin:8px 0 24px}
            td,th{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line)}th{color:var(--mut);font-weight:500}
            .ok{color:var(--ok)}.bad{color:var(--bad)}code{font-size:13px}
            </style></head><body><h1>ReoBridge</h1>
            """);
        sb.Append($"<div class=mut>Up {E(TimeSpan.FromSeconds((int)(DateTime.UtcNow - _started).TotalSeconds))} · {_rtsp.Connections} RTSP connection(s)</div>");
        foreach (var c in _cameras)
        {
            string cls = c.State == "connected" ? "ok" : "bad";
            sb.Append($"<h2>{E(c.Name)} <span class={cls}>{E(c.State)}</span></h2>");
            sb.Append($"<div class=mut>{E(c.Host)} · {E(c.Model)} · {E(c.Encryption)} · reconnects {c.Reconnects}");
            if (c.LastError is not null) sb.Append($" · <span class=bad>{E(c.LastError)}</span>");
            sb.Append("</div><table><tr><th>Path</th><th>Video</th><th>fps</th><th>kbit/s</th><th>Audio</th><th>Viewers</th></tr>");
            foreach (var p in c.Paths)
            {
                bool live = p.LastFrameUtc > DateTime.UtcNow.AddSeconds(-3);
                sb.Append($"<tr><td><code>/{E(p.Name)}</code></td><td>{E(p.Codec)} {(p.Width > 0 ? $"{p.Width}×{p.Height}" : "")}</td>");
                sb.Append($"<td class={(live ? "ok" : "mut")}>{(live ? p.MeasuredFps.ToString("F1") : "idle")}</td><td>{(live ? p.MeasuredKbps.ToString("F0") : "")}</td>");
                sb.Append($"<td>{(p.Audio is { } a ? $"AAC {a.SampleRate / 1000.0:0.#} kHz" : "")}</td><td>{p.ClientCount}</td></tr>");
            }
            sb.Append("</table>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
