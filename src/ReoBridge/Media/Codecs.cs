namespace ReoBridge.Media;

public enum VideoCodec { H264, H265 }

/// <summary>Annex B / NAL unit helpers.</summary>
public static class Nal
{
    /// <summary>Splits an Annex B buffer (00 00 01 / 00 00 00 01 start codes) into NAL units.</summary>
    public static List<ArraySegment<byte>> SplitAnnexB(byte[] data)
    {
        var nals = new List<ArraySegment<byte>>();
        int start = -1;
        int i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0) Add(nals, data, start, i);
                i += 3;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0) Add(nals, data, start, data.Length);
        else if (data.Length > 0) nals.Add(new ArraySegment<byte>(data)); // no start codes: one raw NAL
        return nals;
    }

    // A NAL never ends in a zero byte, so trailing zeros belong to the next 4-byte start code.
    private static void Add(List<ArraySegment<byte>> nals, byte[] data, int start, int end)
    {
        while (end > start && data[end - 1] == 0) end--;
        if (end > start) nals.Add(new ArraySegment<byte>(data, start, end - start));
    }

    public static int Type(VideoCodec codec, ArraySegment<byte> nal) =>
        codec == VideoCodec.H264 ? nal[0] & 0x1F : (nal[0] >> 1) & 0x3F;

    public static bool IsKeyframe(VideoCodec codec, int type) =>
        codec == VideoCodec.H264 ? type == 5 : type is >= 16 and <= 21;
}

/// <summary>What ADTS tells us about an AAC stream, and the RFC 3640 config string.</summary>
public sealed record AacInfo(int ObjectType, int SampleRate, int Channels, int FrequencyIndex)
{
    private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>AudioSpecificConfig: 5 bits object type, 4 bits frequency index, 4 bits channels, 3 zero bits.</summary>
    public string ConfigHex => ((ObjectType << 11) | (FrequencyIndex << 7) | (Channels << 3)).ToString("X4");

    /// <summary>Splits a buffer of ADTS frames into raw AAC access units.</summary>
    public static List<ArraySegment<byte>> SplitAdts(byte[] data, out AacInfo? info)
    {
        info = null;
        var units = new List<ArraySegment<byte>>();
        int pos = 0;
        while (pos + 7 <= data.Length)
        {
            if (data[pos] != 0xFF || (data[pos + 1] & 0xF0) != 0xF0) break;
            bool noCrc = (data[pos + 1] & 1) == 1;
            int headerLen = noCrc ? 7 : 9;
            int objectType = (data[pos + 2] >> 6) + 1;
            int freq = (data[pos + 2] >> 2) & 0x0F;
            int channels = ((data[pos + 2] & 1) << 2) | (data[pos + 3] >> 6);
            int frameLen = ((data[pos + 3] & 3) << 11) | (data[pos + 4] << 3) | (data[pos + 5] >> 5);
            if (frameLen < headerLen || pos + frameLen > data.Length || freq >= Rates.Length) break;
            info ??= new AacInfo(objectType, Rates[freq], channels, freq);
            units.Add(new ArraySegment<byte>(data, pos + headerLen, frameLen - headerLen));
            pos += frameLen;
        }
        return units;
    }
}

/// <summary>One RTP payload (no RTP header) and whether it ends the frame (marker bit).</summary>
public readonly record struct RtpChunk(byte[] Payload, bool Marker);

/// <summary>Splits frames into RTP payloads (RFC 6184 H.264, RFC 7798 H.265, RFC 3640 AAC).</summary>
public static class RtpPacketizer
{
    public const int MaxPayload = 1400;

    public static List<RtpChunk> Video(VideoCodec codec, IReadOnlyList<ArraySegment<byte>> nals)
    {
        var chunks = new List<RtpChunk>();
        for (int n = 0; n < nals.Count; n++)
        {
            var nal = nals[n];
            bool lastNal = n == nals.Count - 1;
            if (nal.Count <= MaxPayload)
            {
                chunks.Add(new RtpChunk(nal.ToArray(), lastNal));
                continue;
            }

            int headerLen = codec == VideoCodec.H264 ? 1 : 2;
            byte[] fuPrefix;
            int type;
            if (codec == VideoCodec.H264)
            {
                type = nal[0] & 0x1F;
                fuPrefix = [(byte)((nal[0] & 0xE0) | 28)]; // FU-A indicator
            }
            else
            {
                type = (nal[0] >> 1) & 0x3F;
                fuPrefix = [(byte)((nal[0] & 0x81) | (49 << 1)), nal[1]]; // FU payload header
            }

            int pos = headerLen;
            int room = MaxPayload - fuPrefix.Length - 1;
            while (pos < nal.Count)
            {
                int len = Math.Min(room, nal.Count - pos);
                bool first = pos == headerLen;
                bool last = pos + len == nal.Count;
                var p = new byte[fuPrefix.Length + 1 + len];
                fuPrefix.CopyTo(p, 0);
                p[fuPrefix.Length] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | type);
                nal.AsSpan(pos, len).CopyTo(p.AsSpan(fuPrefix.Length + 1));
                chunks.Add(new RtpChunk(p, last && lastNal));
                pos += len;
            }
        }
        return chunks;
    }

    /// <summary>One AAC access unit per packet: AU-headers-length (16 bits), then a 13-bit size + 3-bit index.</summary>
    public static RtpChunk Aac(ArraySegment<byte> au)
    {
        var p = new byte[4 + au.Count];
        p[0] = 0;
        p[1] = 16;
        p[2] = (byte)(au.Count >> 5);
        p[3] = (byte)((au.Count & 0x1F) << 3);
        au.AsSpan().CopyTo(p.AsSpan(4));
        return new RtpChunk(p, true);
    }
}
