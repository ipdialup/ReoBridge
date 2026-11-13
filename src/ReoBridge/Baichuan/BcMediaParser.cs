using System.Buffers.Binary;
using System.Text;

namespace ReoBridge.Baichuan;

public enum BcMediaKind { Info, IFrame, PFrame, Aac, Adpcm }

/// <summary>One parsed media record. Video data is Annex B (start-code delimited NAL units).</summary>
public sealed record BcMediaFrame(BcMediaKind Kind, byte[] Data)
{
    /// <summary>"H264" or "H265" for video frames.</summary>
    public string? Codec { get; init; }
    /// <summary>Camera clock in microseconds (wraps at 2^32).</summary>
    public uint Microseconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
}

/// <summary>
/// Turns the byte stream carried in video messages into frames. Records can span
/// messages, so bytes are buffered until a record is complete. Each record is a
/// 4-byte ASCII tag, a small header, the data, then zero padding to 8 bytes:
/// "1001"/"1002" info, "x0dc" I-frame, "x1dc" P-frame, "05wb" AAC, "01wb" ADPCM.
/// </summary>
public sealed class BcMediaParser
{
    private byte[] _buf = new byte[1 << 20];
    private int _start;
    private int _end;

    /// <summary>Bytes skipped because they did not start a known record.</summary>
    public long ResyncBytes { get; private set; }

    public IEnumerable<BcMediaFrame> Push(ReadOnlySpan<byte> data)
    {
        Append(data);
        var frames = new List<BcMediaFrame>();
        while (true)
        {
            var span = _buf.AsSpan(_start, _end - _start);
            if (span.Length < 4) break;
            var (status, frame, used) = TryParse(span);
            if (status == ParseStatus.NeedMore) break;
            if (status == ParseStatus.Bad)
            {
                int skip = FindNextTag(span[1..]);
                skip = skip < 0 ? Math.Max(1, span.Length - 3) : skip + 1;
                ResyncBytes += skip;
                _start += skip;
                continue;
            }
            _start += used;
            if (frame is not null) frames.Add(frame);
        }
        return frames;
    }

    public void Reset()
    {
        _start = _end = 0;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_start > 0 && _start == _end) _start = _end = 0;
        if (_end + data.Length > _buf.Length)
        {
            int live = _end - _start;
            if (live + data.Length > _buf.Length)
            {
                var bigger = new byte[Math.Max(_buf.Length * 2, live + data.Length)];
                _buf.AsSpan(_start, live).CopyTo(bigger);
                _buf = bigger;
            }
            else
            {
                _buf.AsSpan(_start, live).CopyTo(_buf);
            }
            _start = 0;
            _end = live;
        }
        data.CopyTo(_buf.AsSpan(_end));
        _end += data.Length;
    }

    private enum ParseStatus { Ok, NeedMore, Bad }

    private static int Pad(int size) => size % 8 == 0 ? 0 : 8 - size % 8;

    private static bool IsVideoTag(ReadOnlySpan<byte> t, out bool iframe)
    {
        iframe = false;
        if (t.Length < 4 || t[2] != (byte)'d' || t[3] != (byte)'c') return false;
        if (t[0] < (byte)'0' || t[0] > (byte)'9') return false;
        if (t[1] == (byte)'0') { iframe = true; return true; }
        return t[1] == (byte)'1';
    }

    private static bool IsKnownTag(ReadOnlySpan<byte> t) =>
        IsVideoTag(t, out _) || t.StartsWith("1001"u8) || t.StartsWith("1002"u8) ||
        t.StartsWith("05wb"u8) || t.StartsWith("01wb"u8);

    private static int FindNextTag(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i + 4 <= span.Length; i++)
            if (IsKnownTag(span.Slice(i, 4)))
                return i;
        return -1;
    }

    private static (ParseStatus, BcMediaFrame?, int) TryParse(ReadOnlySpan<byte> s)
    {
        var tag = s[..4];
        if (tag.SequenceEqual("1001"u8) || tag.SequenceEqual("1002"u8))
        {
            if (s.Length < 32) return (ParseStatus.NeedMore, null, 0);
            if (BinaryPrimitives.ReadUInt32LittleEndian(s[4..]) != 32) return (ParseStatus.Bad, null, 0);
            var info = new BcMediaFrame(BcMediaKind.Info, [])
            {
                Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
                Fps = s[17],
            };
            return (ParseStatus.Ok, info, 32);
        }

        if (IsVideoTag(tag, out bool iframe))
        {
            if (s.Length < 24) return (ParseStatus.NeedMore, null, 0);
            var codec = Encoding.ASCII.GetString(s[4..8]);
            if (codec != "H264" && codec != "H265") return (ParseStatus.Bad, null, 0);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
            uint extra = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
            uint micros = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
            if (size > 8 * 1024 * 1024 || extra > 1024) return (ParseStatus.Bad, null, 0);
            int total = 24 + (int)extra + (int)size + Pad((int)size);
            if (s.Length < total) return (ParseStatus.NeedMore, null, 0);
            var data = s.Slice(24 + (int)extra, (int)size).ToArray();
            return (ParseStatus.Ok, new BcMediaFrame(iframe ? BcMediaKind.IFrame : BcMediaKind.PFrame, data)
            {
                Codec = codec,
                Microseconds = micros,
            }, total);
        }

        bool aac = tag.SequenceEqual("05wb"u8);
        if (aac || tag.SequenceEqual("01wb"u8))
        {
            if (s.Length < 8) return (ParseStatus.NeedMore, null, 0);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(s[6..]) != size) return (ParseStatus.Bad, null, 0);
            int total = 8 + size + Pad(size);
            if (s.Length < total) return (ParseStatus.NeedMore, null, 0);
            var data = s.Slice(8, size).ToArray();
            return (ParseStatus.Ok, new BcMediaFrame(aac ? BcMediaKind.Aac : BcMediaKind.Adpcm, data), total);
        }

        return (ParseStatus.Bad, null, 0);
    }
}
