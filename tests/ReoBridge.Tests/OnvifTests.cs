using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ReoBridge.Bridge;
using ReoBridge.Onvif;
using ReoBridge.Rtsp;

namespace ReoBridge.Tests;

public class OnvifTests
{
    private static OnvifServer Server(bool withUsers = true)
    {
        var cam = new CameraWorker(new CameraOptions { Name = "Indoor", Host = "192.0.2.1", OnvifPort = 8999 }, NullLogger.Instance);
        var auth = new RtspAuth(withUsers ? [new RtspUser("bi", "secret")] : []);
        return new OnvifServer(cam, auth, 8554, NullLogger.Instance);
    }

    private static string Soap(string body, string header = "") =>
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"" +
        " xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"" +
        $" xmlns:tptz=\"http://www.onvif.org/ver20/ptz/wsdl\"><s:Header>{header}</s:Header><s:Body>{body}</s:Body></s:Envelope>";

    private static string Wsse(string user, string password)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var digest = Convert.ToBase64String(SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes(created + password)).ToArray()));
        return "<wsse:Security xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" " +
               "xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\"><wsse:UsernameToken>" +
               $"<wsse:Username>{user}</wsse:Username><wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">{digest}</wsse:Password>" +
               $"<wsse:Nonce>{Convert.ToBase64String(nonce)}</wsse:Nonce><wsu:Created>{created}</wsu:Created></wsse:UsernameToken></wsse:Security>";
    }

    [Theory]
    [InlineData(-1, 0, "left", 64)]
    [InlineData(0.5, 0, "right", 32)]
    [InlineData(0, 1, "up", 64)]
    [InlineData(0, -0.25, "down", 16)]
    [InlineData(-0.5, 0.5, "leftUp", 32)]
    [InlineData(0.5, -0.5, "rightDown", 32)]
    [InlineData(0, 0, "stop", 1)]
    [InlineData(0.01, -0.01, "stop", 1)]
    public void Velocity_MapsToReolinkCommand(double x, double y, string command, int speed)
    {
        var (c, s) = OnvifServer.ToReolinkMove(x, y);
        Assert.Equal(command, c);
        Assert.Equal(speed, s);
    }

    [Fact]
    public async Task Capabilities_AreAnonymous_AndPointAtTheRequestHost()
    {
        var (code, body) = await Server().HandleSoapAsync(Soap("<tds:GetCapabilities/>"), "10.1.2.3", default);
        Assert.Equal(200, code);
        Assert.Contains("http://10.1.2.3:8999/onvif/ptz_service", body);
        Assert.Contains("http://10.1.2.3:8999/onvif/media_service", body);
    }

    [Fact]
    public async Task Profiles_NeedCredentials()
    {
        var (code, body) = await Server().HandleSoapAsync(Soap("<trt:GetProfiles/>"), "h", default);
        Assert.Equal(400, code);
        Assert.Contains("NotAuthorized", body);

        (code, _) = await Server().HandleSoapAsync(Soap("<trt:GetProfiles/>", Wsse("bi", "wrong")), "h", default);
        Assert.Equal(400, code);
    }

    [Fact]
    public async Task Profiles_And_StreamUri_WithDigestLogin()
    {
        var server = Server();
        var (code, body) = await server.HandleSoapAsync(Soap("<trt:GetProfiles/>", Wsse("bi", "secret")), "h", default);
        Assert.Equal(200, code);
        Assert.Contains("token=\"MainProfile\"", body);
        Assert.Contains("token=\"SubProfile\"", body);
        Assert.Contains("PTZConfiguration", body);

        (code, body) = await server.HandleSoapAsync(
            Soap("<trt:GetStreamUri><trt:StreamSetup/><trt:ProfileToken>SubProfile</trt:ProfileToken></trt:GetStreamUri>", Wsse("bi", "secret")), "nvr.lan", default);
        Assert.Equal(200, code);
        Assert.Contains("rtsp://nvr.lan:8554/Indoor/sub", body);
    }

    [Fact]
    public async Task NoUsers_MeansOpenAccess()
    {
        var (code, _) = await Server(withUsers: false).HandleSoapAsync(Soap("<trt:GetProfiles/>"), "h", default);
        Assert.Equal(200, code);
    }

    [Fact]
    public async Task Ptz_WhileCameraOffline_IsASoapFault()
    {
        var (code, body) = await Server(withUsers: false).HandleSoapAsync(
            Soap("<tptz:ContinuousMove><tptz:ProfileToken>MainProfile</tptz:ProfileToken><tptz:Velocity><PanTilt x=\"1\" y=\"0\"/></tptz:Velocity></tptz:ContinuousMove>"), "h", default);
        Assert.Equal(500, code);
        Assert.Contains("not connected", body);
    }

    [Fact]
    public async Task InvertedCamera_StillFaultsCleanlyWhenOffline()
    {
        // Orientation flags only change the direction sent; with no camera the move must still fail cleanly.
        var cam = new CameraWorker(new CameraOptions { Name = "C", Host = "192.0.2.1", OnvifPort = 1, InvertPan = true, InvertTilt = true }, NullLogger.Instance);
        var (code, _) = await new OnvifServer(cam, new RtspAuth([]), 8554, NullLogger.Instance).HandleSoapAsync(
            Soap("<tptz:ContinuousMove><tptz:Velocity><PanTilt x=\"1\" y=\"1\"/></tptz:Velocity></tptz:ContinuousMove>"), "h", default);
        Assert.Equal(500, code);
    }

    [Fact]
    public async Task UnknownOperation_IsActionNotSupported()
    {
        var (code, body) = await Server(withUsers: false).HandleSoapAsync(Soap("<tds:SystemReboot/>"), "h", default);
        Assert.Equal(500, code);
        Assert.Contains("ActionNotSupported", body);
    }
}
