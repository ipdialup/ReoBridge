using Microsoft.Extensions.Logging;
using ReoBridge.Baichuan;
using ReoBridge.Media;

namespace ReoBridge.Bridge;

/// <summary>
/// Keeps one camera connected and its streams flowing into their media paths. Reconnects with
/// backoff, restarts a stream that goes quiet, and only pulls on-demand streams while someone watches.
/// </summary>
public sealed class CameraWorker
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(10);

    private readonly CameraOptions _opt;
    private readonly ILogger _log;
    private readonly List<StreamSlot> _slots = [];

    public CameraWorker(CameraOptions opt, ILogger log)
    {
        _opt = opt;
        _log = log;
        AddSlot(opt.MainStream, false, "main", [opt.Name, $"{opt.Name}/main", $"{opt.Name}/mainStream", $"{opt.Name}/main_stream"]);
        AddSlot(opt.SubStream, true, "sub", [$"{opt.Name}/sub", $"{opt.Name}/subStream", $"{opt.Name}/sub_stream"]);
    }

    private void AddSlot(string mode, bool sub, string name, string[] aliases)
    {
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode == "off") return;
        _slots.Add(new StreamSlot(new MediaPath(_opt.Name, name, sub, aliases), mode == "always"));
    }

    public string Name => _opt.Name;
    public string Host => _opt.Host;
    public IEnumerable<MediaPath> Paths => _slots.Select(s => s.Path);
    public string State { get; private set; } = "starting";
    public string? LastError { get; private set; }
    public DateTime? ConnectedSinceUtc { get; private set; }
    public BcEncryption? Encryption { get; private set; }
    public string? Model { get; private set; }
    public int Reconnects { get; private set; }
    public CameraOptions Options => _opt;
    /// <summary>False only when the camera says it has no pan/tilt (unknown until the first login counts as yes).</summary>
    public bool PtzSupported => _ptzMode is null || !_ptzMode.Equals("none", StringComparison.OrdinalIgnoreCase);
    public string? Serial { get; private set; }
    public string? Firmware { get; private set; }

    private volatile BcClient? _client;
    private string? _ptzMode;

    private BcClient Client => _client ?? throw new BcException($"{Name} is not connected");

    public Task PtzAsync(string command, int speed, CancellationToken ct) => Client.PtzAsync(command, speed, ct);
    public Task<List<(int Id, string Name)>> GetPresetsAsync(CancellationToken ct) => Client.GetPresetsAsync(ct);
    public Task PresetAsync(int id, bool save, string? name, CancellationToken ct) => Client.PresetAsync(id, save, name, ct);

    public async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                await RunSessionAsync(ct, () => attempt = 0);
                wait = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (BcAuthException ex)
            {
                LastError = ex.Message;
                State = "login refused";
                _log.LogError("login refused ({Error}); retrying in 60 s", ex.Message);
                wait = TimeSpan.FromSeconds(60);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                State = "disconnected";
                wait = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
                _log.LogWarning("{Error}; reconnecting in {Wait} s", ex.Message, wait.TotalSeconds);
            }
            finally
            {
                ConnectedSinceUtc = null;
                foreach (var s in _slots) s.Path.SourceLost();
            }
            Reconnects++;
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
        }
        State = "stopped";
    }

    private async Task RunSessionAsync(CancellationToken ct, Action healthy)
    {
        State = "connecting";
        await using var client = new BcClient(_opt.Host, _opt.Port, _opt.Username, _opt.Password, _log);
        await client.ConnectAsync(ct);
        Encryption = client.Encryption;
        string? Info(string name) => client.DeviceInfo?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        Model = Info("type") ?? Info("model");
        _ptzMode = Info("ptzMode");
        Serial = Info("serialNumber") ?? Info("uid");
        Firmware = Info("firmwareVersion") ?? Info("firmVersion");
        _client = client;
        ConnectedSinceUtc = DateTime.UtcNow;
        State = "connected";
        LastError = null;
        _log.LogInformation("logged in to {Host} ({Encryption})", _opt.Host, client.Encryption);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sct = sessionCts.Token;
        var lastPing = DateTime.UtcNow;
        try
        {
            while (!sct.IsCancellationRequested)
            {
                if (client.Completion.IsCompleted)
                    throw new IOException("camera closed the connection");

                foreach (var slot in _slots)
                {
                    bool want = slot.AlwaysOn || slot.Path.Wanted;
                    if (want && slot.Stream is null)
                    {
                        slot.Stream = await client.StartVideoAsync(slot.Path.IsSub, sct);
                        slot.StartedUtc = DateTime.UtcNow;
                        slot.Pump = PumpAsync(slot, slot.Stream, sct);
                        _log.LogInformation("started {Stream} stream", slot.Path.Stream);
                    }
                    else if (!want && slot.Stream is not null)
                    {
                        _log.LogInformation("stopping {Stream} stream (no viewers)", slot.Path.Stream);
                        await StopSlotAsync(client, slot, sct);
                    }

                    if (slot.Stream is not null)
                    {
                        var last = slot.Path.LastFrameUtc is { } lf && lf > slot.StartedUtc ? lf : slot.StartedUtc;
                        if (DateTime.UtcNow - last > StallTimeout)
                            throw new IOException($"no video on {slot.Path.Stream} for {StallTimeout.TotalSeconds:F0} s");
                        if (slot.Pump is { IsFaulted: true } p)
                            throw new IOException($"{slot.Path.Stream} stream failed: {p.Exception?.GetBaseException().Message}");
                        if (slot.Path.LastFrameUtc > slot.StartedUtc) healthy();
                    }
                }

                if (DateTime.UtcNow - lastPing > TimeSpan.FromSeconds(30) && _slots.All(s => s.Stream is null))
                {
                    lastPing = DateTime.UtcNow;
                    await client.PingAsync(sct);
                    healthy();
                }
                await Task.Delay(500, sct);
            }
        }
        finally
        {
            _client = null;
            sessionCts.Cancel();
            foreach (var slot in _slots)
            {
                if (slot.Pump is not null) { try { await slot.Pump; } catch { } }
                slot.Stream = null;
                slot.Pump = null;
            }
        }
    }

    private static async Task StopSlotAsync(BcClient client, StreamSlot slot, CancellationToken ct)
    {
        var stream = slot.Stream!;
        slot.Stream = null;
        try { await client.StopVideoAsync(stream, ct); } catch (TimeoutException) { }
        if (slot.Pump is not null) { try { await slot.Pump.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (TimeoutException) { } }
        slot.Pump = null;
        slot.Path.SourceLost();
    }

    private async Task PumpAsync(StreamSlot slot, BcVideoStream stream, CancellationToken ct)
    {
        await Task.Yield();
        var parser = new BcMediaParser();
        var path = slot.Path;
        try
        {
            while (await stream.Reader.WaitToReadAsync(ct))
            {
                while (stream.Reader.TryRead(out var chunk))
                {
                    if (slot.Stream != stream) return; // stopped
                    foreach (var f in parser.Push(chunk))
                    {
                        switch (f.Kind)
                        {
                            case BcMediaKind.Info: path.OnInfo(f); break;
                            case BcMediaKind.IFrame or BcMediaKind.PFrame: path.OnVideo(f); break;
                            default: path.OnAudio(f); break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (parser.ResyncBytes > 0)
                _log.LogWarning("{Stream} skipped {Bytes} unparseable bytes", path.Stream, parser.ResyncBytes);
        }
    }

    private sealed class StreamSlot(MediaPath path, bool alwaysOn)
    {
        public MediaPath Path { get; } = path;
        public bool AlwaysOn { get; } = alwaysOn;
        public BcVideoStream? Stream { get; set; }
        public Task? Pump { get; set; }
        public DateTime StartedUtc { get; set; }
    }
}
