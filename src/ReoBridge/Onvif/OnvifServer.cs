using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ReoBridge.Bridge;
using ReoBridge.Rtsp;

namespace ReoBridge.Onvif;

/// <summary>
/// A small ONVIF endpoint for one camera: just enough of the device, media and PTZ services for NVRs
/// (Blue Iris, Frigate, Home Assistant...) to find the RTSP streams and drive pan/tilt and presets.
/// Every path (/onvif/device_service, /onvif/media_service, /onvif/ptz_service) is served by the same
/// dispatcher; the SOAP body says which operation is wanted.
/// </summary>
public sealed class OnvifServer
{
    private const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
    private const string NsDevice = "http://www.onvif.org/ver10/device/wsdl";
    private const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
    private const string NsPtz = "http://www.onvif.org/ver20/ptz/wsdl";
    private const string NsSchema = "http://www.onvif.org/ver10/schema";
    private const string VelocitySpace = "http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace";

    /// <summary>Operations ONVIF allows without credentials (needed before a client can authenticate).</summary>
    private static readonly HashSet<string> Anonymous = ["GetSystemDateAndTime", "GetCapabilities", "GetServices", "GetServiceCapabilities", "GetWsdlUrl"];

    private readonly CameraWorker _camera;
    private readonly RtspAuth _auth;
    private readonly int _port;
    private readonly int _rtspPort;
    private readonly ILogger _log;
    private CancellationTokenSource? _autoStop;

    public OnvifServer(CameraWorker camera, RtspAuth auth, int rtspPort, ILogger log)
    {
        _camera = camera;
        _auth = auth;
        _port = camera.Options.OnvifPort;
        _rtspPort = rtspPort;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        try { listener.Start(); }
        catch (SocketException ex)
        {
            _log.LogWarning("ONVIF for {Camera} disabled: cannot listen on port {Port} ({Error})", _camera.Name, _port, ex.Message);
            return;
        }
        _log.LogInformation("ONVIF for {Camera} on http://*:{Port}/onvif/device_service", _camera.Name, _port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleConnectionAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                // HTTP/1.1 keep-alive: serve requests until the client closes or goes quiet.
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    var req = await HttpRequest.ReadAsync(stream, idle.Token);
                    if (req is null) return;

                    var host = req.Host ?? (client.Client.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "localhost";
                    var (code, body) = req.Method == "POST"
                        ? await HandleSoapAsync(req.Body, host, ct)
                        : (405, "");
                    LogOnce((client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?", req.Method, req.Path, req.Body, code);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    string reason = code switch { 200 => "OK", 400 => "Bad Request", 405 => "Method Not Allowed", _ => "Internal Server Error" };
                    var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/soap+xml; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nServer: ReoBridge\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
                    await stream.WriteAsync(bytes, ct);
                    if (req.Close) return;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
            catch (Exception ex) { _log.LogDebug(ex, "ONVIF connection failed"); }
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seen = new();

    /// <summary>Logs each distinct client / operation / result once, so what an NVR asks for is visible without debug logging.</summary>
    private void LogOnce(string client, string method, string path, string body, int code)
    {
        string op = $"{method} {path}";
        if (method == "POST")
        {
            try
            {
                var el = XDocument.Parse(body).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault();
                if (el is not null) op = el.Name.LocalName;
            }
            catch (XmlException) { }
        }
        if (_seen.Count > 500 || !_seen.TryAdd($"{client}|{op}|{code}", 0)) return;
        _log.LogInformation("ONVIF {Client} {Op} -> {Code}", client, op, code);
    }

    /// <summary>Parses one SOAP request and returns the HTTP status and response envelope.</summary>
    internal async Task<(int Code, string Body)> HandleSoapAsync(string xml, string host, CancellationToken ct)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException) { return (400, Fault("env:Sender", "ter:WellFormed", "Request is not valid XML")); }

        var body = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault();
        if (body is null) return (400, Fault("env:Sender", "ter:WellFormed", "No SOAP body"));
        string op = body.Name.LocalName;

        if (!Anonymous.Contains(op) && !Authorised(doc))
        {
            _log.LogDebug("ONVIF {Op} refused: bad or missing credentials", op);
            return (400, Fault("env:Sender", "ter:NotAuthorized", "Sender not authorized"));
        }

        try
        {
            string? result = op switch
            {
                // Device service
                "GetSystemDateAndTime" => SystemDateAndTime(),
                "GetCapabilities" => Capabilities(host),
                "GetServices" => Services(host),
                "GetServiceCapabilities" => ServiceCapabilities(body.Name.NamespaceName),
                "GetDeviceInformation" => DeviceInformation(),
                "GetScopes" => Scopes(),
                "GetHostname" => $"<tds:GetHostnameResponse><tds:HostnameInformation><tt:FromDHCP>false</tt:FromDHCP><tt:Name>{X(_camera.Name)}</tt:Name></tds:HostnameInformation></tds:GetHostnameResponse>",
                "GetNetworkInterfaces" => "<tds:GetNetworkInterfacesResponse/>",
                "GetDNS" => "<tds:GetDNSResponse><tds:DNSInformation><tt:FromDHCP>true</tt:FromDHCP></tds:DNSInformation></tds:GetDNSResponse>",
                "GetNTP" => "<tds:GetNTPResponse><tds:NTPInformation><tt:FromDHCP>true</tt:FromDHCP></tds:NTPInformation></tds:GetNTPResponse>",
                "GetDiscoveryMode" => "<tds:GetDiscoveryModeResponse><tds:DiscoveryMode>NonDiscoverable</tds:DiscoveryMode></tds:GetDiscoveryModeResponse>",
                "GetUsers" => "<tds:GetUsersResponse/>",
                "GetRelayOutputs" => "<tds:GetRelayOutputsResponse/>",

                // Media service
                "GetProfiles" => $"<trt:GetProfilesResponse>{string.Concat(Profiles().Select(p => ProfileXml(p, "trt:Profiles")))}</trt:GetProfilesResponse>",
                "GetProfile" => FindProfile(body) is { } p ? $"<trt:GetProfileResponse>{ProfileXml(p, "trt:Profile")}</trt:GetProfileResponse>" : null,
                "GetStreamUri" => StreamUri(body, host),
                "GetVideoSources" => VideoSources(),
                "GetVideoSourceConfigurations" => $"<trt:GetVideoSourceConfigurationsResponse>{VideoSourceConfig("trt:Configurations")}</trt:GetVideoSourceConfigurationsResponse>",
                "GetVideoEncoderConfigurations" => $"<trt:GetVideoEncoderConfigurationsResponse>{string.Concat(Profiles().Select(p => EncoderConfig(p, "trt:Configurations")))}</trt:GetVideoEncoderConfigurationsResponse>",
                "GetAudioSources" => HasAudio ? "<trt:GetAudioSourcesResponse><trt:AudioSources token=\"AudioSource\"><tt:Channels>1</tt:Channels></trt:AudioSources></trt:GetAudioSourcesResponse>" : "<trt:GetAudioSourcesResponse/>",
                "GetAudioSourceConfigurations" => $"<trt:GetAudioSourceConfigurationsResponse>{(HasAudio ? AudioSourceConfig("trt:Configurations") : "")}</trt:GetAudioSourceConfigurationsResponse>",
                "GetAudioEncoderConfigurations" => $"<trt:GetAudioEncoderConfigurationsResponse>{(Profiles().Select(p => p.Path.Audio).FirstOrDefault(a => a is not null) is { } ae ? AudioEncoderConfig(ae, "trt:Configurations") : "")}</trt:GetAudioEncoderConfigurationsResponse>",
                "GetSnapshotUri" => null,

                // PTZ service
                "GetNodes" => $"<tptz:GetNodesResponse>{PtzNode("tptz:PTZNode")}</tptz:GetNodesResponse>",
                "GetNode" => $"<tptz:GetNodeResponse>{PtzNode("tptz:PTZNode")}</tptz:GetNodeResponse>",
                "GetConfigurations" => $"<tptz:GetConfigurationsResponse>{PtzConfig("tptz:PTZConfiguration")}</tptz:GetConfigurationsResponse>",
                "GetConfiguration" => $"<tptz:GetConfigurationResponse>{PtzConfig("tptz:PTZConfiguration")}</tptz:GetConfigurationResponse>",
                "GetConfigurationOptions" => PtzConfigOptions(),
                "GetStatus" => $"<tptz:GetStatusResponse><tptz:PTZStatus><tt:UtcTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</tt:UtcTime></tptz:PTZStatus></tptz:GetStatusResponse>",
                "ContinuousMove" => await ContinuousMoveAsync(body, ct),
                "RelativeMove" => await RelativeMoveAsync(body, ct),
                "Stop" => await StopAsync(body, ct),
                "GetPresets" => await PresetsAsync(ct),
                "GotoPreset" => await GotoPresetAsync(body, ct),
                "SetPreset" => await SetPresetAsync(body, ct),
                _ => null,
            };
            if (result is null)
            {
                _log.LogDebug("ONVIF {Op} not supported", op);
                return (500, Fault("env:Receiver", "ter:ActionNotSupported", $"{op} is not supported"));
            }
            return (200, Envelope(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning("ONVIF {Op} failed: {Error}", op, ex.Message);
            return (500, Fault("env:Receiver", "ter:Action", ex.Message));
        }
    }

    // ---------------------------------------------------------------- auth

    private bool Authorised(XDocument doc)
    {
        if (!_auth.Enabled) return true;
        var token = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "UsernameToken");
        if (token is null) return false;
        string? Child(string name) => token.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var pw = token.Elements().FirstOrDefault(e => e.Name.LocalName == "Password");
        bool digest = pw?.Attribute("Type")?.Value.EndsWith("PasswordDigest", StringComparison.Ordinal) ?? false;
        return _auth.CheckWsse(Child("Username") ?? "", pw?.Value.Trim() ?? "", digest, Child("Nonce"), Child("Created"));
    }

    // ---------------------------------------------------------------- device

    private static string SystemDateAndTime()
    {
        var u = DateTime.UtcNow;
        return "<tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime><tt:DateTimeType>NTP</tt:DateTimeType><tt:DaylightSavings>false</tt:DaylightSavings>" +
               "<tt:TimeZone><tt:TZ>UTC0</tt:TZ></tt:TimeZone>" +
               $"<tt:UTCDateTime><tt:Time><tt:Hour>{u.Hour}</tt:Hour><tt:Minute>{u.Minute}</tt:Minute><tt:Second>{u.Second}</tt:Second></tt:Time>" +
               $"<tt:Date><tt:Year>{u.Year}</tt:Year><tt:Month>{u.Month}</tt:Month><tt:Day>{u.Day}</tt:Day></tt:Date></tt:UTCDateTime>" +
               "</tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>";
    }

    private string Addr(string host, string service) => $"http://{host}:{_port}/onvif/{service}";

    private string Capabilities(string host)
    {
        var sb = new StringBuilder("<tds:GetCapabilitiesResponse><tds:Capabilities>");
        sb.Append($"<tt:Device><tt:XAddr>{Addr(host, "device_service")}</tt:XAddr>");
        sb.Append("<tt:Network><tt:IPFilter>false</tt:IPFilter><tt:ZeroConfiguration>false</tt:ZeroConfiguration><tt:IPVersion6>false</tt:IPVersion6><tt:DynDNS>false</tt:DynDNS></tt:Network>");
        sb.Append("<tt:System><tt:DiscoveryResolve>false</tt:DiscoveryResolve><tt:DiscoveryBye>false</tt:DiscoveryBye><tt:RemoteDiscovery>false</tt:RemoteDiscovery><tt:SystemBackup>false</tt:SystemBackup><tt:SystemLogging>false</tt:SystemLogging><tt:FirmwareUpgrade>false</tt:FirmwareUpgrade>");
        sb.Append("<tt:SupportedVersions><tt:Major>2</tt:Major><tt:Minor>0</tt:Minor></tt:SupportedVersions></tt:System>");
        sb.Append("<tt:Security><tt:TLS1.1>false</tt:TLS1.1><tt:TLS1.2>false</tt:TLS1.2><tt:OnboardKeyGeneration>false</tt:OnboardKeyGeneration><tt:AccessPolicyConfig>false</tt:AccessPolicyConfig><tt:X.509Token>false</tt:X.509Token><tt:SAMLToken>false</tt:SAMLToken><tt:KerberosToken>false</tt:KerberosToken><tt:RELToken>false</tt:RELToken></tt:Security></tt:Device>");
        sb.Append($"<tt:Media><tt:XAddr>{Addr(host, "media_service")}</tt:XAddr><tt:StreamingCapabilities><tt:RTPMulticast>false</tt:RTPMulticast><tt:RTP_TCP>true</tt:RTP_TCP><tt:RTP_RTSP_TCP>true</tt:RTP_RTSP_TCP></tt:StreamingCapabilities></tt:Media>");
        if (_camera.PtzSupported) sb.Append($"<tt:PTZ><tt:XAddr>{Addr(host, "ptz_service")}</tt:XAddr></tt:PTZ>");
        sb.Append("</tds:Capabilities></tds:GetCapabilitiesResponse>");
        return sb.ToString();
    }

    private string Services(string host)
    {
        static string Svc(string ns, string addr, int major, int minor) =>
            $"<tds:Service><tds:Namespace>{ns}</tds:Namespace><tds:XAddr>{addr}</tds:XAddr><tds:Version><tt:Major>{major}</tt:Major><tt:Minor>{minor}</tt:Minor></tds:Version></tds:Service>";
        var sb = new StringBuilder("<tds:GetServicesResponse>");
        sb.Append(Svc(NsDevice, Addr(host, "device_service"), 2, 0));
        sb.Append(Svc(NsMedia, Addr(host, "media_service"), 2, 0));
        if (_camera.PtzSupported) sb.Append(Svc(NsPtz, Addr(host, "ptz_service"), 2, 0));
        sb.Append("</tds:GetServicesResponse>");
        return sb.ToString();
    }

    private string ServiceCapabilities(string ns) => ns switch
    {
        NsMedia => "<trt:GetServiceCapabilitiesResponse><trt:Capabilities SnapshotUri=\"false\" Rotation=\"false\"><trt:ProfileCapabilities MaximumNumberOfProfiles=\"2\"/><trt:StreamingCapabilities RTPMulticast=\"false\" RTP_TCP=\"true\" RTP_RTSP_TCP=\"true\"/></trt:Capabilities></trt:GetServiceCapabilitiesResponse>",
        NsPtz => "<tptz:GetServiceCapabilitiesResponse><tptz:Capabilities EFlip=\"false\" Reverse=\"false\" GetCompatibleConfigurations=\"false\"/></tptz:GetServiceCapabilitiesResponse>",
        _ => "<tds:GetServiceCapabilitiesResponse><tds:Capabilities><tds:Network/><tds:Security UsernameToken=\"true\"/><tds:System/></tds:Capabilities></tds:GetServiceCapabilitiesResponse>",
    };

    private string DeviceInformation() =>
        "<tds:GetDeviceInformationResponse><tds:Manufacturer>Reolink</tds:Manufacturer>" +
        $"<tds:Model>{X(_camera.Model ?? "Reolink camera")}</tds:Model><tds:FirmwareVersion>{X(_camera.Firmware ?? "unknown")}</tds:FirmwareVersion>" +
        $"<tds:SerialNumber>{X(_camera.Serial ?? _camera.Name)}</tds:SerialNumber><tds:HardwareId>ReoBridge</tds:HardwareId></tds:GetDeviceInformationResponse>";

    private string Scopes()
    {
        static string Scope(string s) => $"<tds:Scopes><tt:ScopeDef>Fixed</tt:ScopeDef><tt:ScopeItem>{X(s)}</tt:ScopeItem></tds:Scopes>";
        return "<tds:GetScopesResponse>" + Scope("onvif://www.onvif.org/type/video_encoder") + Scope("onvif://www.onvif.org/Profile/Streaming") +
               (_camera.PtzSupported ? Scope("onvif://www.onvif.org/type/ptz") : "") +
               Scope("onvif://www.onvif.org/name/" + Uri.EscapeDataString(_camera.Name)) + "</tds:GetScopesResponse>";
    }

    // ---------------------------------------------------------------- media

    private sealed record Profile(string Token, Media.MediaPath Path);

    private List<Profile> Profiles() =>
        _camera.Paths.Select(p => new Profile(p.IsSub ? "SubProfile" : "MainProfile", p)).ToList();

    private Profile? FindProfile(XElement body)
    {
        var token = body.Elements().FirstOrDefault(e => e.Name.LocalName == "ProfileToken")?.Value.Trim();
        var all = Profiles();
        return all.FirstOrDefault(p => p.Token == token) ?? (string.IsNullOrEmpty(token) ? all.FirstOrDefault() : null);
    }

    private static (int W, int H) Size(Media.MediaPath p) =>
        p.Width > 0 ? (p.Width, p.Height) : p.IsSub ? (640, 360) : (1920, 1080);

    private string VideoSourceConfig(string element)
    {
        var main = Profiles().FirstOrDefault(p => !p.Path.IsSub) ?? Profiles().FirstOrDefault();
        var (w, h) = main is null ? (1920, 1080) : Size(main.Path);
        return $"<{element} token=\"VideoSourceConfig\"><tt:Name>VideoSourceConfig</tt:Name><tt:UseCount>{Math.Max(1, Profiles().Count)}</tt:UseCount>" +
               $"<tt:SourceToken>VideoSource</tt:SourceToken><tt:Bounds x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\"/></{element}>";
    }

    private static string EncoderConfig(Profile p, string element)
    {
        var (w, h) = Size(p.Path);
        bool h265 = p.Path.Codec?.ToString().Contains("265") ?? false;
        int fps = p.Path.MeasuredFps > 1 ? (int)Math.Round(p.Path.MeasuredFps) : 15;
        int kbps = p.Path.MeasuredKbps > 1 ? (int)Math.Round(p.Path.MeasuredKbps) : (p.Path.IsSub ? 256 : 2048);
        return $"<{element} token=\"{(p.Path.IsSub ? "SubEncoder" : "MainEncoder")}\"><tt:Name>{p.Path.Stream}</tt:Name><tt:UseCount>1</tt:UseCount>" +
               $"<tt:Encoding>{(h265 ? "H265" : "H264")}</tt:Encoding><tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution>" +
               $"<tt:Quality>5</tt:Quality><tt:RateControl><tt:FrameRateLimit>{fps}</tt:FrameRateLimit><tt:EncodingInterval>1</tt:EncodingInterval><tt:BitrateLimit>{kbps}</tt:BitrateLimit></tt:RateControl>" +
               (h265 ? "" : $"<tt:H264><tt:GovLength>{fps * 2}</tt:GovLength><tt:H264Profile>High</tt:H264Profile></tt:H264>") +
               "<tt:Multicast><tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address><tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart></tt:Multicast>" +
               $"<tt:SessionTimeout>PT60S</tt:SessionTimeout></{element}>";
    }

    private string ProfileXml(Profile p, string element)
    {
        var sb = new StringBuilder($"<{element} token=\"{p.Token}\" fixed=\"true\"><tt:Name>{X(_camera.Name)} {p.Path.Stream}</tt:Name>");
        // ONVIF schema order: video source, audio source, video encoder, audio encoder, ..., PTZ.
        var audio = p.Path.Audio;
        var parts = new StringBuilder();
        parts.Append(VideoSourceConfig("tt:VideoSourceConfiguration"));
        if (audio is not null) parts.Append(AudioSourceConfig("tt:AudioSourceConfiguration"));
        parts.Append(EncoderConfig(p, "tt:VideoEncoderConfiguration"));
        if (audio is not null) parts.Append(AudioEncoderConfig(audio, "tt:AudioEncoderConfiguration"));
        sb.Append(parts);
        if (_camera.PtzSupported) sb.Append(PtzConfig("tt:PTZConfiguration"));
        sb.Append($"</{element}>");
        return sb.ToString();
    }

    private bool HasAudio => _camera.Paths.Any(p => p.Audio is not null);

    private static string AudioSourceConfig(string element) =>
        $"<{element} token=\"AudioSourceConfig\"><tt:Name>AudioSourceConfig</tt:Name><tt:UseCount>2</tt:UseCount><tt:SourceToken>AudioSource</tt:SourceToken></{element}>";

    private static string AudioEncoderConfig(Media.AacInfo a, string element) =>
        $"<{element} token=\"AudioEncoder\"><tt:Name>Audio</tt:Name><tt:UseCount>1</tt:UseCount><tt:Encoding>AAC</tt:Encoding>" +
        $"<tt:Bitrate>32</tt:Bitrate><tt:SampleRate>{a.SampleRate / 1000}</tt:SampleRate>" +
        "<tt:Multicast><tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address><tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart></tt:Multicast>" +
        $"<tt:SessionTimeout>PT60S</tt:SessionTimeout></{element}>";

    private string? StreamUri(XElement body, string host)
    {
        var p = FindProfile(body);
        if (p is null) return null;
        string uri = $"rtsp://{host}:{_rtspPort}/{p.Path.Name}";
        return $"<trt:GetStreamUriResponse><trt:MediaUri><tt:Uri>{X(uri)}</tt:Uri><tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>" +
               "<tt:InvalidAfterReboot>false</tt:InvalidAfterReboot><tt:Timeout>PT0S</tt:Timeout></trt:MediaUri></trt:GetStreamUriResponse>";
    }

    private string VideoSources()
    {
        var main = Profiles().FirstOrDefault(p => !p.Path.IsSub);
        var (w, h) = main is null ? (1920, 1080) : Size(main.Path);
        int fps = main?.Path.MeasuredFps > 1 ? (int)Math.Round(main.Path.MeasuredFps) : 15;
        return $"<trt:GetVideoSourcesResponse><trt:VideoSources token=\"VideoSource\"><tt:Framerate>{fps}</tt:Framerate>" +
               $"<tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution></trt:VideoSources></trt:GetVideoSourcesResponse>";
    }

    // ---------------------------------------------------------------- PTZ

    private static string Range(string name) => $"<tt:{name}><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:{name}>";

    private static string PtzNode(string element) =>
        $"<{element} token=\"PTZNode\" FixedHomePosition=\"false\"><tt:Name>PTZ</tt:Name><tt:SupportedPTZSpaces>" +
        $"<tt:ContinuousPanTiltVelocitySpace><tt:URI>{VelocitySpace}</tt:URI>{Range("XRange")}{Range("YRange")}</tt:ContinuousPanTiltVelocitySpace>" +
        "<tt:RelativePanTiltTranslationSpace><tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/TranslationGenericSpace</tt:URI>" + Range("XRange") + Range("YRange") + "</tt:RelativePanTiltTranslationSpace>" +
        $"</tt:SupportedPTZSpaces><tt:MaximumNumberOfPresets>64</tt:MaximumNumberOfPresets><tt:HomeSupported>false</tt:HomeSupported></{element}>";

    private static string PtzConfig(string element) =>
        $"<{element} token=\"PTZConfig\"><tt:Name>PTZ</tt:Name><tt:UseCount>2</tt:UseCount><tt:NodeToken>PTZNode</tt:NodeToken>" +
        $"<tt:DefaultContinuousPanTiltVelocitySpace>{VelocitySpace}</tt:DefaultContinuousPanTiltVelocitySpace>" +
        "<tt:DefaultPTZSpeed><tt:PanTilt x=\"0.5\" y=\"0.5\" space=\"http://www.onvif.org/ver10/tptz/PanTiltSpaces/GenericSpeedSpace\"/></tt:DefaultPTZSpeed>" +
        $"<tt:DefaultPTZTimeout>PT5S</tt:DefaultPTZTimeout></{element}>";

    private static string PtzConfigOptions() =>
        "<tptz:GetConfigurationOptionsResponse><tptz:PTZConfigurationOptions><tt:Spaces>" +
        $"<tt:ContinuousPanTiltVelocitySpace><tt:URI>{VelocitySpace}</tt:URI>{Range("XRange")}{Range("YRange")}</tt:ContinuousPanTiltVelocitySpace>" +
        "</tt:Spaces><tt:PTZTimeout><tt:Min>PT1S</tt:Min><tt:Max>PT60S</tt:Max></tt:PTZTimeout></tptz:PTZConfigurationOptions></tptz:GetConfigurationOptionsResponse>";

    /// <summary>Maps an ONVIF pan/tilt velocity (x right, y up, each -1..1) to a Reolink move command and speed (1-64).</summary>
    public static (string Command, int Speed) ToReolinkMove(double x, double y)
    {
        const double dead = 0.05;
        bool l = x < -dead, r = x > dead, u = y > dead, d = y < -dead;
        string command = (l, r, u, d) switch
        {
            (true, _, true, _) => "leftUp",
            (true, _, _, true) => "leftDown",
            (_, true, true, _) => "rightUp",
            (_, true, _, true) => "rightDown",
            (true, _, _, _) => "left",
            (_, true, _, _) => "right",
            (_, _, true, _) => "up",
            (_, _, _, true) => "down",
            _ => "stop",
        };
        int speed = (int)Math.Round(Math.Clamp(Math.Max(Math.Abs(x), Math.Abs(y)), 0, 1) * 64);
        return (command, Math.Clamp(speed, 1, 64));
    }

    private static (double X, double Y)? PanTilt(XElement body, string container)
    {
        var pt = body.Elements().FirstOrDefault(e => e.Name.LocalName == container)?
                     .Elements().FirstOrDefault(e => e.Name.LocalName == "PanTilt");
        if (pt is null) return null;
        static double D(XAttribute? a) => double.TryParse(a?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (D(pt.Attribute("x")), D(pt.Attribute("y")));
    }

    /// <summary>Applies the camera's InvertPan / InvertTilt settings (for cameras mounted sideways or upside down).</summary>
    private (double X, double Y) Oriented((double X, double Y) v) =>
        (_camera.Options.InvertPan ? -v.X : v.X, _camera.Options.InvertTilt ? -v.Y : v.Y);

    private async Task<string> ContinuousMoveAsync(XElement body, CancellationToken ct)
    {
        var v = Oriented(PanTilt(body, "Velocity") ?? (0, 0));
        var (command, speed) = ToReolinkMove(v.X, v.Y);
        speed = CapSpeed(speed);
        CancelAutoStop();
        await _camera.PtzAsync(command, speed, ct);
        _moveStarted = DateTime.UtcNow;

        // Honour the request's Timeout (ONVIF says the move stops by itself after it), and the camera's step length.
        var timeoutText = body.Elements().FirstOrDefault(e => e.Name.LocalName == "Timeout")?.Value;
        TimeSpan timeout = TimeSpan.Zero;
        if (timeoutText is not null)
        {
            try { timeout = XmlConvert.ToTimeSpan(timeoutText.Trim()); } catch (FormatException) { }
        }
        var step = TimeSpan.FromMilliseconds(Math.Max(0, _camera.Options.PtzStepMs));
        if (step > TimeSpan.Zero && (timeout <= TimeSpan.Zero || step < timeout)) timeout = step;
        if (command != "stop" && timeout > TimeSpan.Zero) ScheduleStop(timeout);
        _log.LogInformation("PTZ {Camera} {Command} speed {Speed} (x {X:0.##}, y {Y:0.##}{Stop})", _camera.Name, command, speed, v.X, v.Y,
            timeout > TimeSpan.Zero ? $", stops after {timeout.TotalMilliseconds:0} ms" : timeoutText is null ? ", until told to stop" : "");
        return "<tptz:ContinuousMoveResponse/>";
    }

    /// <summary>Reolink has no positional moves: a relative move becomes a short timed move in that direction.</summary>
    private async Task<string> RelativeMoveAsync(XElement body, CancellationToken ct)
    {
        var t = Oriented(PanTilt(body, "Translation") ?? (0, 0));
        var (command, _) = ToReolinkMove(t.X, t.Y);
        if (command != "stop")
        {
            CancelAutoStop();
            int speed = CapSpeed(32);
            await _camera.PtzAsync(command, speed, ct);
            _moveStarted = DateTime.UtcNow;
            var ms = _camera.Options.PtzStepMs > 0 ? _camera.Options.PtzStepMs : 150 + 1500 * Math.Min(1, Math.Max(Math.Abs(t.X), Math.Abs(t.Y)));
            ScheduleStop(TimeSpan.FromMilliseconds(ms));
            _log.LogInformation("PTZ {Camera} {Command} speed {Speed} (relative {X:0.##}, {Y:0.##}; {Ms:0} ms)", _camera.Name, command, speed, t.X, t.Y, ms);
        }
        return "<tptz:RelativeMoveResponse/>";
    }

    private async Task<string> StopAsync(XElement body, CancellationToken ct)
    {
        CancelAutoStop();
        bool panTilt = !string.Equals(body.Elements().FirstOrDefault(e => e.Name.LocalName == "PanTilt")?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        if (panTilt)
        {
            await _camera.PtzAsync("stop", 1, ct);
            if (_moveStarted is { } at)
                _log.LogInformation("PTZ {Camera} stop after {Ms:0} ms", _camera.Name, (DateTime.UtcNow - at).TotalMilliseconds);
            _moveStarted = null;
        }
        return "<tptz:StopResponse/>";
    }

    private DateTime? _moveStarted;

    private int CapSpeed(int speed) => _camera.Options.PtzSpeed is > 0 and var cap ? Math.Min(speed, Math.Min(64, cap)) : speed;

    private void CancelAutoStop() => Interlocked.Exchange(ref _autoStop, null)?.Cancel();

    private void ScheduleStop(TimeSpan after)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _autoStop, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(after, cts.Token);
                if (Interlocked.CompareExchange(ref _autoStop, null, cts) == cts)
                {
                    await _camera.PtzAsync("stop", 1, CancellationToken.None);
                    _log.LogInformation("PTZ {Camera} stopped by itself after {Ms:0} ms", _camera.Name, after.TotalMilliseconds);
                    _moveStarted = null;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogDebug("PTZ auto-stop failed: {Error}", ex.Message); }
        });
    }

    private async Task<string> PresetsAsync(CancellationToken ct)
    {
        var presets = await _camera.GetPresetsAsync(ct);
        var sb = new StringBuilder("<tptz:GetPresetsResponse>");
        foreach (var (id, name) in presets)
            sb.Append($"<tptz:Preset token=\"{id}\"><tt:Name>{X(string.IsNullOrEmpty(name) ? $"Preset {id}" : name)}</tt:Name></tptz:Preset>");
        sb.Append("</tptz:GetPresetsResponse>");
        return sb.ToString();
    }

    private static int PresetToken(XElement body) =>
        int.TryParse(body.Elements().FirstOrDefault(e => e.Name.LocalName == "PresetToken")?.Value.Trim(), out var id) ? id : -1;

    private async Task<string> GotoPresetAsync(XElement body, CancellationToken ct)
    {
        int id = PresetToken(body);
        if (id < 0) throw new ArgumentException("Unknown preset token");
        CancelAutoStop();
        await _camera.PresetAsync(id, false, null, ct);
        return "<tptz:GotoPresetResponse/>";
    }

    private async Task<string> SetPresetAsync(XElement body, CancellationToken ct)
    {
        int id = PresetToken(body);
        string? name = body.Elements().FirstOrDefault(e => e.Name.LocalName == "PresetName")?.Value.Trim();
        if (id < 0)
        {
            var used = (await _camera.GetPresetsAsync(ct)).Select(p => p.Id).ToHashSet();
            id = Enumerable.Range(1, 64).FirstOrDefault(i => !used.Contains(i), -1);
            if (id < 0) throw new InvalidOperationException("All preset slots are in use");
        }
        await _camera.PresetAsync(id, true, string.IsNullOrEmpty(name) ? $"Preset {id}" : name, ct);
        return $"<tptz:SetPresetResponse><tptz:PresetToken>{id}</tptz:PresetToken></tptz:SetPresetResponse>";
    }

    // ---------------------------------------------------------------- SOAP

    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    private static string Envelope(string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<env:Envelope xmlns:env=\"{NsSoap}\" xmlns:tds=\"{NsDevice}\" xmlns:trt=\"{NsMedia}\" xmlns:tptz=\"{NsPtz}\" xmlns:tt=\"{NsSchema}\" xmlns:ter=\"http://www.onvif.org/ver10/error\">" +
        $"<env:Body>{body}</env:Body></env:Envelope>";

    private static string Fault(string code, string subcode, string reason) => Envelope(
        $"<env:Fault><env:Code><env:Value>{code}</env:Value><env:Subcode><env:Value>{subcode}</env:Value></env:Subcode></env:Code>" +
        $"<env:Reason><env:Text xml:lang=\"en\">{X(reason)}</env:Text></env:Reason></env:Fault>");

    /// <summary>Minimal HTTP/1.1 request reader (headers + Content-Length body).</summary>
    private sealed class HttpRequest
    {
        public string Method = "";
        public string Path = "";
        public string? Host;
        public bool Close;
        public string Body = "";

        public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken ct)
        {
            var buf = new MemoryStream();
            var one = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int r = await stream.ReadAsync(one, ct);
                if (r == 0) return null;
                buf.Write(one, 0, r);
                headerEnd = buf.GetBuffer().AsSpan(0, (int)buf.Length).IndexOf("\r\n\r\n"u8);
                if (buf.Length > 64 * 1024) return null;
            }
            var all = buf.ToArray();
            var lines = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
            var first = lines[0].Split(' ');
            var req = new HttpRequest { Method = first[0].ToUpperInvariant(), Path = first.Length > 1 ? first[1] : "" };
            int length = 0;
            foreach (var line in lines.Skip(1))
            {
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string k = line[..c].Trim(), v = line[(c + 1)..].Trim();
                if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(v, out length);
                else if (k.Equals("Host", StringComparison.OrdinalIgnoreCase)) req.Host = StripPort(v);
                else if (k.Equals("Connection", StringComparison.OrdinalIgnoreCase)) req.Close = v.Equals("close", StringComparison.OrdinalIgnoreCase);
            }
            if (length > 1024 * 1024) return null;
            var body = new byte[length];
            int have = Math.Min(length, all.Length - headerEnd - 4);
            Array.Copy(all, headerEnd + 4, body, 0, have);
            while (have < length)
            {
                int r = await stream.ReadAsync(body.AsMemory(have), ct);
                if (r == 0) return null;
                have += r;
            }
            req.Body = Encoding.UTF8.GetString(body);
            return req;
        }

        private static string StripPort(string host)
        {
            if (host.StartsWith('[')) { int e = host.IndexOf(']'); return e > 0 ? host[..(e + 1)] : host; }
            int c = host.LastIndexOf(':');
            return c > 0 ? host[..c] : host;
        }
    }
}
