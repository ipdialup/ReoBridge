using System.Text;

namespace ReoBridge.Rtsp;

public sealed class RtspRequest
{
    public required string Method { get; init; }
    public required string Uri { get; init; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string CSeq => Header("CSeq") ?? "0";

    /// <summary>The URL path without scheme, host, query or surrounding slashes, e.g. "Indoor/main/trackID=0".</summary>
    public string Path
    {
        get
        {
            string u = Uri;
            if (u.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = u.IndexOf('/', u.IndexOf("://", StringComparison.Ordinal) + 3);
                u = slash < 0 ? "" : u[slash..];
            }
            int q = u.IndexOf('?');
            if (q >= 0) u = u[..q];
            return System.Uri.UnescapeDataString(u).Trim('/');
        }
    }
}

public sealed class RtspResponse
{
    public RtspResponse(int code, string reason)
    {
        Code = code;
        Reason = reason;
    }

    public int Code { get; }
    public string Reason { get; }
    public List<KeyValuePair<string, string>> Headers { get; } = [];
    public byte[] Body { get; set; } = [];

    public RtspResponse With(string name, string value)
    {
        Headers.Add(new(name, value));
        return this;
    }

    public byte[] Serialize(string cseq)
    {
        var sb = new StringBuilder();
        sb.Append($"RTSP/1.0 {Code} {Reason}\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        sb.Append("Server: ReoBridge\r\n");
        sb.Append($"Date: {DateTime.UtcNow:R}\r\n");
        foreach (var h in Headers) sb.Append($"{h.Key}: {h.Value}\r\n");
        if (Body.Length > 0) sb.Append($"Content-Length: {Body.Length}\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        if (Body.Length == 0) return head;
        var all = new byte[head.Length + Body.Length];
        head.CopyTo(all, 0);
        Body.CopyTo(all, head.Length);
        return all;
    }

    public static RtspResponse Ok() => new(200, "OK");
}

/// <summary>Reads RTSP requests and skips interleaved ($) binary frames from one connection.</summary>
public sealed class RtspReader
{
    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _start;
    private int _end;

    public RtspReader(Stream stream) => _stream = stream;

    /// <summary>Returns the next request, or null at end of stream. Interleaved frames invoke onInterleaved.</summary>
    public async Task<RtspRequest?> ReadAsync(Action<int>? onInterleaved, CancellationToken ct)
    {
        while (true)
        {
            if (!await EnsureAsync(1, ct)) return null;
            if (_buf[_start] == (byte)'$')
            {
                if (!await EnsureAsync(4, ct)) return null;
                int channel = _buf[_start + 1];
                int len = (_buf[_start + 2] << 8) | _buf[_start + 3];
                _start += 4;
                await SkipAsync(len, ct);
                onInterleaved?.Invoke(channel);
                continue;
            }

            // Text request: read up to the blank line.
            int headerEnd;
            while ((headerEnd = FindHeaderEnd()) < 0)
            {
                if (_end - _start >= _buf.Length) throw new InvalidDataException("RTSP request headers too large");
                if (!await FillAsync(ct)) return null;
            }
            string text = Encoding.UTF8.GetString(_buf, _start, headerEnd - _start);
            _start = headerEnd + 4;

            var lines = text.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3) throw new InvalidDataException($"Bad RTSP request line: {lines[0]}");
            var req = new RtspRequest { Method = first[0].ToUpperInvariant(), Uri = first[1] };
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) req.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            if (int.TryParse(req.Header("Content-Length"), out int bodyLen) && bodyLen > 0)
            {
                if (bodyLen > _buf.Length) throw new InvalidDataException("RTSP body too large");
                if (!await EnsureAsync(bodyLen, ct)) return null;
                req.Body = _buf.AsSpan(_start, bodyLen).ToArray();
                _start += bodyLen;
            }
            return req;
        }
    }

    private int FindHeaderEnd()
    {
        var span = _buf.AsSpan(_start, _end - _start);
        int i = span.IndexOf("\r\n\r\n"u8);
        return i < 0 ? -1 : _start + i;
    }

    private async Task<bool> EnsureAsync(int count, CancellationToken ct)
    {
        while (_end - _start < count)
            if (!await FillAsync(ct)) return false;
        return true;
    }

    private async Task SkipAsync(int count, CancellationToken ct)
    {
        while (count > 0)
        {
            if (_end == _start && !await FillAsync(ct)) return;
            int n = Math.Min(count, _end - _start);
            _start += n;
            count -= n;
        }
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        int n = await _stream.ReadAsync(_buf.AsMemory(_end), ct);
        if (n == 0) return false;
        _end += n;
        return true;
    }
}
