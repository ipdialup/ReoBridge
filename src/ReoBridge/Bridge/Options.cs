namespace ReoBridge.Bridge;

public sealed class ReoBridgeOptions
{
    public RtspOptions Rtsp { get; set; } = new();
    /// <summary>Port for the status page and /status JSON. 0 turns it off.</summary>
    public int StatusPort { get; set; } = 8555;
    /// <summary>Folder for daily log files. Empty = no file logging.</summary>
    public string? LogDirectory { get; set; }
    public int LogRetainDays { get; set; } = 14;
    public List<CameraOptions> Cameras { get; set; } = [];
}

public sealed class RtspOptions
{
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8554;
    /// <summary>RTSP logins. Leave empty to allow anyone on the network.</summary>
    public List<UserOptions> Users { get; set; } = [];
}

public sealed class UserOptions
{
    public string Name { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class CameraOptions
{
    /// <summary>Used in the RTSP path: rtsp://host:8554/{Name}/main.</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9000;
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
    /// <summary>always | ondemand | off</summary>
    public string MainStream { get; set; } = "always";
    /// <summary>always | ondemand | off</summary>
    public string SubStream { get; set; } = "ondemand";
}
