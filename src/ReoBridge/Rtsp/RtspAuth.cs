using System.Security.Cryptography;
using System.Text;

namespace ReoBridge.Rtsp;

public sealed record RtspUser(string Name, string Password);

/// <summary>RTSP Digest (RFC 2617, MD5) and Basic authentication. No users configured = open access.</summary>
public sealed class RtspAuth
{
    public const string Realm = "ReoBridge";
    private readonly Dictionary<string, string> _users;

    public RtspAuth(IEnumerable<RtspUser> users) =>
        _users = users.Where(u => !string.IsNullOrEmpty(u.Name))
                      .ToDictionary(u => u.Name, u => u.Password ?? "", StringComparer.Ordinal);

    public bool Enabled => _users.Count > 0;

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public RtspResponse Challenge(string nonce) => new RtspResponse(401, "Unauthorized")
        .With("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{nonce}\", algorithm=MD5")
        .With("WWW-Authenticate", $"Basic realm=\"{Realm}\"");

    public bool Check(RtspRequest req, string nonce)
    {
        if (!Enabled) return true;
        var header = req.Header("Authorization");
        if (header is null) return false;

        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
            catch (FormatException) { return false; }
            int colon = decoded.IndexOf(':');
            return colon > 0 && _users.TryGetValue(decoded[..colon], out var pw) && FixedEquals(pw, decoded[(colon + 1)..]);
        }

        if (header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            var p = ParseParams(header[7..]);
            if (!p.TryGetValue("username", out var user) || !_users.TryGetValue(user, out var password)) return false;
            if (p.GetValueOrDefault("nonce") != nonce) return false;
            string uri = p.GetValueOrDefault("uri") ?? req.Uri;
            string ha1 = Md5($"{user}:{Realm}:{password}");
            string ha2 = Md5($"{req.Method}:{uri}");
            string expected = p.TryGetValue("qop", out var qop)
                ? Md5($"{ha1}:{nonce}:{p.GetValueOrDefault("nc")}:{p.GetValueOrDefault("cnonce")}:{qop}:{ha2}")
                : Md5($"{ha1}:{nonce}:{ha2}");
            return FixedEquals(expected, (p.GetValueOrDefault("response") ?? "").ToLowerInvariant());
        }
        return false;
    }

    /// <summary>ONVIF WS-Security UsernameToken: PasswordDigest = Base64(SHA1(nonce + created + password)) or plain text.</summary>
    public bool CheckWsse(string user, string password, bool digest, string? nonceB64, string? created)
    {
        if (!Enabled) return true;
        if (!_users.TryGetValue(user, out var real)) return false;
        if (!digest) return FixedEquals(real, password);
        byte[] nonce;
        try { nonce = Convert.FromBase64String(nonceB64 ?? ""); } catch (FormatException) { return false; }
        var input = nonce.Concat(Encoding.UTF8.GetBytes((created ?? "") + real)).ToArray();
        return FixedEquals(Convert.ToBase64String(SHA1.HashData(input)), password);
    }

    internal static Dictionary<string, string> ParseParams(string s)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
            int eq = s.IndexOf('=', i);
            if (eq < 0) break;
            string key = s[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < s.Length && s[i] == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                value = s[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                int end = s.IndexOf(',', i);
                if (end < 0) end = s.Length;
                value = s[i..end].Trim();
                i = end;
            }
            result[key] = value;
        }
        return result;
    }

    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
