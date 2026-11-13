using System.Text.Json;

namespace ReoBridge.Tray;

/// <summary>Per-user tray settings, kept in %APPDATA%\ReoBridge\tray.json.</summary>
public sealed class TraySettings
{
    public string StatusUrl { get; set; } = "http://localhost:8555/";
    public bool Notifications { get; set; } = true;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReoBridge", "tray.json");

    public static TraySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }

    /// <summary>Accepts "server", "server:8555" or a full URL and returns the status page base URL.</summary>
    public static string Normalise(string input)
    {
        input = input.Trim();
        if (!input.Contains("://")) input = "http://" + input;
        var uri = new UriBuilder(input);
        if (uri.Uri.IsDefaultPort && !input.Contains(":" + uri.Port)) uri.Port = 8555;
        uri.Path = "/";
        return uri.Uri.ToString();
    }

    /// <summary>True when the status URL points at this machine, so the local service can be managed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLocal
    {
        get
        {
            if (!Uri.TryCreate(StatusUrl, UriKind.Absolute, out var u)) return false;
            if (u.IsLoopback || u.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var mine = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName());
                return System.Net.IPAddress.TryParse(u.Host, out var ip) && mine.Contains(ip);
            }
            catch (Exception) { return false; }
        }
    }
}
