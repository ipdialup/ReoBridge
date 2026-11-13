using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReoBridge.Onvif;
using ReoBridge.Rtsp;

namespace ReoBridge.Bridge;

/// <summary>Runs the camera workers, the RTSP server and the status page.</summary>
public sealed class BridgeService : BackgroundService
{
    private readonly ReoBridgeOptions _opt;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;

    public BridgeService(IOptions<ReoBridgeOptions> opt, ILoggerFactory logs)
    {
        _opt = opt.Value;
        _logs = logs;
        _log = logs.CreateLogger("ReoBridge");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cameras = ValidCameras().Select(c => new CameraWorker(c, _logs.CreateLogger($"Camera.{c.Name}"))).ToList();
        if (cameras.Count == 0)
            _log.LogWarning("No cameras configured - add them under ReoBridge:Cameras in the config file");

        var paths = cameras.SelectMany(c => c.Paths).ToList();
        var auth = new RtspAuth(_opt.Rtsp.Users.Select(u => new RtspUser(u.Name, u.Password)));
        if (!auth.Enabled) _log.LogWarning("RTSP has no users configured: anyone on the network can watch");
        var bind = IPAddress.TryParse(_opt.Rtsp.Bind, out var ip) ? ip : IPAddress.Any;
        var rtsp = new RtspServer(bind, _opt.Rtsp.Port, paths, auth, _logs.CreateLogger("Rtsp"));

        foreach (var p in paths)
            _log.LogInformation("Publishing rtsp://<this machine>:{Port}/{Path}", _opt.Rtsp.Port, p.Name);

        var tasks = new List<Task> { rtsp.RunAsync(ct) };
        tasks.AddRange(cameras.Select(c => c.RunAsync(ct)));
        foreach (var c in cameras.Where(c => c.Options.OnvifPort > 0))
            tasks.Add(new OnvifServer(c, auth, _opt.Rtsp.Port, _logs.CreateLogger("Onvif")).RunAsync(ct));
        if (_opt.StatusPort > 0)
            tasks.Add(new StatusServer(_opt.StatusPort, cameras, rtsp, _opt.Rtsp.Port, _logs.CreateLogger("Status")).RunAsync(ct));

        // If the RTSP listener cannot start (port in use) there is no point carrying on.
        var first = await Task.WhenAny(tasks);
        if (first.IsFaulted && !ct.IsCancellationRequested)
        {
            _log.LogCritical(first.Exception?.GetBaseException(), "ReoBridge stopped");
            throw first.Exception!.GetBaseException();
        }
        await Task.WhenAll(tasks);
    }

    /// <summary>Skips cameras whose settings would clash with another (duplicate names or ports), with a log line saying why.</summary>
    private IEnumerable<CameraOptions> ValidCameras()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new HashSet<int> { _opt.Rtsp.Port, _opt.StatusPort };
        foreach (var c in _opt.Cameras)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Host))
                _log.LogError("Skipping a camera with no Name or Host");
            else if (c.Name.Contains('/') || c.Name.Contains(' '))
                _log.LogError("Skipping camera {Name}: the name is used in URLs, so no spaces or slashes", c.Name);
            else if (!names.Add(c.Name))
                _log.LogError("Skipping camera {Name}: another camera already has that name", c.Name);
            else if (c.OnvifPort > 0 && !ports.Add(c.OnvifPort))
                _log.LogError("Skipping camera {Name}: OnvifPort {Port} is already used by another camera or service", c.Name, c.OnvifPort);
            else
                yield return c;
        }
    }
}
