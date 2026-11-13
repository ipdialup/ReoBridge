using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReoBridge.Tray;

public enum StreamMode { always, ondemand, off }

/// <summary>One camera as shown in the editor's property grid.</summary>
public sealed class CameraEdit
{
    [Category("Camera"), Description("Short name used in the stream URLs: rtsp://host:8554/<Name>/main. No spaces or slashes.")]
    public string Name { get; set; } = "";

    [Category("Camera"), Description("IP address or host name of the Reolink camera.")]
    public string Host { get; set; } = "";

    [Category("Camera"), Description("Reolink's own protocol port on the camera (normally 9000).")]
    public int Port { get; set; } = 9000;

    [Category("Camera"), Description("Camera login.")]
    public string Username { get; set; } = "admin";

    [Category("Camera"), Description("Camera password."), PasswordPropertyText(true)]
    public string Password { get; set; } = "";

    [Category("Streams"), Description("always = keep pulling it; ondemand = only while someone watches; off = not published.")]
    public StreamMode MainStream { get; set; } = StreamMode.always;

    [Category("Streams"), Description("always = keep pulling it; ondemand = only while someone watches; off = not published.")]
    public StreamMode SubStream { get; set; } = StreamMode.ondemand;

    [Category("ONVIF and PTZ"), Description("Port for this camera's ONVIF service (what your NVR uses for pan/tilt and presets). 0 = off. Each camera needs its own port.")]
    public int OnvifPort { get; set; }

    [Category("ONVIF and PTZ"), Description("Swap left and right, for a camera mounted so pan runs the other way.")]
    public bool InvertPan { get; set; }

    [Category("ONVIF and PTZ"), Description("Swap up and down, e.g. for a camera mounted upside down on a ceiling.")]
    public bool InvertTilt { get; set; }

    [Category("ONVIF and PTZ"), Description("Fastest pan/tilt speed sent to the camera, 1-64. Lower = smaller, finer moves. 0 = use the speed the NVR asks for.")]
    public int PtzSpeed { get; set; }

    [Category("ONVIF and PTZ"), Description("How far one press moves: every move stops by itself after this many milliseconds (e.g. 300). 0 = keep moving until the NVR says stop.")]
    public int PtzStepMs { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "(new camera)" : Name;
}

/// <summary>
/// Edits the Cameras list in reobridge.json. Runs elevated because the file holds passwords and only
/// administrators can read it. Settings it does not know about are kept as they are.
/// </summary>
public sealed class ConfigEditorForm : Form
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string _path;
    private readonly JsonNode _root;
    private readonly List<JsonObject> _originals = [];
    private readonly BindingList<CameraEdit> _cameras = [];
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, ToolbarVisible = false, PropertySort = PropertySort.Categorized };

    public ConfigEditorForm(string path)
    {
        _path = path;
        Text = $"ReoBridge cameras — {path}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 560);
        MinimumSize = new Size(600, 400);
        Font = SystemFonts.MessageBoxFont ?? Font;

        try { _root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new JsonObject(); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _root = new JsonObject();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot read {path}:\n\n{ex.Message}", "ReoBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _root = new JsonObject();
            Load += (_, _) => Close();
        }

        if (_root["ReoBridge"]?["Cameras"] is JsonArray arr)
        {
            foreach (var node in arr.OfType<JsonObject>())
            {
                _originals.Add(node);
                _cameras.Add(FromJson(node));
            }
        }
        BuildUi();
    }

    private void BuildUi()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 200, FixedPanel = FixedPanel.Panel1 };
        _list.DataSource = _cameras;
        _list.SelectedIndexChanged += (_, _) => _grid.SelectedObject = _list.SelectedItem;
        _grid.PropertyValueChanged += (_, _) => { _cameras.ResetBindings(); };

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var add = new Button { Text = "Add", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        add.Click += (_, _) =>
        {
            int nextOnvif = _cameras.Count == 0 ? 8999 : Math.Max(8999, _cameras.Max(c => c.OnvifPort) + 1);
            _cameras.Add(new CameraEdit { Name = $"Camera{_cameras.Count + 1}", OnvifPort = nextOnvif });
            _list.SelectedIndex = _cameras.Count - 1;
        };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedItem is not CameraEdit c) return;
            if (MessageBox.Show($"Remove {c}?", "ReoBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _cameras.Remove(c);
        };
        listButtons.Controls.AddRange([add, remove]);
        split.Panel1.Controls.Add(_list);
        split.Panel1.Controls.Add(listButtons);
        split.Panel2.Controls.Add(_grid);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        var save = new Button { Text = "Save and restart", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveAndRestart();
        bottom.Controls.AddRange([cancel, save]);
        CancelButton = cancel;

        Controls.Add(split);
        Controls.Add(bottom);
        if (_cameras.Count > 0) _list.SelectedIndex = 0;
    }

    private static CameraEdit FromJson(JsonObject o)
    {
        string S(string k, string d) => o[k]?.GetValueKind() == JsonValueKind.String ? o[k]!.GetValue<string>() : d;
        int I(string k, int d) => o[k]?.GetValueKind() == JsonValueKind.Number ? o[k]!.GetValue<int>() : d;
        bool B(string k) => o[k]?.GetValueKind() == JsonValueKind.True;
        StreamMode M(string k, StreamMode d) => Enum.TryParse<StreamMode>(S(k, d.ToString()), true, out var m) ? m : d;
        return new CameraEdit
        {
            Name = S("Name", ""), Host = S("Host", ""), Port = I("Port", 9000),
            Username = S("Username", "admin"), Password = S("Password", ""),
            MainStream = M("MainStream", StreamMode.always), SubStream = M("SubStream", StreamMode.ondemand),
            OnvifPort = I("OnvifPort", 0), InvertPan = B("InvertPan"), InvertTilt = B("InvertTilt"),
            PtzSpeed = I("PtzSpeed", 0), PtzStepMs = I("PtzStepMs", 0),
        };
    }

    private string? CheckCameras()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new HashSet<int>();
        foreach (var c in _cameras)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Contains(' ') || c.Name.Contains('/'))
                return $"\"{c.Name}\": the name is needed and cannot contain spaces or slashes.";
            if (!names.Add(c.Name)) return $"Two cameras are called {c.Name}.";
            if (string.IsNullOrWhiteSpace(c.Host)) return $"{c.Name} needs a host (the camera's IP address).";
            if (c.Port is < 1 or > 65535) return $"{c.Name}: camera port must be 1-65535.";
            if (c.OnvifPort is < 0 or > 65535) return $"{c.Name}: ONVIF port must be 0 (off) or 1-65535.";
            if (c.PtzSpeed is < 0 or > 64) return $"{c.Name}: PTZ speed must be 0 (NVR decides) or 1-64.";
            if (c.PtzStepMs is < 0 or > 10000) return $"{c.Name}: PTZ step must be 0 (off) or up to 10000 ms.";
            if (c.OnvifPort > 0 && !ports.Add(c.OnvifPort)) return $"ONVIF port {c.OnvifPort} is used by two cameras; each needs its own.";
        }
        return null;
    }

    private void SaveAndRestart()
    {
        if (CheckCameras() is { } problem)
        {
            MessageBox.Show(problem, "ReoBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var bridge = _root["ReoBridge"] as JsonObject;
        if (bridge is null) _root["ReoBridge"] = bridge = new JsonObject();
        var arr = new JsonArray();
        foreach (var c in _cameras)
        {
            // Start from the original entry (keeps settings this editor does not show), matched by name.
            var o = _originals.FirstOrDefault(x => string.Equals(x["Name"]?.ToString(), c.Name, StringComparison.OrdinalIgnoreCase))?.DeepClone() as JsonObject ?? new JsonObject();
            o["Name"] = c.Name;
            o["Host"] = c.Host;
            o["Port"] = c.Port;
            o["Username"] = c.Username;
            o["Password"] = c.Password;
            o["MainStream"] = c.MainStream.ToString();
            o["SubStream"] = c.SubStream.ToString();
            o["OnvifPort"] = c.OnvifPort;
            o["InvertPan"] = c.InvertPan;
            o["InvertTilt"] = c.InvertTilt;
            o["PtzSpeed"] = c.PtzSpeed;
            o["PtzStepMs"] = c.PtzStepMs;
            arr.Add(o);
        }
        bridge["Cameras"] = arr;

        try
        {
            if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
            // Writing into the existing file keeps its permissions (administrators and SYSTEM only).
            File.WriteAllText(_path, _root.ToJsonString(Indented));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save {_path}:\n\n{ex.Message}", "ReoBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Cursor = Cursors.WaitCursor;
        bool restarted = Program.RestartService(showResult: true);
        Cursor = Cursors.Default;
        if (restarted)
            MessageBox.Show("Saved. ReoBridge restarted with the new cameras.", "ReoBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}
