using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace ReoBridge.Baichuan;

/// <summary>A reply to a request: its response code and decrypted XML (if any).</summary>
public sealed record BcReply(ushort Code, XElement? Body);

/// <summary>An open media stream: the raw BcMedia bytes, in order, as the camera sends them.</summary>
public sealed class BcVideoStream
{
    internal BcVideoStream(ushort msgNum, bool sub)
    {
        MsgNum = msgNum;
        IsSub = sub;
    }

    public ushort MsgNum { get; }
    public bool IsSub { get; }
    internal Channel<byte[]> Data { get; } = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
    public ChannelReader<byte[]> Reader => Data.Reader;
}

/// <summary>
/// One TCP connection to a camera speaking the Baichuan protocol (port 9000).
/// Call <see cref="ConnectAsync"/>, then start streams; <see cref="Completion"/>
/// finishes (usually with an exception) when the connection drops.
/// </summary>
public sealed class BcClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly ILogger _log;

    private readonly BcCrypto _crypto = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<(uint, ushort), TaskCompletionSource<BcReply>> _pending = new();
    private readonly ConcurrentDictionary<ushort, BcVideoStream> _streams = new();
    private readonly HashSet<ushort> _binaryMsgNums = [];
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _tcp;
    private NetworkStream? _net;
    private Task _readLoop = Task.CompletedTask;
    private int _msgNum;

    public BcClient(string host, int port, string username, string password, ILogger log)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        _log = log;
    }

    public BcEncryption Encryption => _crypto.Mode;
    public XElement? DeviceInfo { get; private set; }
    public Task Completion => _readLoop;

    /// <summary>Last time any message arrived from the camera.</summary>
    public DateTime LastReceiveUtc { get; private set; }

    private ushort NextMsgNum() => (ushort)Interlocked.Increment(ref _msgNum);

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _tcp.ConnectAsync(_host, _port, timeout.Token);
        }
        _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _net = _tcp.GetStream();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        await LoginAsync(ct);
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        ushort num = NextMsgNum();

        // Step 1: header-only legacy login asking for the best encryption (0xdc12 = full AES).
        var legacy = await RequestAsync(new BcMessage
        {
            MsgId = BcMsgId.Login, MsgNum = num, ResponseCode = 0xdc12, Class = BcMessage.ClassLegacy,
        }, ct);

        // The reply's response code is 0xddXX, where XX is the encryption the camera chose.
        int chosen = legacy.Code >> 8 == 0xdd ? legacy.Code & 0xff : -1;
        string nonce = legacy.Body?.Element("Encryption")?.Element("nonce")?.Value
            ?? throw new BcException($"Login: no nonce in reply (code 0x{legacy.Code:x4})");
        switch (chosen)
        {
            case 0x00: _crypto.UseNone(); break;
            case 0x01: _crypto.UseXor(); break;
            case 0x02: _crypto.UseAes(nonce, _password, full: false); break;
            case 0x12: _crypto.UseAes(nonce, _password, full: true); break;
            default: throw new BcException($"Login: unknown encryption 0x{legacy.Code:x4}");
        }

        // Step 2: modern login with MD5(user+nonce) / MD5(password+nonce).
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<body>\n" +
            "<LoginUser version=\"1.1\">\n" +
            $"<userName>{BcCrypto.Md5Login(_username + nonce)}</userName>\n" +
            $"<password>{BcCrypto.Md5Login(_password + nonce)}</password>\n" +
            "<userVer>1</userVer>\n</LoginUser>\n" +
            "<LoginNet version=\"1.1\">\n<type>LAN</type>\n<udpPort>0</udpPort>\n</LoginNet>\n</body>\n";
        var reply = await RequestAsync(new BcMessage
        {
            MsgId = BcMsgId.Login, MsgNum = num, Class = BcMessage.ClassModern,
            Payload = EncryptControl(BcMsgId.Login, 0, xml),
        }, ct);
        if (reply.Code != 200 || reply.Body?.Element("DeviceInfo") is not { } info)
            throw new BcAuthException($"Login rejected (code {reply.Code}) - check the camera username and password");
        DeviceInfo = info;
        _log.LogDebug("Logged in to {Host}, encryption {Enc}", _host, _crypto.Mode);
    }

    /// <summary>Asks the camera to start sending the main or sub stream.</summary>
    public async Task<BcVideoStream> StartVideoAsync(bool sub, CancellationToken ct)
    {
        ushort num = NextMsgNum();
        var stream = new BcVideoStream(num, sub);
        _streams[num] = stream;
        try
        {
            var reply = await RequestAsync(new BcMessage
            {
                MsgId = BcMsgId.Video, MsgNum = num, StreamType = (byte)(sub ? 1 : 0),
                Payload = EncryptControl(BcMsgId.Video, 0, PreviewXml(sub, includeType: true)),
            }, ct);
            if (reply.Code != 200) throw new BcException($"Camera refused the {(sub ? "sub" : "main")} stream (code {reply.Code})");
            return stream;
        }
        catch
        {
            _streams.TryRemove(num, out _);
            throw;
        }
    }

    public async Task StopVideoAsync(BcVideoStream stream, CancellationToken ct)
    {
        _streams.TryRemove(stream.MsgNum, out _);
        stream.Data.Writer.TryComplete();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await RequestAsync(new BcMessage
            {
                MsgId = BcMsgId.VideoStop, MsgNum = stream.MsgNum, StreamType = (byte)(stream.IsSub ? 1 : 0),
                Payload = EncryptControl(BcMsgId.VideoStop, 0, PreviewXml(stream.IsSub, includeType: false)),
            }, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or BcException or IOException)
        {
            // The camera often just stops; a missing reply is not a problem.
        }
    }

    public async Task PingAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await RequestAsync(new BcMessage { MsgId = BcMsgId.Ping, MsgNum = NextMsgNum() }, timeout.Token);
    }

    /// <summary>Sends an XML command (with a channel extension) and returns the reply.</summary>
    public Task<BcReply> CommandAsync(uint msgId, string? bodyXml, CancellationToken ct) =>
        RequestAsync(new BcMessage
        {
            MsgId = msgId, MsgNum = NextMsgNum(),
            Extension = EncryptControl(msgId, 0, "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<Extension version=\"1.1\">\n<channelId>0</channelId>\n</Extension>\n"),
            Payload = bodyXml is null ? [] : EncryptControl(msgId, 0, "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<body>\n" + bodyXml + "</body>\n"),
        }, ct);

    /// <summary>
    /// Starts moving (or stops) the camera. Command: left, right, up, down, leftUp, leftDown,
    /// rightUp, rightDown, zoomIn, zoomOut or stop. Speed is the camera's scale (1-64).
    /// </summary>
    public async Task PtzAsync(string command, int speed, CancellationToken ct)
    {
        var reply = await CommandAsync(BcMsgId.PtzControl,
            "<PtzControl version=\"1.1\">\n<channelId>0</channelId>\n" +
            $"<speed>{Math.Clamp(speed, 1, 64)}</speed>\n<command>{command}</command>\n</PtzControl>\n", ct);
        if (reply.Code != 200) throw new BcException($"Camera refused PTZ {command} (code {reply.Code})");
    }

    /// <summary>Preset positions stored on the camera (id, name).</summary>
    public async Task<List<(int Id, string Name)>> GetPresetsAsync(CancellationToken ct)
    {
        var reply = await CommandAsync(BcMsgId.GetPtzPresets, null, ct);
        if (reply.Code != 200) throw new BcException($"Camera refused the preset list (code {reply.Code})");
        return reply.Body?.Descendants("preset")
            .Select(p => (Id: int.TryParse(p.Element("id")?.Value, out var id) ? id : -1, Name: p.Element("name")?.Value ?? ""))
            .Where(p => p.Id >= 0)
            .ToList() ?? [];
    }

    /// <summary>Moves to (toPos) or saves the current position as (setPos) a preset.</summary>
    public async Task PresetAsync(int id, bool save, string? name, CancellationToken ct)
    {
        string nameXml = save && name is not null ? $"<name>{System.Security.SecurityElement.Escape(name)}</name>\n" : "";
        var reply = await CommandAsync(BcMsgId.PtzPreset,
            "<PtzPreset version=\"1.1\">\n<channelId>0</channelId>\n<presetList>\n<preset>\n" +
            $"<id>{id}</id>\n{nameXml}<command>{(save ? "setPos" : "toPos")}</command>\n</preset>\n</presetList>\n</PtzPreset>\n", ct);
        if (reply.Code != 200) throw new BcException($"Camera refused preset {id} (code {reply.Code})");
    }

    private static string PreviewXml(bool sub, bool includeType) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<body>\n<Preview version=\"1.1\">\n" +
        "<channelId>0</channelId>\n" +
        $"<handle>{(sub ? 256 : 0)}</handle>\n" +
        (includeType ? $"<streamType>{(sub ? "subStream" : "mainStream")}</streamType>\n" : "") +
        "</Preview>\n</body>\n";

    private byte[] EncryptControl(uint msgId, byte channel, string xml)
    {
        var level = ControlLevel(msgId);
        return _crypto.Encrypt(level, channel, Encoding.UTF8.GetBytes(xml));
    }

    // During login the camera never goes above XOR, even when it later uses AES.
    private BcEncryption ControlLevel(uint msgId) =>
        msgId == BcMsgId.Login && _crypto.Mode is BcEncryption.Aes or BcEncryption.FullAes
            ? BcEncryption.Xor
            : _crypto.Mode;

    private async Task<BcReply> RequestAsync(BcMessage msg, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = (msg.MsgId, msg.MsgNum);
        _pending[key] = tcs;
        try
        {
            await SendAsync(msg, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BcException($"No reply from the camera to message {msg.MsgId}");
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private async Task SendAsync(BcMessage msg, CancellationToken ct)
    {
        var net = _net ?? throw new InvalidOperationException("Not connected");
        var bytes = msg.Serialize();
        await _writeLock.WaitAsync(ct);
        try { await net.WriteAsync(bytes, ct); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await BcMessage.ReadAsync(_net!, ct);
                LastReceiveUtc = DateTime.UtcNow;
                Dispatch(msg);
            }
        }
        catch (Exception ex)
        {
            error = ex is EndOfStreamException ? new IOException("Camera closed the connection", ex) : ex;
        }
        finally
        {
            var fail = error ?? new OperationCanceledException();
            foreach (var p in _pending.Values) p.TrySetException(fail);
            foreach (var s in _streams.Values) s.Data.Writer.TryComplete(fail);
        }
        if (error is not null && !ct.IsCancellationRequested) throw error;
    }

    private void Dispatch(BcMessage msg)
    {
        bool modern = msg.Class != BcMessage.ClassLegacy;
        XElement? ext = null;
        if (modern && msg.Extension.Length > 0)
        {
            var extBytes = _crypto.Mode == BcEncryption.None
                ? msg.Extension
                : _crypto.Decrypt(ControlLevel(msg.MsgId), msg.Channel, msg.Extension);
            ext = ParseXml(extBytes);
            if (ext?.Element("binaryData")?.Value is { } bin)
            {
                if (bin == "0") _binaryMsgNums.Remove(msg.MsgNum);
                else _binaryMsgNums.Add(msg.MsgNum);
            }
        }

        bool binary = _binaryMsgNums.Contains(msg.MsgNum) && ext?.Element("binaryData")?.Value != "0";
        if (binary)
        {
            if (_pending.TryGetValue((msg.MsgId, msg.MsgNum), out var waiting))
                waiting.TrySetResult(new BcReply(msg.ResponseCode, null));
            if (msg.MsgId == BcMsgId.Video && _streams.TryGetValue(msg.MsgNum, out var stream))
                stream.Data.Writer.TryWrite(DecryptMedia(msg, ext));
            return;
        }

        XElement? body = null;
        if (modern && msg.Payload.Length > 0)
        {
            BcEncryption level;
            if (msg.MsgId == BcMsgId.Login && msg.ResponseCode >> 8 == 0xdd)
                level = (msg.ResponseCode & 0xff) == 0 ? BcEncryption.None : BcEncryption.Xor;
            else
                level = ControlLevel(msg.MsgId);
            body = ParseXml(_crypto.Decrypt(level, msg.Channel, msg.Payload));
        }

        if (_pending.TryGetValue((msg.MsgId, msg.MsgNum), out var tcs))
            tcs.TrySetResult(new BcReply(msg.ResponseCode, body));
        else
            _log.LogDebug("Unsolicited {Msg}", msg);
    }

    // With full AES only the first encryptLen bytes of each media payload are encrypted.
    private byte[] DecryptMedia(BcMessage msg, XElement? ext)
    {
        if (_crypto.Mode != BcEncryption.FullAes) return msg.Payload;
        if (ext?.Element("encryptLen")?.Value is not { } lenText || !int.TryParse(lenText, out int len) || len <= 0)
            return msg.Payload;
        len = Math.Min(len, msg.Payload.Length);
        var result = msg.Payload.ToArray();
        _crypto.AesDecrypt(msg.Payload.AsSpan(0, len)).CopyTo(result, 0);
        return result;
    }

    private XElement? ParseXml(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n');
        if (text.Length == 0) return null;
        try
        {
            return XElement.Parse(text);
        }
        catch (System.Xml.XmlException ex)
        {
            _log.LogWarning("Unreadable XML from camera ({Error}): {Start}", ex.Message,
                SecurityElement.Escape(text[..Math.Min(80, text.Length)]));
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _tcp?.Dispose(); } catch { }
        try { await _readLoop; } catch { }
        _crypto.Dispose();
        _cts.Dispose();
    }
}

public class BcException(string message) : Exception(message);
public sealed class BcAuthException(string message) : BcException(message);
