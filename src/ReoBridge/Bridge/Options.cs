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
    /// <summary>ONVIF (device, media and PTZ) port for this camera; 0 = no ONVIF. Each camera needs its own port.</summary>
    public int OnvifPort { get; set; }
    /// <summary>Swap left and right for PTZ (camera mounted so pan runs the other way).</summary>
    public bool InvertPan { get; set; }
    /// <summary>Swap up and down for PTZ (e.g. a camera mounted upside down on a ceiling).</summary>
    public bool InvertTilt { get; set; }

    /// <summary>Top pan/tilt speed sent to the camera (1-64). 0 = use whatever speed the NVR asks for.</summary>
    public int PtzSpeed { get; set; }

    /// <summary>
    /// How long one press moves the camera, in milliseconds: every move stops by itself after this, so a click
    /// on the NVR's arrow nudges the camera a fixed step. 0 = move until the NVR says stop.
    /// </summary>
    public int PtzStepMs { get; set; }
}
