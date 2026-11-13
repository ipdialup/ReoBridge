using System.Buffers.Binary;

namespace ReoBridge.Baichuan;

/// <summary>Message ids used by ReoBridge.</summary>
public static class BcMsgId
{
    public const uint Login = 1;
    public const uint Logout = 2;
    public const uint Video = 3;
    public const uint VideoStop = 4;
    public const uint PtzControl = 18;
    public const uint PtzPreset = 19;
    public const uint GetPtzPresets = 190;
    public const uint Snapshot = 109;
    public const uint Ping = 93;
}

/// <summary>
/// One Baichuan message. Wire format (little endian):
/// magic u32, msgId u32, bodyLen u32, channel u8, streamType u8, msgNum u16,
/// responseCode u16, class u16, then payloadOffset u32 for classes 0x6414 and 0x0000.
/// The body is an optional "extension" XML (up to payloadOffset) followed by the payload.
/// </summary>
public sealed class BcMessage
{
    public const uint Magic = 0x0abcdef0;
    public const uint MagicReversed = 0x0fedcba0;

    public const ushort ClassLegacy = 0x6514;
    public const ushort ClassModernNoOffset = 0x6614;
    public const ushort ClassModern = 0x6414;
    public const ushort ClassModernZero = 0x0000;

    public uint MsgId { get; init; }
    public byte Channel { get; init; }
    public byte StreamType { get; init; }
    public ushort MsgNum { get; init; }
    public ushort ResponseCode { get; init; }
    public ushort Class { get; init; } = ClassModern;

    /// <summary>Extension bytes as on the wire (encrypted), or empty.</summary>
    public byte[] Extension { get; init; } = [];
    /// <summary>Payload bytes as on the wire (encrypted when XML), or empty.</summary>
    public byte[] Payload { get; init; } = [];

    public static bool HasPayloadOffset(ushort cls) => cls == ClassModern || cls == ClassModernZero;
    public static int HeaderSize(ushort cls) => HasPayloadOffset(cls) ? 24 : 20;

    public byte[] Serialize()
    {
        int header = HeaderSize(Class);
        int bodyLen = Extension.Length + Payload.Length;
        var buf = new byte[header + bodyLen];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], MsgId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)bodyLen);
        s[12] = Channel;
        s[13] = StreamType;
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], MsgNum);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], ResponseCode);
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], Class);
        if (HasPayloadOffset(Class))
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)Extension.Length);
        Extension.CopyTo(s[header..]);
        Payload.CopyTo(s[(header + Extension.Length)..]);
        return buf;
    }

    /// <summary>Reads one message from the stream; throws EndOfStreamException on close.</summary>
    public static async Task<BcMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[24];
        await stream.ReadExactlyAsync(head.AsMemory(0, 20), ct);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (magic != Magic && magic != MagicReversed)
            throw new InvalidDataException($"Bad Baichuan magic 0x{magic:x8}");
        uint msgId = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        uint bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        ushort cls = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(18));
        if (bodyLen > 16 * 1024 * 1024)
            throw new InvalidDataException($"Baichuan body too large ({bodyLen})");

        uint offset = 0;
        if (HasPayloadOffset(cls))
        {
            await stream.ReadExactlyAsync(head.AsMemory(20, 4), ct);
            offset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(20));
            if (offset > bodyLen) throw new InvalidDataException("Payload offset past body");
        }

        var body = new byte[bodyLen];
        await stream.ReadExactlyAsync(body, ct);
        return new BcMessage
        {
            MsgId = msgId,
            Channel = head[12],
            StreamType = head[13],
            MsgNum = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14)),
            ResponseCode = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(16)),
            Class = cls,
            Extension = body[..(int)offset],
            Payload = body[(int)offset..],
        };
    }

    public override string ToString() =>
        $"msg {MsgId} num {MsgNum} code 0x{ResponseCode:x4} class 0x{Class:x4} ext {Extension.Length} payload {Payload.Length}";
}
