using System.Net.NetworkInformation;

namespace PortSight;

public sealed class MainForm : Form
{
    // Original PortSight charcoal/steel artwork palette.
    static readonly Color Bg = Color.FromArgb(12, 13, 15), Surface = Color.FromArgb(20, 22, 26), Surface2 = Color.FromArgb(33, 36, 42), Fg = Color.FromArgb(232, 234, 238), Muted = Color.FromArgb(139, 144, 154), Accent = Color.FromArgb(154, 173, 192);
    readonly ComboBox _nics = new(); readonly NumericUpDown _seconds = new(); readonly Label _status = new();
    readonly TextBox _listenOutput = Output(), _danteOutput = Output(), _oscOutput = Output(), _loadOutput = Output();
    readonly TextBox _range = new(); readonly DataGridView _hosts = new(); readonly Label _scanStatus = new(); readonly NumericUpDown _oscPort = new();
    readonly Label _monitorStats = new(); readonly UtilHistory _history = new(); readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 1000 };
    readonly List<NetworkInterface> _adapters = []; CancellationTokenSource? _oscCts, _scanCts; bool _monitorBusy;

    public MainForm()
    {
        Text = "PortSight 2 — switch port & network view"; Icon = TryLoadIcon(); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimumSize = new Size(960, 680); Size = new Size(1080, 760); AutoScaleMode = AutoScaleMode.Dpi; BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 10f);
        var logo = new PictureBox { Image = TryLoadLogo(), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(22, 12), Size = new Size(52, 52), BackColor = Color.Transparent };
        var title = new Label { Text = "PortSight", Font = new Font("Segoe UI Semibold", 21f), ForeColor = Fg, AutoSize = true, Location = new Point(82, 16) };
        var subtitle = new Label { Text = "Laptop RJ45 + standard Cat5/6  ·  switch discovery, live hosts and network health", ForeColor = Muted, AutoSize = true, Location = new Point(84, 51) };
        _status.Text = "Ready — started elevated once for LLDP/CDP capture. This tool never needs special probe cables."; _status.ForeColor = Accent; _status.AutoSize = true; _status.Location = new Point(25, 78);
        _nics.DropDownStyle = ComboBoxStyle.DropDownList; _nics.BackColor = Surface; _nics.ForeColor = Fg; _nics.FlatStyle = FlatStyle.Flat; _nics.Width = 610; _nics.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; _nics.Location = new Point(22, 106); _nics.SelectedIndexChanged += (_, _) => SetDefaultRange(); LoadNics();
        var nicLabel = new Label { Text = "NETWORK ADAPTER", ForeColor = Muted, AutoSize = true, Location = new Point(23, 91), Font = new Font("Segoe UI", 8f) };
        var tabs = new TabControl { Location = new Point(20, 142), Size = new Size(ClientSize.Width - 40, ClientSize.Height - 162), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, DrawMode = TabDrawMode.OwnerDrawFixed, ItemSize = new Size(125, 32), SizeMode = TabSizeMode.Fixed };
        tabs.DrawItem += DrawTab; tabs.TabPages.Add(BuildListen()); tabs.TabPages.Add(BuildHosts()); tabs.TabPages.Add(BuildDante()); tabs.TabPages.Add(BuildOsc()); tabs.TabPages.Add(BuildLoad()); tabs.TabPages.Add(BuildMonitor()); Controls.AddRange([logo, title, subtitle, _status, nicLabel, _nics, tabs]); _monitorTimer.Tick += async (_, _) => await MonitorTickAsync();
    }

    TabPage Page(string title) => new(title) { BackColor = Bg, ForeColor = Fg, Padding = new Padding(18), AutoScroll = true };
    TabPage BuildListen()
    {
        var p = Page("Listen"); var hint = Hint("Listen for LLDP/CDP advertisements on the selected Cat5/6-connected NIC. A managed switch must advertise discovery data.", 18, 16); var secondsLabel = Hint("LISTEN DURATION (SECONDS)", 18, 51, 8f);
        _seconds.Minimum = 5; _seconds.Maximum = 120; _seconds.Value = 35; _seconds.Location = new Point(18, 69); _seconds.Width = 90; _seconds.BackColor = Surface; _seconds.ForeColor = Fg;
        var sample = Button("Built-in Cisco sample", new Point(124, 67), new Size(155, 32), true); var listen = Button("Listen live", new Point(288, 67), new Size(125, 32)); sample.Click += (_, _) => LoadSample(); listen.Click += async (_, _) => await ListenAsync(listen);
        _listenOutput.Location = new Point(18, 116); _listenOutput.Text = "Choose Built-in Cisco sample to test the decoder offline.\r\n\r\nLive capture uses tshark when installed, otherwise Windows pktmon. The one startup UAC prompt allows both paths.";
        p.Controls.AddRange([hint, secondsLabel, _seconds, sample, listen, _listenOutput]); FillPage(p, _listenOutput, 116); return p;
    }
    TabPage BuildHosts()
    {
        var p = Page("Who's up?"); p.Controls.Add(Hint("Subnet-only discovery: ICMP first, then TCP 80/443/22/445/3389/8080. Only responding hosts are listed.", 18, 16)); _range.Location = new Point(18, 48); _range.Width = 230; _range.BackColor = Surface; _range.ForeColor = Fg; _range.Text = SelectedNic() is { } nic ? HostScanner.DefaultRange(nic) : "192.168.1.0/24";
        var scan = Button("Scan live hosts", new Point(260, 46), new Size(140, 32)); scan.Click += async (_, _) => await ScanAsync(scan); _scanStatus.Location = new Point(416, 53); _scanStatus.ForeColor = Accent; _scanStatus.AutoSize = true;
        _hosts.Location = new Point(18, 93); StyleGrid(_hosts); _hosts.Columns.Add("ip", "IP"); _hosts.Columns.Add("rtt", "RTT (ms)"); _hosts.Columns.Add("method", "Method"); _hosts.Columns.Add("hostname", "Hostname"); _hosts.Columns[0].Width = 150; _hosts.Columns[1].Width = 110; _hosts.Columns[2].Width = 100; _hosts.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        p.Controls.AddRange([_range, scan, _scanStatus, _hosts]); FillPage(p, _hosts, 93); return p;
    }
    TabPage BuildDante()
    {
        var p = Page("Dante"); p.Controls.Add(Hint("Browse _netaudio-arc._udp using mDNS 224.0.0.251:5353. If none answer, an offline FOH sample rack is shown.", 18, 16)); var browse = Button("Browse Dante", new Point(18, 48), new Size(130, 32)); browse.Click += async (_, _) => await DanteAsync(browse);
        _danteOutput.Location = new Point(18, 94); p.Controls.AddRange([browse, _danteOutput]); FillPage(p, _danteOutput, 94); return p;
    }
    TabPage BuildOsc()
    {
        var p = Page("OSC"); p.Controls.Add(Hint("UDP listener needs no administrator rights. Starts with safe loopback messages for /gyrosc/gyro and /voices/3/osc/14/freq.", 18, 16)); p.Controls.Add(Hint("UDP PORT", 18, 50, 8f)); _oscPort.Minimum = 1; _oscPort.Maximum = 65535; _oscPort.Value = 8000; _oscPort.Location = new Point(18, 68); _oscPort.Width = 100; _oscPort.BackColor = Surface; _oscPort.ForeColor = Fg;
        var osc = Button("Start listener", new Point(130, 66), new Size(125, 32)); osc.Click += async (_, _) => await OscAsync(osc); _oscOutput.Location = new Point(18, 115); p.Controls.AddRange([_oscPort, osc, _oscOutput]); FillPage(p, _oscOutput, 115); return p;
    }
    TabPage BuildLoad()
    {
        var p = Page("Load"); p.Controls.Add(Hint("Take a one-shot three-second reading from the laptop's selected NIC.", 18, 16)); var load = Button("Sample 3 seconds", new Point(18, 48), new Size(145, 32)); load.Click += async (_, _) => await LoadAsync(load);
        _loadOutput.Location = new Point(18, 95); _loadOutput.Text = "Includes utilisation, Mbps in/out, packets, multicast, errors and discards.\r\n\r\nCaveat: this is the laptop NIC on this access port, not the switch uplink."; p.Controls.AddRange([load, _loadOutput]); FillPage(p, _loadOutput, 95); return p;
    }
    TabPage BuildMonitor()
    {
        var p = Page("Monitor"); p.Controls.Add(Hint("Continuous one-second access-port NIC view — this is not switch uplink traffic.", 18, 16)); var startStop = Button("Start monitor", new Point(18, 48), new Size(140, 32)); startStop.Click += (_, _) => { _monitorTimer.Enabled = !_monitorTimer.Enabled; startStop.Text = _monitorTimer.Enabled ? "Stop monitor" : "Start monitor"; _status.Text = _monitorTimer.Enabled ? "Monitoring selected laptop NIC every second." : "Monitor stopped."; };
        _monitorStats.Location = new Point(18, 99); _monitorStats.AutoSize = true; _monitorStats.ForeColor = Fg; _monitorStats.Font = new Font("Consolas", 10f); _monitorStats.Text = "Start the monitor to collect live NIC values."; _history.Location = new Point(18, 210); p.Controls.AddRange([startStop, _monitorStats, _history, Hint("Utilisation rolling history (%)", 18, 182, 8f)]); FillPage(p, _history, 210); return p;
    }
    void LoadNics() { _adapters.AddRange(CaptureEngine.EthernetNics()); for (int i = 0; i < _adapters.Count; i++) _nics.Items.Add(CaptureEngine.DisplayName(_adapters[i])); if (_nics.Items.Count > 0) _nics.SelectedIndex = 0; else _nics.Items.Add("(no Ethernet NIC found — offline samples still work)"); }
    NetworkInterface? SelectedNic() => _nics.SelectedIndex >= 0 && _nics.SelectedIndex < _adapters.Count ? _adapters[_nics.SelectedIndex] : null;
    void SetDefaultRange() { var nic = SelectedNic(); if (nic is not null && (_range.Text.Length == 0 || _range.Text == "192.168.1.0/24")) _range.Text = HostScanner.DefaultRange(nic); }
    void LoadSample() { var n = Decoder.Decode(Samples.CiscoAccessVoice()); _listenOutput.Text = Neighbor.Merge(n is null ? [] : [n]).Format("Onboard RJ45 (sample)", "built-in Cisco LLDP decode", 1); _status.Text = "Sample decoded: core-sw-01.lab.local, Gi1/0/24, VLAN 20 / voice VLAN 30."; }
    async Task ListenAsync(Button button) { var nic = SelectedNic(); if (nic is null) { MessageBox.Show(this, "Select an Ethernet adapter first."); return; } button.Enabled = false; _status.Text = $"Listening {(int)_seconds.Value}s on {nic.Name}…"; try { var r = await Task.Run(() => CaptureEngine.Listen(nic.Name, (int)_seconds.Value)); _listenOutput.Text = Neighbor.Merge(r.Neighbors).Format(nic.Name, r.Backend, r.FrameCount); _status.Text = $"Listen complete via {r.Backend}."; } catch (Exception ex) { _listenOutput.Text = ex.Message; _status.Text = "Live capture failed — see details."; } finally { button.Enabled = true; } }
    async Task ScanAsync(Button button)
    {
        if (_scanCts is not null) { _scanCts.Cancel(); return; }
        try { var total = HostScanner.Expand(_range.Text).Count; _hosts.Rows.Clear(); _scanCts = new CancellationTokenSource(); button.Text = "Stop scan"; var progress = new Progress<int>(n => _scanStatus.Text = $"Probed {n}/{total}…"); var found = await HostScanner.ScanAsync(_range.Text, progress, _scanCts.Token); foreach (var h in found) _hosts.Rows.Add(h.Ip, h.RttMs.ToString("0"), h.Method, h.Hostname); _scanStatus.Text = $"{found.Count} live host(s)"; }
        catch (OperationCanceledException) { _scanStatus.Text = "Scan stopped."; } catch (Exception ex) { _scanStatus.Text = ex.Message; } finally { _scanCts?.Dispose(); _scanCts = null; button.Text = "Scan live hosts"; }
    }
    async Task DanteAsync(Button button) { button.Enabled = false; _status.Text = "Browsing Dante mDNS…"; var name = SelectedNic()?.Name ?? "Ethernet"; try { var live = await Task.Run(() => DanteMdns.Browse()); _danteOutput.Text = DanteMdns.Format(live.Count > 0 ? live : DanteMdns.LabFoh(), live.Count > 0 ? "mDNS" : "FOH lab sample (no mDNS replies)", name); _status.Text = live.Count > 0 ? $"Found {live.Count} Dante device(s)." : "No mDNS replies — FOH sample rack shown."; } catch (Exception ex) { _danteOutput.Text = DanteMdns.Format(DanteMdns.LabFoh(), "FOH lab sample", name) + "\r\n\r\nLive mDNS error: " + ex.Message; _status.Text = "mDNS unavailable — sample shown."; } finally { button.Enabled = true; } }
    async Task OscAsync(Button button)
    {
        if (_oscCts is not null) { _oscCts.Cancel(); return; } var port = (int)_oscPort.Value; _oscCts = new CancellationTokenSource(); var cts = _oscCts; button.Text = "Stop listener"; _oscPort.Enabled = false; _oscOutput.Text = $"PortSight OSC UDP {port}\r\nWaiting… loopback samples will appear below.\r\n";
        try { await Task.Run(() => { using var udp = Osc.Bind(port); Osc.Send("127.0.0.1", port, Osc.GyroSample()); Osc.Send("127.0.0.1", port, Osc.FreqSample()); while (!cts.IsCancellationRequested) { try { var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0); var msg = Osc.Decode(udp.Receive(ref remote)); BeginInvoke(() => { _oscOutput.AppendText($"{DateTime.Now:HH:mm:ss}  {remote.Address}  {msg.Format()}\r\n"); _status.Text = $"OSC received {msg.Address}"; }); } catch (System.Net.Sockets.SocketException) { } } }, cts.Token); }
        catch (Exception ex) { _oscOutput.AppendText("OSC listener failed: " + ex.Message); } finally { if (_oscCts == cts) _oscCts = null; _oscPort.Enabled = true; button.Text = "Start listener"; }
    }
    async Task LoadAsync(Button button) { var nic = SelectedNic(); if (nic is null) return; button.Enabled = false; _status.Text = "Sampling selected NIC for 3 seconds…"; try { var r = await Task.Run(() => LoadMeter.Sample(nic, 3)); _loadOutput.Text = r.Format(); _status.Text = $"Load: {r.UtilPct:0.0}% — {r.Verdict}."; } catch (Exception ex) { _loadOutput.Text = ex.Message; } finally { button.Enabled = true; } }
    async Task MonitorTickAsync() { if (_monitorBusy) return; var nic = SelectedNic(); if (nic is null) return; _monitorBusy = true; try { var r = await Task.Run(() => LoadMeter.Sample(nic, 1)); _history.Add(r.UtilPct); _monitorStats.Text = $"Utilisation  {r.UtilPct,6:0.0}%     In  {r.MbpsIn,7:0.00} Mbps     Out  {r.MbpsOut,7:0.00} Mbps\r\nPackets      {r.Pps,6:0}/s     Multicast {r.MulticastPct,5:0.0}%     Errors {r.ErrorsPerSec:0.0}/s     Discards {r.DiscardsPerSec:0.0}/s\r\nVerdict      {r.Verdict}     (laptop access-port NIC view, not the switch uplink)"; } catch { _monitorStats.Text = "Unable to read this NIC."; } finally { _monitorBusy = false; } }
    static TextBox Output() => new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Surface, ForeColor = Fg, Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.FixedSingle };
    static void FillPage(TabPage page, Control control, int top)
    {
        control.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        void ResizeToPage()
        {
            control.Size = new Size(Math.Max(500, page.ClientSize.Width - 36), Math.Max(180, page.ClientSize.Height - top - 18));
        }
        page.Resize += (_, _) => ResizeToPage();
        page.HandleCreated += (_, _) => ResizeToPage();
    }
    static Label Hint(string text, int x, int y, float size = 10f) => new() { Text = text, ForeColor = Muted, AutoSize = true, Location = new Point(x, y), Font = new Font("Segoe UI", size) };
    static Button Button(string text, Point point, Size size, bool secondary = false) => new() { Text = text, Location = point, Size = size, FlatStyle = FlatStyle.Flat, BackColor = secondary ? Surface : Fg, ForeColor = secondary ? Fg : Bg, Cursor = Cursors.Hand };
    static void StyleGrid(DataGridView grid) { grid.BackgroundColor = Surface; grid.BorderStyle = BorderStyle.FixedSingle; grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface2, ForeColor = Fg }; grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Fg, SelectionBackColor = Color.FromArgb(54, 78, 90) }; grid.RowHeadersVisible = false; grid.AllowUserToAddRows = false; grid.ReadOnly = true; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; }
    void DrawTab(object? sender, DrawItemEventArgs e) { var selected = e.Index == ((TabControl)sender!).SelectedIndex; using var b = new SolidBrush(selected ? Surface2 : Surface); e.Graphics.FillRectangle(b, e.Bounds); TextRenderer.DrawText(e.Graphics, ((TabControl)sender!).TabPages[e.Index].Text, Font, e.Bounds, selected ? Fg : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
    static Icon? TryLoadIcon() { try { return new Icon(typeof(MainForm), "app.ico"); } catch { return null; } }
    static Image? TryLoadLogo() { try { using var s = typeof(MainForm).Assembly.GetManifestResourceStream("PortSight.Resources.etr-logo.png"); return s is null ? null : new Bitmap(s); } catch { return null; } }
    protected override void OnFormClosing(FormClosingEventArgs e) { _oscCts?.Cancel(); _scanCts?.Cancel(); _monitorTimer.Stop(); base.OnFormClosing(e); }
}

public sealed class UtilHistory : Control
{
    readonly Queue<double> _values = new(); public UtilHistory() { DoubleBuffered = true; BackColor = Color.FromArgb(30, 34, 40); }
    public void Add(double value) { if (_values.Count >= 90) _values.Dequeue(); _values.Enqueue(Math.Clamp(value, 0, 100)); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var grid = new Pen(Color.FromArgb(60, 71, 80)); using var line = new Pen(Color.FromArgb(117, 181, 208), 2); for (int p = 0; p <= 100; p += 25) { var y = (Height - 1) * (1 - p / 100d); e.Graphics.DrawLine(grid, 0, (float)y, Width, (float)y); } if (_values.Count < 2) return; var a = _values.ToArray(); var points = a.Select((v, i) => new PointF((float)i * (Width - 1) / (a.Length - 1), (float)((Height - 1) * (1 - v / 100d)))).ToArray(); e.Graphics.DrawLines(line, points); }
}
