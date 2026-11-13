using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ReoBridge.Media;

namespace ReoBridge.Rtsp;

/// <summary>Where one track's RTP goes: interleaved on the RTSP connection, or UDP to the client.</summary>
public sealed class TrackTransport : IDisposable
{
    public int Track { get; init; }
    public bool Tcp { get; init; }
    public int RtpChannel { get; init; }
    public int RtcpChannel { get; init; }
    public IPEndPoint? ClientRtp { get; init; }
    public IPEndPoint? ClientRtcp { get; init; }
    public Socket? RtpSocket { get; init; }
    public Socket? RtcpSocket { get; init; }

    public uint Ssrc { get; } = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
    public ushort Seq { get; set; } = (ushort)Random.Shared.Next(ushort.MaxValue);
    public uint TimestampOffset { get; } = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
    public uint Packets { get; set; }
    public uint Octets { get; set; }
    public DateTime LastSrUtc { get; set; }

    public int ServerRtpPort => (RtpSocket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    public void Dispose()
    {
        RtpSocket?.Dispose();
        RtcpSocket?.Dispose();
    }
}

/// <summary>
/// One client watching one path. Packets are queued and written by a single task so a slow
/// client never holds up the camera or other viewers; if the queue overflows the session
/// drops frames and resumes at the next keyframe.
/// </summary>
public sealed class RtspSession : IMediaSink, IAsyncDisposable
{
    private readonly Channel<(TrackTransport Transport, byte[] Data, bool Rtcp)> _queue =
        Channel.CreateBounded<(TrackTransport, byte[], bool)>(new BoundedChannelOptions(4000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly Func<byte[], CancellationToken, Task> _writeInterleaved;
    private readonly CancellationTokenSource _cts = new();
    private Task _writer = Task.CompletedTask;
    private bool _waitForKeyframe = true;
    private bool _videoStarted;

    public RtspSession(MediaPath path, Func<byte[], CancellationToken, Task> writeInterleaved)
    {
        Path = path;
        _writeInterleaved = writeInterleaved;
    }

    public string Id { get; } = Convert.ToHexString(BitConverter.GetBytes(Random.Shared.NextInt64())).ToLowerInvariant();
    public MediaPath Path { get; }
    public Dictionary<int, TrackTransport> Tracks { get; } = [];
    public bool Playing { get; private set; }
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    public long DroppedFrames { get; private set; }
    public bool UsesUdp => Tracks.Values.Any(t => !t.Tcp);

    public void Play()
    {
        if (Playing) return;
        _waitForKeyframe = true;
        _videoStarted = false;
        Playing = true;
        if (_writer.IsCompleted) _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
        foreach (var t in Tracks.Values.Where(t => !t.Tcp && t.RtcpSocket is not null))
            _ = Task.Run(() => ReceiveRtcpAsync(t.RtcpSocket!, _cts.Token));
        Path.AddSink(this);
    }

    public void Pause()
    {
        Playing = false;
        Path.RemoveSink(this);
    }

    public void Send(int track, uint timestamp, bool keyframe, IReadOnlyList<RtpChunk> chunks)
    {
        if (!Playing || !Tracks.TryGetValue(track, out var t)) return;
        if (track == 0)
        {
            if (_waitForKeyframe && !keyframe) return;
            if (keyframe) _waitForKeyframe = false;
            _videoStarted = true;
        }
        else if (!_videoStarted && Tracks.ContainsKey(0))
        {
            return; // start audio together with video
        }

        uint ts = timestamp + t.TimestampOffset;
        bool overflow = false;

        if (t.Tcp)
        {
            // One write per frame: all packets, interleaved-framed, in a single buffer.
            int size = chunks.Sum(c => 4 + 12 + c.Payload.Length);
            var buf = new byte[size];
            int pos = 0;
            foreach (var c in chunks)
            {
                int len = 12 + c.Payload.Length;
                buf[pos] = (byte)'$';
                buf[pos + 1] = (byte)t.RtpChannel;
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(pos + 2), (ushort)len);
                WriteRtpHeader(buf.AsSpan(pos + 4), t, ts, c.Marker, track);
                c.Payload.CopyTo(buf, pos + 16);
                pos += 4 + len;
                Count(t, c);
            }
            overflow = !_queue.Writer.TryWrite((t, buf, false));
        }
        else
        {
            foreach (var c in chunks)
            {
                var pkt = new byte[12 + c.Payload.Length];
                WriteRtpHeader(pkt, t, ts, c.Marker, track);
                c.Payload.CopyTo(pkt, 12);
                Count(t, c);
                if (!_queue.Writer.TryWrite((t, pkt, false))) { overflow = true; break; }
            }
        }

        if (overflow)
        {
            DroppedFrames++;
            _waitForKeyframe = true;
            return;
        }

        if (DateTime.UtcNow - t.LastSrUtc > TimeSpan.FromSeconds(5))
        {
            t.LastSrUtc = DateTime.UtcNow;
            var sr = SenderReport(t, ts);
            if (t.Tcp)
            {
                var framed = new byte[4 + sr.Length];
                framed[0] = (byte)'$';
                framed[1] = (byte)t.RtcpChannel;
                BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(2), (ushort)sr.Length);
                sr.CopyTo(framed, 4);
                _queue.Writer.TryWrite((t, framed, true));
            }
            else
            {
                _queue.Writer.TryWrite((t, sr, true));
            }
        }
    }

    private static void Count(TrackTransport t, RtpChunk c)
    {
        t.Packets++;
        t.Octets += (uint)c.Payload.Length;
    }

    private static void WriteRtpHeader(Span<byte> h, TrackTransport t, uint ts, bool marker, int track)
    {
        h[0] = 0x80;
        h[1] = (byte)((marker ? 0x80 : 0) | (track == 0 ? 96 : 97));
        BinaryPrimitives.WriteUInt16BigEndian(h[2..], t.Seq++);
        BinaryPrimitives.WriteUInt32BigEndian(h[4..], ts);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], t.Ssrc);
    }

    private static byte[] SenderReport(TrackTransport t, uint ts)
    {
        var p = new byte[28];
        p[0] = 0x80;
        p[1] = 200;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), t.Ssrc);
        var ntp = DateTime.UtcNow - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ulong secs = (ulong)ntp.TotalSeconds;
        ulong frac = (ulong)((ntp.TotalSeconds - secs) * 4294967296.0);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), (uint)secs);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), (uint)frac);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), ts);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), t.Packets);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(24), t.Octets);
        return p;
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (t, data, rtcp) in _queue.Reader.ReadAllAsync(ct))
            {
                if (t.Tcp)
                {
                    await _writeInterleaved(data, ct);
                }
                else
                {
                    var socket = rtcp ? t.RtcpSocket : t.RtpSocket;
                    var target = rtcp ? t.ClientRtcp : t.ClientRtp;
                    if (socket is not null && target is not null)
                    {
                        try { await socket.SendToAsync(data, SocketFlags.None, target, ct); }
                        catch (SocketException) { /* client port closed; keep going until timeout */ }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _cts.Cancel(); } // connection gone; the connection loop cleans up
    }

    private async Task ReceiveRtcpAsync(Socket socket, CancellationToken ct)
    {
        var buf = new byte[1500];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await socket.ReceiveFromAsync(buf, SocketFlags.None, any, ct);
                LastActivityUtc = DateTime.UtcNow;
            }
        }
        catch (Exception) { }
    }

    public RtpInfo RtpInfoFor(int track) =>
        Tracks.TryGetValue(track, out var t)
            ? new RtpInfo(t.Seq, (track == 0 ? Path.VideoTimestamp : Path.AudioTimestamp) + t.TimestampOffset)
            : new RtpInfo(0, 0);

    public async ValueTask DisposeAsync()
    {
        Pause();
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        foreach (var t in Tracks.Values) t.Dispose();
        _cts.Dispose();
    }
}

public readonly record struct RtpInfo(ushort Seq, uint Timestamp);
