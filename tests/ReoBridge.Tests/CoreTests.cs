using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ReoBridge.Baichuan;
using ReoBridge.Media;
using ReoBridge.Rtsp;
using Xunit;

namespace ReoBridge.Tests;

public class CryptoTests
{
    [Fact]
    public void Xor_RoundTrips()
    {
        var data = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><body/>");
        var enc = BcCrypto.Xor(3, data);
        Assert.NotEqual(data, enc);
        Assert.Equal(data, BcCrypto.Xor(3, enc));
    }

    [Fact]
    public void Md5Login_Is31UpperHexChars() =>
        Assert.Equal("21232F297A57A5A743894A0E4A801FC", BcCrypto.Md5Login("admin"));

    [Fact]
    public void AesKey_Is16AsciiHexChars()
    {
        var key = BcCrypto.AesKey("nonce", "pw");
        var expected = Convert.ToHexString(MD5.HashData("nonce-pw"u8.ToArray()))[..16];
        Assert.Equal(expected, Encoding.ASCII.GetString(key));
    }

    [Fact]
    public void Aes_RoundTripsOddLengths()
    {
        using var c = new BcCrypto();
        c.UseAes("abc", "secret", full: true);
        var data = RandomNumberGenerator.GetBytes(37);
        var enc = c.AesEncrypt(data);
        Assert.Equal(37, enc.Length);
        Assert.Equal(data, c.AesDecrypt(enc));
        // CFB: a prefix decrypts on its own (FullAes media only encrypts a prefix).
        Assert.Equal(data[..20], c.AesDecrypt(enc.AsSpan(0, 20)));
    }
}

public class MediaParserTests
{
    private static byte[] VideoRecord(bool iframe, byte[] payload, uint micros)
    {
        int pad = payload.Length % 8 == 0 ? 0 : 8 - payload.Length % 8;
        var r = new byte[24 + payload.Length + pad];
        Encoding.ASCII.GetBytes(iframe ? "00dc" : "01dc").CopyTo(r, 0);
        Encoding.ASCII.GetBytes("H264").CopyTo(r, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(16), micros);
        payload.CopyTo(r, 24);
        return r;
    }

    [Fact]
    public void Parses_FramesSplitAcrossChunks_AndResyncs()
    {
        var a = VideoRecord(true, [0, 0, 0, 1, 0x65, 1, 2, 3, 4], 1000);
        var b = VideoRecord(false, [0, 0, 0, 1, 0x41, 9], 67000);
        var all = a.Concat(new byte[] { 0xde, 0xad }).Concat(b).ToArray();
        var p = new BcMediaParser();
        var frames = new List<BcMediaFrame>();
        for (int i = 0; i < all.Length; i += 5)
            frames.AddRange(p.Push(all.AsSpan(i, Math.Min(5, all.Length - i))));
        Assert.Equal(2, frames.Count);
        Assert.Equal(BcMediaKind.IFrame, frames[0].Kind);
        Assert.Equal(1000u, frames[0].Microseconds);
        Assert.Equal(BcMediaKind.PFrame, frames[1].Kind);
        Assert.Equal(2, p.ResyncBytes);
    }
}

public class CodecTests
{
    [Fact]
    public void SplitAnnexB_HandlesThreeAndFourByteStartCodes()
    {
        byte[] d = [0, 0, 0, 1, 0x67, 0xAA, 0, 0, 1, 0x68, 0xBB, 0, 0, 0, 1, 0x65, 0xCC, 0xDD];
        var nals = Nal.SplitAnnexB(d);
        Assert.Equal(3, nals.Count);
        Assert.Equal(7, Nal.Type(VideoCodec.H264, nals[0]));
        Assert.Equal(8, Nal.Type(VideoCodec.H264, nals[1]));
        Assert.Equal(5, Nal.Type(VideoCodec.H264, nals[2]));
        Assert.True(Nal.IsKeyframe(VideoCodec.H264, 5));
    }

    [Fact]
    public void H264_LargeNal_IsFragmentedAsFuA()
    {
        var nal = new byte[5000];
        nal[0] = 0x65;
        for (int i = 1; i < nal.Length; i++) nal[i] = (byte)i;
        var chunks = RtpPacketizer.Video(VideoCodec.H264, [new ArraySegment<byte>(nal)]);
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Payload.Length <= RtpPacketizer.MaxPayload));
        Assert.Equal(28, chunks[0].Payload[0] & 0x1F);           // FU-A
        Assert.Equal(0x80 | 5, chunks[0].Payload[1]);            // start bit + type
        Assert.Equal(0x40 | 5, chunks[^1].Payload[1]);           // end bit + type
        Assert.True(chunks[^1].Marker);
        Assert.All(chunks.SkipLast(1), c => Assert.False(c.Marker));
        var rebuilt = new List<byte> { nal[0] };
        foreach (var c in chunks) rebuilt.AddRange(c.Payload.Skip(2));
        Assert.Equal(nal, rebuilt.ToArray());
    }

    [Fact]
    public void Adts_GivesAudioSpecificConfig()
    {
        // 16 kHz mono AAC-LC, as the E1 sends it
        byte[] header = [0xFF, 0xF1, 0x60, 0x40, 0x00, 0x00, 0x00];
        int len = header.Length + 4;
        header[3] |= (byte)((len >> 11) & 0x3);
        header[4] = (byte)(len >> 3);
        header[5] = (byte)(((len & 7) << 5) | 0x1F);
        header[6] = 0xFC;
        var frame = header.Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        var units = AacInfo.SplitAdts(frame, out var info);
        Assert.NotNull(info);
        Assert.Equal(16000, info!.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal("1408", info.ConfigHex);
        Assert.Single(units);
        Assert.Equal(4, units[0].Count);
    }
}

public class RtspTests
{
    [Fact]
    public void Digest_AcceptsCorrectResponse_RejectsWrong()
    {
        var auth = new RtspAuth([new RtspUser("admin", "pw")]);
        string nonce = "abc123";
        string uri = "rtsp://h/Indoor/main";
        static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        string resp = Md5($"{Md5($"admin:{RtspAuth.Realm}:pw")}:{nonce}:{Md5($"DESCRIBE:{uri}")}");

        RtspRequest Req(string r) => new() { Method = "DESCRIBE", Uri = uri, Headers =
            { ["Authorization"] = $"Digest username=\"admin\", realm=\"{RtspAuth.Realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{r}\"" } };
        Assert.True(auth.Check(Req(resp), nonce));
        Assert.False(auth.Check(Req(new string('0', 32)), nonce));
    }

    [Fact]
    public void Basic_Works()
    {
        var auth = new RtspAuth([new RtspUser("admin", "pw")]);
        var ok = new RtspRequest { Method = "DESCRIBE", Uri = "rtsp://h/x", Headers = { ["Authorization"] = "Basic " + Convert.ToBase64String("admin:pw"u8.ToArray()) } };
        var bad = new RtspRequest { Method = "DESCRIBE", Uri = "rtsp://h/x", Headers = { ["Authorization"] = "Basic " + Convert.ToBase64String("admin:no"u8.ToArray()) } };
        Assert.True(auth.Check(ok, ""));
        Assert.False(auth.Check(bad, ""));
    }

    [Fact]
    public void Path_StripsHostQueryAndSlashes()
    {
        var r = new RtspRequest { Method = "SETUP", Uri = "rtsp://192.168.0.6:8554/Indoor/main/trackID=0?x=1" };
        Assert.Equal("Indoor/main/trackID=0", r.Path);
    }

    [Fact]
    public void Transport_ParsesInterleaved()
    {
        var t = RtspServer.ParseTransport("RTP/AVP/TCP;unicast;interleaved=2-3", 1, null);
        Assert.NotNull(t);
        Assert.True(t!.Tcp);
        Assert.Equal(2, t.RtpChannel);
        Assert.Equal(3, t.RtcpChannel);
    }
}
