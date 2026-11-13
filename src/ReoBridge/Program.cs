using ReoBridge;
using ReoBridge.Baichuan;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReoBridge.Bridge;

if (args.Length > 0 && args[0] == "probe")
    return await Probe.RunAsync(args[1..]);
if (args.Length > 0 && args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine("ReoBridge - serves Reolink cameras as standard RTSP streams.");
    Console.WriteLine();
    Console.WriteLine("  ReoBridge [--config <file.json>]   run the bridge (console or Windows service / systemd)");
    Console.WriteLine("  ReoBridge probe --host <ip> [--user admin] [--sub] [--seconds 20] [--out file]");
    Console.WriteLine("                                     test a camera; password from REOBRIDGE_PASSWORD");
    Console.WriteLine();
    Console.WriteLine("Config is read from appsettings.json next to the program, then the file named by");
    Console.WriteLine("--config or REOBRIDGE_CONFIG (on Windows also %ProgramData%/ReoBridge/reobridge.json).");
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

string? configFile = Array.IndexOf(args, "--config") is int ci and >= 0 && ci + 1 < args.Length
    ? args[ci + 1]
    : Environment.GetEnvironmentVariable("REOBRIDGE_CONFIG");
if (configFile is null && OperatingSystem.IsWindows())
{
    var programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReoBridge", "reobridge.json");
    if (File.Exists(programData)) configFile = programData;
}
if (configFile is not null)
    builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false);

builder.Services.Configure<ReoBridgeOptions>(builder.Configuration.GetSection("ReoBridge"));
builder.Services.AddWindowsService(o => o.ServiceName = "ReoBridge");
builder.Services.AddSystemd();
builder.Services.AddHostedService<BridgeService>();

var opts = builder.Configuration.GetSection("ReoBridge").Get<ReoBridgeOptions>() ?? new ReoBridgeOptions();
string? logDir = opts.LogDirectory;
if (string.IsNullOrWhiteSpace(logDir) && configFile is not null)
    logDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configFile))!, "logs");
if (!string.IsNullOrWhiteSpace(logDir))
    builder.Logging.AddProvider(new FileLoggerProvider(logDir, opts.LogRetainDays));
builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; o.SingleLine = true; });

await builder.Build().RunAsync();
return 0;

namespace ReoBridge
{
    /// <summary>Connects to one camera, pulls a stream for a while and reports what arrived.</summary>
    internal static class Probe
    {
        public static async Task<int> RunAsync(string[] args)
        {
            string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            string host = Arg("--host") ?? throw new ArgumentException("--host is required");
            string user = Arg("--user") ?? "admin";
            string password = Environment.GetEnvironmentVariable("REOBRIDGE_PASSWORD") ?? "";
            bool sub = args.Contains("--sub");
            int seconds = int.Parse(Arg("--seconds") ?? "20");
            string? outFile = Arg("--out");

            using var factory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ").SetMinimumLevel(LogLevel.Debug));
            var log = factory.CreateLogger("probe");

            await using var client = new BcClient(host, 9000, user, password, log);
            await client.ConnectAsync(CancellationToken.None);
            log.LogInformation("Device: {Info}", client.DeviceInfo?.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));

            var stream = await client.StartVideoAsync(sub, CancellationToken.None);
            var parser = new BcMediaParser();
            await using var file = outFile is null ? null : File.Create(outFile);
            var counts = new Dictionary<BcMediaKind, int>();
            long bytes = 0;
            uint? firstMicros = null, lastMicros = null;
            string? codec = null;

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                await foreach (var chunk in stream.Reader.ReadAllAsync(stop.Token))
                {
                    bytes += chunk.Length;
                    foreach (var f in parser.Push(chunk))
                    {
                        counts[f.Kind] = counts.GetValueOrDefault(f.Kind) + 1;
                        if (f.Kind == BcMediaKind.Info)
                            log.LogInformation("Info: {W}x{H} @ {Fps} fps", f.Width, f.Height, f.Fps);
                        if (f.Kind is BcMediaKind.IFrame or BcMediaKind.PFrame)
                        {
                            codec = f.Codec;
                            firstMicros ??= f.Microseconds;
                            lastMicros = f.Microseconds;
                            if (file is not null) await file.WriteAsync(f.Data);
                            if (f.Kind == BcMediaKind.IFrame)
                                log.LogInformation("I-frame {Codec} {Len} bytes, starts {Head}", f.Codec, f.Data.Length,
                                    Convert.ToHexString(f.Data.AsSpan(0, Math.Min(8, f.Data.Length))));
                        }
                        if (f.Kind is BcMediaKind.Aac or BcMediaKind.Adpcm && counts[f.Kind] == 1)
                            log.LogInformation("Audio {Kind} {Len} bytes, starts {Head}", f.Kind, f.Data.Length,
                                Convert.ToHexString(f.Data.AsSpan(0, Math.Min(8, f.Data.Length))));
                    }
                }
            }
            catch (OperationCanceledException) { }

            await client.StopVideoAsync(stream, CancellationToken.None);
            double span = firstMicros is null ? 0 : (lastMicros!.Value - firstMicros.Value) / 1e6;
            int videoFrames = counts.GetValueOrDefault(BcMediaKind.IFrame) + counts.GetValueOrDefault(BcMediaKind.PFrame);
            log.LogInformation("Encryption {Enc}; {Bytes} bytes; codec {Codec}; frames: {Counts}; resync bytes {Resync}; video span {Span:F1}s ({Fps:F1} fps)",
                client.Encryption, bytes, codec, string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")),
                parser.ResyncBytes, span, span > 0 ? videoFrames / span : 0);
            return 0;
        }
    }
}
