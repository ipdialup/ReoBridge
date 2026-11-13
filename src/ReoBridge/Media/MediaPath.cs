using System.Diagnostics;
using System.Text;
using ReoBridge.Baichuan;

namespace ReoBridge.Media;

/// <summary>Receives RTP payloads for one path. Implemented by RTSP sessions.</summary>
public interface IMediaSink
{
    /// <param name="track">0 = video, 1 = audio.</param>
    /// <param name="timestamp">RTP timestamp in the track's clock.</param>
    /// <param name="keyframe">True for a video keyframe (a safe point to start or resume).</param>
    void Send(int track, uint timestamp, bool keyframe, IReadOnlyList<RtpChunk> chunks);
}

/// <summary>
/// One published stream, e.g. "Indoor/main". Keeps the latest codec parameters so the
/// SDP survives camera reconnects, turns camera frames into RTP payloads with steady
/// timestamps, and fans them out to every playing session.
/// </summary>
public sealed class MediaPath
{
    public const int VideoClock = 90000;

    private readonly object _lock = new();
    private readonly List<IMediaSink> _sinks = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Video timing
    private uint _videoTs = (uint)Random.Shared.Next();
    private uint? _lastMicros;
    private double _lastVideoWall;
    // Audio timing
    private uint _audioTs = (uint)Random.Shared.Next();
    private double _lastAudioWall = -1;

    // Stats
    private long _bytesWindow;
    private int _framesWindow;
    private double _windowStart;

    public MediaPath(string camera, string stream, bool isSub, IEnumerable<string> aliases)
    {
        Camera = camera;
        Stream = stream;
        IsSub = isSub;
        Name = $"{camera}/{stream}";
        Aliases = aliases.Select(a => a.Trim('/')).ToArray();
    }

    public string Camera { get; }
    public string Stream { get; }
    public string Name { get; }
    public bool IsSub { get; }
    public string[] Aliases { get; }

    public VideoCodec? Codec { get; private set; }
    public byte[]? Vps { get; private set; }
    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }
    public AacInfo? Audio { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }

    public bool Ready => Codec is not null && Sps is not null && Pps is not null && (Codec == VideoCodec.H264 || Vps is not null);
    public uint VideoTimestamp => _videoTs;
    public uint AudioTimestamp => _audioTs;

    public DateTime? LastFrameUtc { get; private set; }
    /// <summary>When video started flowing this time round (null while the source is down).</summary>
    public DateTime? FlowingSinceUtc { get; private set; }
    public double MeasuredFps { get; private set; }
    public double MeasuredKbps { get; private set; }

    public int ClientCount { get { lock (_lock) return _sinks.Count; } }

    /// <summary>Someone wants this stream until this time (an RTSP DESCRIBE, or linger after the last viewer).</summary>
    public DateTime WantedUntilUtc { get; private set; }

    public bool Wanted => ClientCount > 0 || DateTime.UtcNow < WantedUntilUtc;

    public void Want(TimeSpan forHowLong)
    {
        var until = DateTime.UtcNow + forHowLong;
        if (until > WantedUntilUtc) WantedUntilUtc = until;
    }

    /// <summary>Waits until codec parameters are known (first keyframe after a cold start).</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (Ready) return true;
        Task task;
        lock (_lock) task = _ready.Task;
        try
        {
            await task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return Ready;
        }
    }

    public void AddSink(IMediaSink sink)
    {
        lock (_lock) _sinks.Add(sink);
    }

    public void RemoveSink(IMediaSink sink)
    {
        lock (_lock)
        {
            if (_sinks.Remove(sink) && _sinks.Count == 0)
                Want(TimeSpan.FromSeconds(30)); // linger so a quick reconnect finds the stream running
        }
    }

    /// <summary>The camera stream stopped; the next frame re-anchors timestamps.</summary>
    public void SourceLost()
    {
        _lastMicros = null;
        _lastAudioWall = -1;
        FlowingSinceUtc = null;
        MeasuredFps = 0;
        MeasuredKbps = 0;
    }

    public void OnInfo(BcMediaFrame info)
    {
        Width = info.Width;
        Height = info.Height;
        if (info.Fps > 0) Fps = info.Fps;
    }

    public void OnVideo(BcMediaFrame frame)
    {
        var codec = frame.Codec == "H265" ? VideoCodec.H265 : VideoCodec.H264;
        var nals = Nal.SplitAnnexB(frame.Data);
        if (nals.Count == 0) return;

        bool keyframe = false;
        foreach (var nal in nals)
        {
            int type = Nal.Type(codec, nal);
            if (Nal.IsKeyframe(codec, type)) keyframe = true;
            if (codec == VideoCodec.H264)
            {
                if (type == 7) Sps = nal.ToArray();
                else if (type == 8) Pps = nal.ToArray();
            }
            else
            {
                if (type == 32) Vps = nal.ToArray();
                else if (type == 33) Sps = nal.ToArray();
                else if (type == 34) Pps = nal.ToArray();
            }
        }
        Codec = codec;

        AdvanceVideoClock(frame.Microseconds);
        LastFrameUtc = DateTime.UtcNow;
        FlowingSinceUtc ??= LastFrameUtc;
        UpdateStats(frame.Data.Length);

        if (Ready)
        {
            lock (_lock)
            {
                if (!_ready.Task.IsCompleted) _ready.TrySetResult();
            }
        }

        // AUD / SEI-only NALs are fine to forward; drop nothing.
        var chunks = RtpPacketizer.Video(codec, nals);
        Fanout(0, _videoTs, keyframe, chunks);
    }

    public void OnAudio(BcMediaFrame frame)
    {
        if (frame.Kind != BcMediaKind.Aac) return; // ADPCM cameras: video only for now
        var units = AacInfo.SplitAdts(frame.Data, out var info);
        if (info is null || units.Count == 0) return;
        Audio = info;

        double now = _clock.Elapsed.TotalSeconds;
        if (_lastAudioWall >= 0 && now - _lastAudioWall > 1.0)
            _audioTs += (uint)((now - _lastAudioWall) * info.SampleRate); // gap: keep in step with real time
        _lastAudioWall = now;

        foreach (var au in units)
        {
            Fanout(1, _audioTs, false, [RtpPacketizer.Aac(au)]);
            _audioTs += 1024;
        }
    }

    // Use the camera's own frame spacing when it is sane; fall back to wall time across gaps.
    private void AdvanceVideoClock(uint micros)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (_lastMicros is { } last)
        {
            uint delta = micros - last; // wraps correctly
            if (delta > 0 && delta <= 2_000_000)
                _videoTs += (uint)(delta * (VideoClock / 1e6));
            else
                _videoTs += (uint)(Math.Max(now - _lastVideoWall, 1.0 / 30) * VideoClock);
        }
        else if (_lastVideoWall > 0)
        {
            _videoTs += (uint)(Math.Max(now - _lastVideoWall, 1.0 / 30) * VideoClock);
        }
        _lastMicros = micros;
        _lastVideoWall = now;
    }

    private void UpdateStats(int bytes)
    {
        double now = _clock.Elapsed.TotalSeconds;
        _bytesWindow += bytes;
        _framesWindow++;
        double span = now - _windowStart;
        if (span >= 5)
        {
            MeasuredFps = _framesWindow / span;
            MeasuredKbps = _bytesWindow * 8 / span / 1000;
            _bytesWindow = 0;
            _framesWindow = 0;
            _windowStart = now;
        }
    }

    private void Fanout(int track, uint ts, bool keyframe, IReadOnlyList<RtpChunk> chunks)
    {
        IMediaSink[] sinks;
        lock (_lock) sinks = _sinks.ToArray();
        foreach (var s in sinks) s.Send(track, ts, keyframe, chunks);
    }

    public string BuildSdp(string serverAddress)
    {
        var sb = new StringBuilder();
        sb.Append("v=0\r\n");
        sb.Append($"o=- {Math.Abs(Name.GetHashCode())} 1 IN IP4 {serverAddress}\r\n");
        sb.Append($"s=ReoBridge {Name}\r\n");
        sb.Append("c=IN IP4 0.0.0.0\r\n");
        sb.Append("t=0 0\r\n");
        sb.Append("a=control:*\r\n");
        sb.Append("a=range:npt=0-\r\n");

        sb.Append("m=video 0 RTP/AVP 96\r\n");
        if (Codec == VideoCodec.H265)
        {
            sb.Append("a=rtpmap:96 H265/90000\r\n");
            sb.Append($"a=fmtp:96 sprop-vps={B64(Vps)};sprop-sps={B64(Sps)};sprop-pps={B64(Pps)}\r\n");
        }
        else
        {
            string profile = Sps is { Length: >= 4 } ? Convert.ToHexString(Sps, 1, 3) : "42E01F";
            sb.Append("a=rtpmap:96 H264/90000\r\n");
            sb.Append($"a=fmtp:96 packetization-mode=1;profile-level-id={profile};sprop-parameter-sets={B64(Sps)},{B64(Pps)}\r\n");
        }
        if (Width > 0) sb.Append($"a=cliprect:0,0,{Height},{Width}\r\n");
        if (Fps > 0) sb.Append($"a=framerate:{Fps}\r\n");
        sb.Append("a=control:trackID=0\r\n");

        if (Audio is { } a)
        {
            sb.Append("m=audio 0 RTP/AVP 97\r\n");
            sb.Append($"a=rtpmap:97 MPEG4-GENERIC/{a.SampleRate}/{a.Channels}\r\n");
            sb.Append($"a=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config={a.ConfigHex}\r\n");
            sb.Append("a=control:trackID=1\r\n");
        }
        return sb.ToString();
    }

    public int ClockRate(int track) => track == 0 ? VideoClock : Audio?.SampleRate ?? 8000;

    private static string B64(byte[]? b) => b is null ? "" : Convert.ToBase64String(b);
}
