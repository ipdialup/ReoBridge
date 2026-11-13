using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ReoBridge.Tray;

/// <summary>
/// Tray icon that polls the ReoBridge status endpoint: green when every camera is streaming, amber when
/// some are not, red when none are or the service cannot be reached. Balloon tips report changes.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ReoBridge Tray";

    private readonly TraySettings _settings;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<Health, Icon> _icons = [];
    private BridgeStatus? _status;
    private bool _polling;
    private StatusForm? _window;
    private int _failures;

    public TrayApp(TraySettings settings, WaitHandle? showSignal = null, bool showNow = false)
    {
        _settings = settings;
        foreach (var h in Enum.GetValues<Health>()) _icons[h] = DrawIcon(h);
        _icon = new NotifyIcon { Icon = _icons[Health.Unknown], Text = "ReoBridge: checking…", Visible = true, ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _menu.Opening += (_, _) => BuildMenu();
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();

        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        if (showSignal is not null)
            ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        if (showNow) ui.Post(_ => ShowWindow(), null);
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            BridgeStatus now;
            try
            {
                var json = await _http.GetStringAsync(new Uri(new Uri(_settings.StatusUrl), "status"));
                now = BridgeStatus.Parse(json);
                _failures = 0;
            }
            catch (Exception ex)
            {
                // One missed poll (service restarting, Wi-Fi blip) is not worth turning red for.
                if (++_failures < 2 && _status is not null) return;
                now = BridgeStatus.Unreachable(ex is TaskCanceledException ? "no answer (timed out)" : ex.GetBaseException().Message);
            }
            Notify(_status, now);
            _status = now;
            _icon.Icon = _icons[now.Health];
            _icon.Text = Tooltip(now);
            if (_window is { IsDisposed: false }) { _window.UpdateStatus(now); _window.Icon = _icons[now.Health]; }
        }
        finally { _polling = false; }
    }

    private static string Tooltip(BridgeStatus s)
    {
        string text = !s.Reachable ? "ReoBridge: service not reachable"
            : s.Cameras.Count == 0 ? "ReoBridge: no cameras configured"
            : $"ReoBridge: {s.Cameras.Count(c => c.Ok)} of {s.Cameras.Count} cameras streaming"
              + string.Concat(s.Cameras.Where(c => !c.Ok).Select(c => $"\n{c.Name}: {c.State}"));
        return text.Length > 127 ? text[..126] + "…" : text; // NotifyIcon.Text limit
    }

    private void Notify(BridgeStatus? before, BridgeStatus now)
    {
        if (before is null || !_settings.Notifications) return;
        if (before.Reachable && !now.Reachable)
        {
            Balloon("ReoBridge is not answering", now.Error ?? "", ToolTipIcon.Error);
            return;
        }
        if (!before.Reachable && now.Reachable)
        {
            Balloon("ReoBridge is back", Tooltip(now)["ReoBridge: ".Length..], ToolTipIcon.Info);
            return;
        }
        foreach (var cam in now.Cameras)
        {
            var old = before.Cameras.FirstOrDefault(c => c.Name == cam.Name);
            if (old is null || old.Ok == cam.Ok) continue;
            if (cam.Ok) Balloon($"{cam.Name} is streaming again", cam.Host, ToolTipIcon.Info);
            else Balloon($"{cam.Name} lost its camera", cam.LastError ?? cam.State, ToolTipIcon.Warning);
        }
    }

    private void Balloon(string title, string text, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, icon);

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var s = _status;
        var server = Uri.TryCreate(_settings.StatusUrl, UriKind.Absolute, out var u) ? u.Authority : _settings.StatusUrl;
        _menu.Items.Add(new ToolStripMenuItem($"ReoBridge on {server}") { Enabled = false, Font = new Font(_menu.Font, FontStyle.Bold) });

        if (s is null) _menu.Items.Add(new ToolStripMenuItem("Checking…") { Enabled = false });
        else if (!s.Reachable) _menu.Items.Add(new ToolStripMenuItem($"Not reachable: {s.Error}") { Enabled = false });
        else
        {
            _menu.Items.Add(new ToolStripMenuItem($"Up {FormatUptime(s.Uptime)} · version {s.Version}") { Enabled = false });
            foreach (var c in s.Cameras)
            {
                var main = c.Streams.FirstOrDefault(x => x.Live);
                string detail = c.Ok
                    ? main is null ? "connected" : $"{main.Fps:0} fps · {c.Streams.Sum(x => x.Viewers)} viewer(s){(main.Audio is null ? "" : " · audio")}"
                    : c.State;
                var item = new ToolStripMenuItem($"{c.Name}: {detail}") { Image = Dot(c.Ok ? Health.Ok : Health.Down) };
                foreach (var st in c.Streams)
                    item.DropDownItems.Add(new ToolStripMenuItem($"rtsp://{HostOnly()}:…/{st.Path} — {(st.Live ? $"{st.Fps:0} fps, {st.Viewers} viewer(s)" : "idle")}") { Enabled = false });
                if (c.OnvifPort is { } op) item.DropDownItems.Add(new ToolStripMenuItem($"ONVIF / PTZ on port {op}") { Enabled = false });
                if (c.LastError is not null) item.DropDownItems.Add(new ToolStripMenuItem($"Last error: {c.LastError}") { Enabled = false });
                _menu.Items.Add(item);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Show status…", null, (_, _) => ShowWindow()) { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add("Open web status page", null, (_, _) => OpenStatusPage());
        if (_settings.IsLocal)
        {
            _menu.Items.Add("Edit cameras…", null, (_, _) => EditCameras());
            _menu.Items.Add("Restart service", null, (_, _) => RestartService());
            if (Directory.Exists(LogsFolder)) _menu.Items.Add("Open logs folder", null, (_, _) => Open(LogsFolder));
        }
        _menu.Items.Add("Change server…", null, (_, _) => ChangeServer());
        _menu.Items.Add(new ToolStripMenuItem("Notifications", null, (_, _) => { _settings.Notifications = !_settings.Notifications; _settings.Save(); }) { Checked = _settings.Notifications });
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = AutostartEnabled });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private static string LogsFolder => Path.Combine(Path.GetDirectoryName(Program.DefaultConfigPath)!, "logs");

    private static void EditCameras() => Program.RunElevated($"--edit-config \"{Program.DefaultConfigPath}\"");

    private async void RestartService()
    {
        if (Program.RunElevated("--restart-service")) { await Task.Delay(3000); await PollAsync(); }
    }

    private void ShowWindow()
    {
        if (_window is null || _window.IsDisposed)
        {
            var server = Uri.TryCreate(_settings.StatusUrl, UriKind.Absolute, out var u) ? u.Authority : _settings.StatusUrl;
            _window = new StatusForm(server, new StatusActions(_settings.IsLocal, EditCameras, RestartService,
                () => Open(LogsFolder), OpenStatusPage, ChangeServer));
            _window.FormClosed += (_, _) => _window = null;
            _window.UpdateStatus(_status);
            _window.Icon = _icons[_status?.Health ?? Health.Unknown];
            _window.Show();
        }
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private string HostOnly() => Uri.TryCreate(_settings.StatusUrl, UriKind.Absolute, out var u) ? u.Host : "host";

    internal static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";

    private void OpenStatusPage() => Open(_settings.StatusUrl);

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { }
    }

    private void ChangeServer()
    {
        using var form = new Form
        {
            Text = "ReoBridge server", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Status address of the machine running ReoBridge\n(e.g. 192.168.1.10 or http://nvr:8555/):", AutoSize = true });
        var box = new TextBox { Text = _settings.StatusUrl, Width = 340 };
        layout.Controls.Add(box);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(box.Text)) return;
        try { _settings.StatusUrl = TraySettings.Normalise(box.Text); }
        catch (UriFormatException) { MessageBox.Show("That is not a valid address.", "ReoBridge"); return; }
        _settings.Save();
        _status = null;
        _window?.Close();
        _ = PollAsync();
    }

    private static bool AutostartEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunValue) is string; }
    }

    private static void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (k.GetValue(RunValue) is string) k.DeleteValue(RunValue);
        else k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }

    // ------------------------------------------------------------ icons

    private static Color ColourOf(Health h) => h switch
    {
        Health.Ok => Color.FromArgb(46, 160, 67),
        Health.Warning => Color.FromArgb(219, 154, 4),
        Health.Down => Color.FromArgb(207, 34, 46),
        _ => Color.FromArgb(128, 128, 128),
    };

    private static Bitmap Dot(Health h)
    {
        var bmp = new Bitmap(12, 12);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(ColourOf(h));
        g.FillEllipse(b, 1, 1, 10, 10);
        return bmp;
    }

    /// <summary>A small camera: coloured body with a white lens, so state reads at a glance.</summary>
    private static Icon DrawIcon(Health h)
    {
        int size = SystemInformation.SmallIconSize.Width * 2;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 32f;
            using var body = new SolidBrush(ColourOf(h));
            using var path = new GraphicsPath();
            var r = new RectangleF(1 * s, 8 * s, 22 * s, 17 * s);
            float rad = 4 * s;
            path.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            path.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            path.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(body, path);
            g.FillPolygon(body, [new PointF(23 * s, 13 * s), new PointF(31 * s, 9 * s), new PointF(31 * s, 24 * s), new PointF(23 * s, 20 * s)]);
            using var lens = new SolidBrush(Color.White);
            g.FillEllipse(lens, 7 * s, 11 * s, 10 * s, 10 * s);
            g.FillEllipse(body, 9.5f * s, 13.5f * s, 5 * s, 5 * s);
        }
        var handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
