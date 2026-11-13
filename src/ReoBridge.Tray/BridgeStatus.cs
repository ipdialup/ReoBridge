using System.Text.Json;

namespace ReoBridge.Tray;

public sealed record StreamStatus(string Path, double Fps, int Viewers, bool Live, string? Audio, string? Codec = null, int Width = 0, int Height = 0, double Kbps = 0);

public sealed record CameraStatus(string Name, string Host, string State, string? LastError, int? OnvifPort, IReadOnlyList<StreamStatus> Streams,
    string? Model = null, string? Encryption = null, DateTime? ConnectedSince = null, int Reconnects = 0, bool Ptz = false)
{
    public bool Ok => State == "connected";
}

/// <summary>What the service's /status endpoint reported (or why it could not be reached).</summary>
public sealed record BridgeStatus(bool Reachable, string? Error, string? Version, TimeSpan Uptime, IReadOnlyList<CameraStatus> Cameras, int RtspConnections = 0)
{
    public static BridgeStatus Unreachable(string error) => new(false, error, null, TimeSpan.Zero, []);

    public static BridgeStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static string? S(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double D(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        var cameras = new List<CameraStatus>();
        if (root.TryGetProperty("cameras", out var cams))
        {
            foreach (var c in cams.EnumerateArray())
            {
                var streams = new List<StreamStatus>();
                if (c.TryGetProperty("streams", out var ss))
                {
                    foreach (var s in ss.EnumerateArray())
                    {
                        bool live = s.TryGetProperty("lastFrame", out var lf) && lf.ValueKind == JsonValueKind.String
                                    && lf.GetDateTime().ToUniversalTime() > DateTime.UtcNow.AddSeconds(-5);
                        streams.Add(new StreamStatus(S(s, "path") ?? "", D(s, "fps"), (int)D(s, "viewers"), live, S(s, "audio"),
                            S(s, "codec"), (int)D(s, "width"), (int)D(s, "height"), D(s, "kbps")));
                    }
                }
                int? onvif = c.TryGetProperty("onvifPort", out var op) && op.ValueKind == JsonValueKind.Number ? op.GetInt32() : null;
                DateTime? since = c.TryGetProperty("connectedSince", out var cs) && cs.ValueKind == JsonValueKind.String ? cs.GetDateTime().ToUniversalTime() : null;
                bool ptz = c.TryGetProperty("ptz", out var pz) && pz.ValueKind == JsonValueKind.True;
                cameras.Add(new CameraStatus(S(c, "name") ?? "?", S(c, "host") ?? "", S(c, "state") ?? "unknown", S(c, "lastError"), onvif, streams,
                    S(c, "model"), S(c, "encryption"), since, (int)D(c, "reconnects"), ptz));
            }
        }
        return new BridgeStatus(true, null, S(root, "version"), TimeSpan.FromSeconds(D(root, "uptimeSeconds")), cameras, (int)D(root, "rtspConnections"));
    }

    public Health Health =>
        !Reachable ? Health.Down
        : Cameras.Count == 0 ? Health.Warning
        : Cameras.All(c => c.Ok) ? Health.Ok
        : Cameras.Any(c => c.Ok) ? Health.Warning
        : Health.Down;
}

public enum Health { Unknown, Ok, Warning, Down }
