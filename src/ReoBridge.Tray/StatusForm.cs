using System.Drawing.Drawing2D;

namespace ReoBridge.Tray;

/// <summary>Things the status window can ask the tray to do.</summary>
public sealed record StatusActions(bool Local, Action EditCameras, Action RestartService, Action OpenLogs, Action OpenWebPage, Action ChangeServer);

/// <summary>Native status window: one card per camera with its streams, refreshed from the tray's polling.</summary>
public sealed class StatusForm : Form
{
    private static readonly Color Ok = Color.FromArgb(26, 127, 55);
    private static readonly Color Warn = Color.FromArgb(176, 120, 0);
    private static readonly Color Bad = Color.FromArgb(198, 40, 40);
    private static readonly Color Muted = Color.FromArgb(96, 104, 112);
    private static readonly Color CardBack = Color.White;
    private static readonly Color PageBack = Color.FromArgb(243, 245, 247);

    private readonly string _server;
    private readonly Label _headline = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0) };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 2, 0, 0) };
    private readonly Dot _headDot = new() { Size = new Size(18, 18), Margin = new Padding(0, 9, 10, 0) };
    private readonly FlowLayoutPanel _cards = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        Padding = new Padding(16, 4, 16, 16), BackColor = PageBack,
    };
    private readonly Dictionary<string, CameraCard> _cardByName = [];

    public StatusForm(string server, StatusActions actions)
    {
        _server = server;
        Text = "ReoBridge";
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(560, 360);
        BackColor = PageBack;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception) { }

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16, 14, 16, 10), BackColor = PageBack };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        titles.Controls.Add(_headline);
        titles.Controls.Add(_summary);
        header.Controls.Add(_headDot, 0, 0);
        header.Controls.Add(titles, 1, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 8, 12, 10), BackColor = PageBack };
        Button Btn(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.System, Margin = new Padding(4) };
            b.Click += (_, _) => a();
            buttons.Controls.Add(b);
            return b;
        }
        if (actions.Local)
        {
            Btn("Edit cameras…", actions.EditCameras);
            Btn("Restart service", actions.RestartService);
            Btn("Logs", actions.OpenLogs);
        }
        Btn("Web page", actions.OpenWebPage);
        Btn("Change server…", actions.ChangeServer);
        var close = Btn("Close", Close);
        CancelButton = close;

        Controls.Add(_cards);
        Controls.Add(header);
        Controls.Add(buttons);
        _cards.SizeChanged += (_, _) => FitCards();
        UpdateStatus(null);
    }

    private void FitCards()
    {
        int w = _cards.ClientSize.Width - _cards.Padding.Horizontal - 2;
        foreach (Control c in _cards.Controls) c.Width = Math.Max(300, w);
    }

    public void UpdateStatus(BridgeStatus? s)
    {
        if (s is null)
        {
            SetHead(Health.Unknown, "Checking…", _server);
            return;
        }
        if (!s.Reachable)
        {
            SetHead(Health.Down, "ReoBridge is not answering", $"{_server} · {s.Error}");
        }
        else
        {
            int ok = s.Cameras.Count(c => c.Ok);
            string head = s.Cameras.Count == 0 ? "No cameras configured"
                : ok == s.Cameras.Count ? (s.Cameras.Count == 1 ? "Camera is streaming" : $"All {s.Cameras.Count} cameras streaming")
                : $"{ok} of {s.Cameras.Count} cameras streaming";
            SetHead(s.Health, head, $"{_server} · up {TrayApp.FormatUptime(s.Uptime)} · {s.RtspConnections} RTSP connection(s) · version {s.Version}");
        }

        SuspendLayout();
        var names = s.Cameras.Select(c => c.Name).ToHashSet();
        foreach (var gone in _cardByName.Keys.Where(n => !names.Contains(n)).ToList())
        {
            _cards.Controls.Remove(_cardByName[gone]);
            _cardByName[gone].Dispose();
            _cardByName.Remove(gone);
        }
        foreach (var cam in s.Cameras)
        {
            if (!_cardByName.TryGetValue(cam.Name, out var card))
            {
                card = new CameraCard(_server);
                _cardByName[cam.Name] = card;
                _cards.Controls.Add(card);
            }
            card.Show(cam, s.Reachable);
        }
        FitCards();
        ResumeLayout();
    }

    private void SetHead(Health h, string head, string summary)
    {
        _headDot.Colour = ColourOf(h);
        _headline.Text = head;
        _summary.Text = summary;
    }

    internal static Color ColourOf(Health h) => h switch { Health.Ok => Ok, Health.Warning => Warn, Health.Down => Bad, _ => Muted };

    /// <summary>A filled circle.</summary>
    private sealed class Dot : Control
    {
        private Color _colour = Muted;
        public Dot() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); BackColor = Color.Transparent; }
        public Color Colour { get => _colour; set { if (_colour != value) { _colour = value; Invalidate(); } } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_colour);
            int d = Math.Min(Width, Height) - 2;
            e.Graphics.FillEllipse(b, 1, (Height - d) / 2, d, d);
        }
    }

    /// <summary>One camera: name and state, details line, error line and a stream table.</summary>
    private sealed class CameraCard : Panel
    {
        private readonly string _server;
        private readonly Dot _dot = new() { Size = new Size(14, 14), Margin = new Padding(0, 6, 8, 0) };
        private readonly Label _name = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), Margin = new Padding(0) };
        private readonly Label _state = new() { AutoSize = true, Font = new Font("Segoe UI", 10f), Margin = new Padding(8, 3, 0, 0) };
        private readonly Label _details = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 0, 0) };
        private readonly Label _error = new() { AutoSize = true, ForeColor = Bad, Margin = new Padding(0, 4, 0, 0), Visible = false };
        private readonly ListView _streams = new()
        {
            View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None,
            Margin = new Padding(0, 8, 0, 0), Height = 70, Scrollable = false,
        };

        public CameraCard(string server)
        {
            _server = server;
            BackColor = CardBack;
            Padding = new Padding(16, 12, 16, 12);
            Margin = new Padding(0, 0, 0, 12);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowOnly;
            _streams.Columns.Add("Stream URL", 300);
            _streams.Columns.Add("Video", 120);
            _streams.Columns.Add("fps", 50, HorizontalAlignment.Right);
            _streams.Columns.Add("kbit/s", 65, HorizontalAlignment.Right);
            _streams.Columns.Add("Audio", 85);
            _streams.Columns.Add("Viewers", 60, HorizontalAlignment.Right);
            var copy = new ContextMenuStrip();
            copy.Items.Add("Copy URL", null, (_, _) => { if (_streams.SelectedItems.Count > 0) Clipboard.SetText(_streams.SelectedItems[0].Text); });
            _streams.ContextMenuStrip = copy;

            var title = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            title.Controls.AddRange([_dot, _name, _state]);
            var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            stack.Controls.AddRange([title, _details, _error, _streams]);
            Controls.Add(stack);
            SizeChanged += (_, _) => _streams.Width = Math.Max(200, ClientSize.Width - Padding.Horizontal);
        }

        public void Show(CameraStatus c, bool reachable)
        {
            var colour = !reachable ? Muted : c.Ok ? Ok : Bad;
            _dot.Colour = colour;
            _name.Text = c.Name;
            _state.Text = c.State;
            _state.ForeColor = colour;

            var bits = new List<string> { c.Host };
            if (c.Model is not null) bits.Add(c.Model);
            if (c.ConnectedSince is { } since) bits.Add($"connected {TrayApp.FormatUptime(DateTime.UtcNow - since)}");
            bits.Add($"{c.Reconnects} reconnect{(c.Reconnects == 1 ? "" : "s")}");
            if (c.OnvifPort is { } op) bits.Add($"ONVIF :{op}{(c.Ptz ? " with pan/tilt" : "")}");
            _details.Text = string.Join("  ·  ", bits);
            _details.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal), 0);

            _error.Visible = !c.Ok && c.LastError is not null;
            _error.Text = c.LastError ?? "";

            string host = Uri.TryCreate(_server.Contains("://") ? _server : "http://" + _server, UriKind.Absolute, out var u) ? u.Host : _server;
            _streams.BeginUpdate();
            while (_streams.Items.Count > c.Streams.Count) _streams.Items.RemoveAt(_streams.Items.Count - 1);
            for (int i = 0; i < c.Streams.Count; i++)
            {
                var st = c.Streams[i];
                string[] cells =
                [
                    $"rtsp://{host}:8554/{st.Path}",
                    st.Width > 0 ? $"{st.Codec} {st.Width}×{st.Height}" : st.Codec ?? "",
                    st.Live ? st.Fps.ToString("0.0") : "idle",
                    st.Live ? st.Kbps.ToString("0") : "",
                    st.Audio is null ? "" : st.Audio.Replace(" Hz x1", " Hz").Replace("16000 Hz", "16 kHz").Replace("8000 Hz", "8 kHz"),
                    st.Viewers.ToString(),
                ];
                ListViewItem item;
                if (i < _streams.Items.Count) item = _streams.Items[i];
                else { item = new ListViewItem(cells[0]); for (int k = 1; k < cells.Length; k++) item.SubItems.Add(""); _streams.Items.Add(item); }
                for (int k = 0; k < cells.Length; k++) item.SubItems[k].Text = cells[k];
                item.ForeColor = st.Live ? SystemColors.WindowText : Muted;
            }
            _streams.EndUpdate();
            _streams.Height = 26 + 22 * Math.Max(1, c.Streams.Count);
        }
    }
}
