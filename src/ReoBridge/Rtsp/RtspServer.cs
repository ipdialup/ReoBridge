using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ReoBridge.Media;

namespace ReoBridge.Rtsp;

/// <summary>A small RTSP 1.0 server: OPTIONS, DESCRIBE, SETUP (TCP interleaved or UDP unicast), PLAY, PAUSE, TEARDOWN.</summary>
public sealed class RtspServer
{
    private readonly IPAddress _bind;
    private readonly int _port;
    private readonly IReadOnlyList<MediaPath> _paths;
    private readonly RtspAuth _auth;
    private readonly ILogger _log;
    private int _connections;

    public RtspServer(IPAddress bind, int port, IReadOnlyList<MediaPath> paths, RtspAuth auth, ILogger log)
    {
        _bind = bind;
        _port = port;
        _paths = paths;
        _auth = auth;
        _log = log;
    }

    public int Connections => _connections;

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_bind, _port);
        listener.Start();
        _log.LogInformation("RTSP server listening on {Bind}:{Port}", _bind, _port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    public MediaPath? FindPath(string path, out string? control)
    {
        control = null;
        // Strip a trailing track control ("trackID=0") if present.
        int slash = path.LastIndexOf('/');
        string last = slash < 0 ? path : path[(slash + 1)..];
        if (last.StartsWith("trackID=", StringComparison.OrdinalIgnoreCase))
        {
            control = last;
            path = slash < 0 ? "" : path[..slash];
        }
        return _paths.FirstOrDefault(p => p.Aliases.Any(a => a.Equals(path, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task HandleAsync(TcpClient client, CancellationToken serverCt)
    {
        Interlocked.Increment(ref _connections);
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var local = client.Client.LocalEndPoint as IPEndPoint;
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        var ct = cts.Token;
        var stream = client.GetStream();
        var writeLock = new SemaphoreSlim(1, 1);
        var reader = new RtspReader(stream);
        var sessions = new Dictionary<string, RtspSession>();
        string nonce = RtspAuth.NewNonce();
        bool authed = !_auth.Enabled;

        async Task Write(byte[] data, CancellationToken token)
        {
            await writeLock.WaitAsync(token);
            try { await stream.WriteAsync(data, token); }
            finally { writeLock.Release(); }
        }

        // UDP-only sessions have no interleaved traffic: time them out if the client goes silent.
        var watchdog = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                if (sessions.Count > 0 && sessions.Values.All(s => s.UsesUdp) &&
                    sessions.Values.All(s => DateTime.UtcNow - s.LastActivityUtc > TimeSpan.FromSeconds(90)))
                {
                    _log.LogInformation("RTSP {Remote}: UDP session timed out", remote);
                    cts.Cancel();
                }
            }
        }, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var req = await reader.ReadAsync(_ =>
                {
                    foreach (var s in sessions.Values) s.LastActivityUtc = DateTime.UtcNow;
                }, ct);
                if (req is null) break;
                foreach (var s in sessions.Values) s.LastActivityUtc = DateTime.UtcNow;

                RtspResponse resp;
                if (req.Method != "OPTIONS" && !authed)
                {
                    authed = _auth.Check(req, nonce);
                }
                if (req.Method != "OPTIONS" && !authed)
                {
                    resp = _auth.Challenge(nonce);
                }
                else
                {
                    resp = await HandleRequestAsync(req, sessions, local, remote, Write, ct);
                }
                await Write(resp.Serialize(req.CSeq), ct);
                if (req.Method == "TEARDOWN" && sessions.Count == 0 && req.Header("Connection") == "close") break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _log.LogWarning("RTSP {Remote}: {Error}", remote, ex.Message);
        }
        finally
        {
            cts.Cancel();
            foreach (var s in sessions.Values)
            {
                _log.LogInformation("RTSP {Remote}: stopped {Path}", remote, s.Path.Name);
                await s.DisposeAsync();
            }
            client.Dispose();
            Interlocked.Decrement(ref _connections);
        }
    }

    private async Task<RtspResponse> HandleRequestAsync(RtspRequest req, Dictionary<string, RtspSession> sessions,
        IPEndPoint? local, IPEndPoint? remote, Func<byte[], CancellationToken, Task> write, CancellationToken ct)
    {
        switch (req.Method)
        {
            case "OPTIONS":
                return RtspResponse.Ok().With("Public", "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER");

            case "DESCRIBE":
            {
                var path = FindPath(req.Path, out _);
                if (path is null) return new RtspResponse(404, "Not Found");
                path.Want(TimeSpan.FromSeconds(30));
                if (!await path.WaitReadyAsync(TimeSpan.FromSeconds(12), ct))
                    return new RtspResponse(503, "Service Unavailable");
                // Audio usually follows the first keyframe closely; give it a moment so it is in the SDP.
                for (int i = 0; i < 10 && path.Audio is null && path.LastFrameUtc > DateTime.UtcNow.AddSeconds(-2); i++)
                    await Task.Delay(100, ct);
                var sdp = Encoding.ASCII.GetBytes(path.BuildSdp(local?.Address.ToString() ?? "0.0.0.0"));
                string baseUrl = BaseUrl(req, path);
                _log.LogInformation("RTSP {Remote}: DESCRIBE {Path}", remote, path.Name);
                return new RtspResponse(200, "OK") { Body = sdp }
                    .With("Content-Type", "application/sdp")
                    .With("Content-Base", baseUrl + "/");
            }

            case "SETUP":
            {
                var path = FindPath(req.Path, out var control);
                if (path is null) return new RtspResponse(404, "Not Found");
                int track = control is null ? 0 : int.TryParse(control["trackID=".Length..], out var n) ? n : -1;
                if (track is not (0 or 1) || (track == 1 && path.Audio is null))
                    return new RtspResponse(404, "Not Found");

                string? sessionId = SessionId(req);
                RtspSession? session = null;
                if (sessionId is not null && !sessions.TryGetValue(sessionId, out session))
                    return new RtspResponse(454, "Session Not Found");
                if (session is not null && session.Path != path)
                    return new RtspResponse(459, "Aggregate Operation Not Allowed");

                var transportHeader = req.Header("Transport") ?? "";
                var transport = ParseTransport(transportHeader, track, remote);
                if (transport is null) return new RtspResponse(461, "Unsupported Transport");

                if (session is null)
                {
                    session = new RtspSession(path, write);
                    sessions[session.Id] = session;
                }
                if (session.Tracks.Remove(track, out var old)) old.Dispose();
                session.Tracks[track] = transport;

                string reply = transport.Tcp
                    ? $"RTP/AVP/TCP;unicast;interleaved={transport.RtpChannel}-{transport.RtcpChannel};ssrc={transport.Ssrc:X8}"
                    : $"RTP/AVP;unicast;client_port={transport.ClientRtp!.Port}-{transport.ClientRtcp!.Port};server_port={transport.ServerRtpPort}-{transport.ServerRtpPort + 1};ssrc={transport.Ssrc:X8}";
                return RtspResponse.Ok().With("Transport", reply).With("Session", $"{session.Id};timeout=60");
            }

            case "PLAY":
            {
                var session = FindSession(req, sessions);
                if (session is null) return new RtspResponse(454, "Session Not Found");
                session.Play();
                string baseUrl = BaseUrl(req, session.Path);
                var info = string.Join(",", session.Tracks.Keys.Order().Select(t =>
                {
                    var r = session.RtpInfoFor(t);
                    return $"url={baseUrl}/trackID={t};seq={r.Seq};rtptime={r.Timestamp}";
                }));
                _log.LogInformation("RTSP {Remote}: PLAY {Path} over {Transport}", remote, session.Path.Name, session.UsesUdp ? "UDP" : "TCP");
                return RtspResponse.Ok().With("Session", $"{session.Id};timeout=60").With("Range", "npt=0.000-").With("RTP-Info", info);
            }

            case "PAUSE":
            {
                var session = FindSession(req, sessions);
                if (session is null) return new RtspResponse(454, "Session Not Found");
                session.Pause();
                return RtspResponse.Ok().With("Session", session.Id);
            }

            case "TEARDOWN":
            {
                var session = FindSession(req, sessions);
                if (session is not null)
                {
                    sessions.Remove(session.Id);
                    _log.LogInformation("RTSP {Remote}: TEARDOWN {Path}", remote, session.Path.Name);
                    await session.DisposeAsync();
                }
                return RtspResponse.Ok();
            }

            case "GET_PARAMETER":
            case "SET_PARAMETER":
            {
                var r = RtspResponse.Ok();
                if (SessionId(req) is { } id) r.With("Session", id);
                return r;
            }

            default:
                return new RtspResponse(501, "Not Implemented");
        }
    }

    private static string BaseUrl(RtspRequest req, MediaPath path)
    {
        string u = req.Uri;
        int q = u.IndexOf('?');
        if (q >= 0) u = u[..q];
        u = u.TrimEnd('/');
        int slash = u.LastIndexOf('/');
        if (slash >= 0 && u[(slash + 1)..].StartsWith("trackID=", StringComparison.OrdinalIgnoreCase)) u = u[..slash];
        return u;
    }

    private static string? SessionId(RtspRequest req) => req.Header("Session")?.Split(';')[0].Trim();

    private static RtspSession? FindSession(RtspRequest req, Dictionary<string, RtspSession> sessions)
    {
        var id = SessionId(req);
        if (id is not null) return sessions.GetValueOrDefault(id);
        return sessions.Count == 1 ? sessions.Values.First() : null;
    }

    internal static TrackTransport? ParseTransport(string header, int track, IPEndPoint? remote)
    {
        // A client may offer several transports separated by commas; take the first we support.
        foreach (var option in header.Split(','))
        {
            var parts = option.Split(';').Select(p => p.Trim()).ToArray();
            if (parts.Length == 0) continue;
            string proto = parts[0].ToUpperInvariant();
            if (parts.Any(p => p.Equals("multicast", StringComparison.OrdinalIgnoreCase))) continue;

            if (proto == "RTP/AVP/TCP")
            {
                int rtp = track * 2, rtcp = track * 2 + 1;
                var inter = parts.FirstOrDefault(p => p.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase));
                if (inter is not null)
                {
                    var ch = inter["interleaved=".Length..].Split('-');
                    if (int.TryParse(ch[0], out var a)) { rtp = a; rtcp = ch.Length > 1 && int.TryParse(ch[1], out var b) ? b : a + 1; }
                }
                return new TrackTransport { Track = track, Tcp = true, RtpChannel = rtp, RtcpChannel = rtcp };
            }

            if (proto is "RTP/AVP" or "RTP/AVP/UDP" && remote is not null)
            {
                var cp = parts.FirstOrDefault(p => p.StartsWith("client_port=", StringComparison.OrdinalIgnoreCase));
                if (cp is null) continue;
                var ports = cp["client_port=".Length..].Split('-');
                if (!int.TryParse(ports[0], out var rtpPort)) continue;
                int rtcpPort = ports.Length > 1 && int.TryParse(ports[1], out var p2) ? p2 : rtpPort + 1;
                var (rtpSocket, rtcpSocket) = OpenUdpPair(remote.AddressFamily);
                return new TrackTransport
                {
                    Track = track,
                    Tcp = false,
                    ClientRtp = new IPEndPoint(remote.Address, rtpPort),
                    ClientRtcp = new IPEndPoint(remote.Address, rtcpPort),
                    RtpSocket = rtpSocket,
                    RtcpSocket = rtcpSocket,
                };
            }
        }
        return null;
    }

    // RTP on an even port, RTCP on the next one.
    private static (Socket, Socket) OpenUdpPair(AddressFamily family)
    {
        var any = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000) & ~1;
            var rtp = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            var rtcp = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                rtp.Bind(new IPEndPoint(any, port));
                rtcp.Bind(new IPEndPoint(any, port + 1));
                rtp.SendBufferSize = 1 << 20;
                return (rtp, rtcp);
            }
            catch (SocketException)
            {
                rtp.Dispose();
                rtcp.Dispose();
            }
        }
        throw new IOException("No free UDP port pair for RTP");
    }
}
