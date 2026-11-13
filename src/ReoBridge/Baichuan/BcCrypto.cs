using System.Security.Cryptography;
using System.Text;

namespace ReoBridge.Baichuan;

/// <summary>Encryption levels a camera can negotiate during login.</summary>
public enum BcEncryption
{
    /// <summary>Plain text (very old firmware).</summary>
    None,
    /// <summary>Fixed-key XOR on control messages ("BCEncrypt").</summary>
    Xor,
    /// <summary>AES-128-CFB on control messages; media is plain.</summary>
    Aes,
    /// <summary>AES-128-CFB on control messages and on the start of each media payload.</summary>
    FullAes,
}

/// <summary>
/// The camera's two ciphers. Both are stateless per message: XOR restarts its key
/// cycle at the header's channel byte, AES restarts CFB from the fixed IV.
/// </summary>
public sealed class BcCrypto : IDisposable
{
    private static readonly byte[] XorKey = [0x1F, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78, 0xFF];
    private static readonly byte[] Iv = "0123456789abcdef"u8.ToArray();

    private Aes? _aes;

    public BcEncryption Mode { get; private set; } = BcEncryption.None;

    public void UseNone() { Mode = BcEncryption.None; }
    public void UseXor() { Mode = BcEncryption.Xor; }

    public void UseAes(string nonce, string password, bool full)
    {
        _aes?.Dispose();
        _aes = Aes.Create();
        _aes.Key = AesKey(nonce, password);
        Mode = full ? BcEncryption.FullAes : BcEncryption.Aes;
    }

    /// <summary>Key = first 16 characters of the upper-case hex MD5 of "nonce-password".</summary>
    public static byte[] AesKey(string nonce, string password)
    {
        var hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{nonce}-{password}")));
        return Encoding.ASCII.GetBytes(hex[..16]);
    }

    public static byte[] Xor(byte offset, ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ XorKey[(offset + i) % 8] ^ offset);
        return result;
    }

    public byte[] AesDecrypt(ReadOnlySpan<byte> data) => AesCfb(data, decrypt: true);
    public byte[] AesEncrypt(ReadOnlySpan<byte> data) => AesCfb(data, decrypt: false);

    // .NET only does whole blocks without padding, so pad with zeros and trim:
    // in CFB the output for a byte never depends on later bytes.
    private byte[] AesCfb(ReadOnlySpan<byte> data, bool decrypt)
    {
        if (_aes is null) throw new InvalidOperationException("AES key not set");
        int padded = (data.Length + 15) / 16 * 16;
        var buffer = new byte[padded];
        data.CopyTo(buffer);
        var output = decrypt
            ? _aes.DecryptCfb(buffer, Iv, PaddingMode.None, 128)
            : _aes.EncryptCfb(buffer, Iv, PaddingMode.None, 128);
        return output.AsSpan(0, data.Length).ToArray();
    }

    /// <summary>Decrypts a control (XML) block using the given level.</summary>
    public byte[] Decrypt(BcEncryption level, byte offset, ReadOnlySpan<byte> data) => level switch
    {
        BcEncryption.None => data.ToArray(),
        BcEncryption.Xor => Xor(offset, data),
        _ => AesDecrypt(data),
    };

    public byte[] Encrypt(BcEncryption level, byte offset, ReadOnlySpan<byte> data) => level switch
    {
        BcEncryption.None => data.ToArray(),
        BcEncryption.Xor => Xor(offset, data),
        _ => AesEncrypt(data),
    };

    /// <summary>Upper-case hex MD5 cut to 31 characters, as the login expects.</summary>
    public static string Md5Login(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)))[..31];

    public void Dispose() => _aes?.Dispose();
}
