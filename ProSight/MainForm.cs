using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace ProSight;

public sealed class MainForm : Form
{
    // Original ProSight charcoal/steel artwork palette.
    static readonly Color Bg = Color.FromArgb(12, 13, 15), Surface = Color.FromArgb(20, 22, 26), Surface2 = Color.FromArgb(33, 36, 42), Fg = Color.FromArgb(232, 234, 238), Muted = Color.FromArgb(139, 144, 154), Accent = Color.FromArgb(154, 173, 192);
    const int OscMaxChars = 200_000;

    readonly ComboBox _nics = new(); readonly NumericUpDown _seconds = new(); readonly Label _status = new();
    readonly TextBox _listenOutput = Output(), _danteOutput = Output(), _oscOutput = Output(), _loadOutput = Output();
    readonly TextBox _range = new(); readonly DataGridView _hosts = new(); readonly Label _scanStatus = new(); readonly NumericUpDown _oscPort = new();
    readonly Label _monitorStats = new(); readonly UtilHistory _history = new(); readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 1000 };
    readonly List<NetworkInterface> _adapters = []; CancellationTokenSource? _oscCts, _scanCts; bool _monitorBusy;

    // Shared wait indicator: real progress where known (host scan), elapsed-vs-expected for timed waits.
    readonly ProgressBar _progress = new(); readonly Label _progressText = new();
    readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 100 };
    int _progressOp; string _progressWhat = ""; DateTime _progressStart; TimeSpan _progressExpected;

    // OSC lines are queued by the receive thread and flushed on the UI thread (no cross-thread UI calls, bounded rate).
    readonly ConcurrentQueue<string> _oscLines = new(); readonly System.Windows.Forms.Timer _oscFlush = new() { Interval = 150 };
    volatile string? _oscLastAddress; // written by the receive thread, read by FlushOsc
    ConcurrentDictionary<string, int> _oscCounts = new(); readonly Stopwatch _oscRateWatch = new(); readonly Label _oscRates = new();
    LoadSnapshot? _monitorPrev;

    readonly List<NetworkInterface>? _preloaded;

    /// <param name="adapters">Adapters already found by the loading screen; null = enumerate now.</param>
    public MainForm(List<NetworkInterface>? adapters = null)
    {
        _preloaded = adapters;
        // Layout below is in 96-DPI pixels; scale it to the system DPI (fonts already scale, positions didn't → clipping at 125/150 %).
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ProSight 2 — switch port & network view"; Icon = TryLoadIcon(); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimumSize = new Size(960, 680); Size = new Size(1080, 760); BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 10f);
        var logo = new PictureBox { Image = TryLoadLogo(), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(22, 12), Size = new Size(52, 52), BackColor = Color.Transparent };
        var title = new Label { Text = "ProSight", Font = new Font("Segoe UI Semibold", 21f), ForeColor = Fg, AutoSize = true, Location = new Point(82, 16) };
        var subtitle = new Label { Text = "Laptop RJ45 + standard Cat5/6  ·  switch discovery, live hosts and network health", ForeColor = Muted, AutoSize = true, Location = new Point(84, 51) };
        _status.Text = CaptureEngine.IsAdmin() ? "Ready — running as Administrator." : "Ready. Everything except Listen live works without Administrator.";
        _status.ForeColor = Accent; _status.AutoSize = false; _status.AutoEllipsis = true; _status.Size = new Size(615, 18); _status.Location = new Point(25, 72);
        _nics.DropDownStyle = ComboBoxStyle.DropDownList; _nics.BackColor = Surface; _nics.ForeColor = Fg; _nics.FlatStyle = FlatStyle.Flat; _nics.Width = 610; _nics.Anchor = AnchorStyles.Top | AnchorStyles.Left; _nics.Location = new Point(22, 106); _nics.SelectedIndexChanged += (_, _) => SetDefaultRange(); LoadNics();
        var nicLabel = new Label { Text = "NETWORK ADAPTER", ForeColor = Muted, AutoSize = true, Location = new Point(23, 91), Font = new Font("Segoe UI", 8f) };
        _progressText.Text = "IDLE"; _progressText.ForeColor = Muted; _progressText.AutoSize = true; _progressText.Location = new Point(647, 91); _progressText.Font = new Font("Segoe UI", 8f);
        _progress.Location = new Point(648, 107); _progress.Size = new Size(290, 24); _progress.Minimum = 0; _progress.Maximum = 100; _progress.Style = ProgressBarStyle.Continuous; _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _progressTimer.Tick += (_, _) => TickProgress();
        var tabs = new TabControl { Location = new Point(20, 142), Size = new Size(ClientSize.Width - 40, ClientSize.Height - 162), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, DrawMode = TabDrawMode.OwnerDrawFixed, ItemSize = new Size(125, 32), SizeMode = TabSizeMode.Fixed };
        tabs.DrawItem += DrawTab; tabs.TabPages.Add(BuildListen()); tabs.TabPages.Add(BuildHosts()); tabs.TabPages.Add(BuildDante()); tabs.TabPages.Add(BuildOsc()); tabs.TabPages.Add(BuildLoad()); tabs.TabPages.Add(BuildMonitor());
        Controls.AddRange([logo, title, subtitle, _status, nicLabel, _nics, _progressText, _progress, tabs]);
        _monitorTimer.Tick += async (_, _) => await MonitorTickAsync();
        _oscFlush.Tick += (_, _) => FlushOsc();
    }

    // ---------- progress ----------

    /// <summary>Starts the shared progress bar. Returns a token so a finished older operation can't clear a newer one.</summary>
    int BeginProgress(string what, TimeSpan? expected)
    {
        _progressOp++; _progressWhat = what; _progressStart = DateTime.UtcNow; _progressExpected = expected ?? TimeSpan.Zero;
        _progressText.ForeColor = Accent; SetProgress(_progressOp, 0);
        if (expected is not null) _progressTimer.Start(); else _progressTimer.Stop();
        return _progressOp;
    }

    void SetProgress(int op, int percent)
    {
        if (op != _progressOp) return;
        percent = Math.Clamp(percent, 0, 100);
        _progress.Value = percent;
        _progressText.Text = $"{_progressWhat.ToUpperInvariant()}…  {percent}%";
    }

    void TickProgress()
    {
        if (_progressExpected <= TimeSpan.Zero) return;
        var pct = (int)((DateTime.UtcNow - _progressStart) / _progressExpected * 100);
        SetProgress(_progressOp, Math.Min(pct, 99)); // never claim 100 % until the work actually finishes
    }

    void EndProgress(int op, bool completed)
    {
        if (op != _progressOp) return;
        _progressTimer.Stop();
        if (completed) { _progress.Value = 100; _progressText.Text = $"{_progressWhat.ToUpperInvariant()}  100%  DONE"; }
        else { _progressText.Text = $"{_progressWhat.ToUpperInvariant()}  STOPPED"; }
        _progressText.ForeColor = Muted;
    }

    // ---------- tabs ----------

    TabPage Page(string title) => new(title) { BackColor = Bg, ForeColor = Fg, Padding = new Padding(18), AutoScroll = true };
    TabPage BuildListen()
    {
        var p = Page("Listen"); var hint = Hint("LLDP arrives every ~30 s, CDP every ~60 s. With tshark, stops at the first one; else waits the full time.", 18, 16); var secondsLabel = Hint("MAX WAIT (SECONDS)", 18, 51, 8f);
        _seconds.Minimum = 5; _seconds.Maximum = 120; _seconds.Value = 65; /* > one CDP interval, so CDP-only switches are always caught */ _seconds.Location = new Point(18, 69); _seconds.Width = 90; _seconds.BackColor = Surface; _seconds.ForeColor = Fg;
        var sample = Button("Built-in Cisco sample", new Point(124, 67), new Size(155, 32), true); var listen = Button("Listen live", new Point(288, 67), new Size(125, 32)); sample.Click += (_, _) => LoadSample(); listen.Click += async (_, _) => await ListenAsync(listen);
        _listenOutput.Location = new Point(18, 116); _listenOutput.Text = "Choose Built-in Cisco sample to test the decoder offline.\r\n\r\nLive capture uses tshark when installed, otherwise Windows pktmon. If ProSight isn't running as Administrator, Listen live offers to restart it elevated.";
        p.Controls.AddRange([hint, secondsLabel, _seconds, sample, listen, _listenOutput]); FillPage(p, _listenOutput, 116); return p;
    }
    TabPage BuildHosts()
    {
        var p = Page("Who's up?"); p.Controls.Add(Hint("Ping, then 6 TCP ports in parallel, then ARP for silent on-link devices. Vendors need oui.csv (README).", 18, 16)); _range.Location = new Point(18, 48); _range.Width = 230; _range.BackColor = Surface; _range.ForeColor = Fg; _range.Text = SelectedNic() is { } nic ? HostScanner.DefaultRange(nic) : "192.168.1.0/24";
        var scan = Button("Scan live hosts", new Point(260, 46), new Size(140, 32)); scan.Click += async (_, _) => await ScanAsync(scan); _scanStatus.Location = new Point(416, 53); _scanStatus.ForeColor = Accent; _scanStatus.AutoSize = true;
        _hosts.Location = new Point(18, 93); StyleGrid(_hosts); _hosts.Columns.Add("ip", "IP"); _hosts.Columns.Add("rtt", "RTT (ms)"); _hosts.Columns.Add("method", "Seen via"); _hosts.Columns.Add("mac", "MAC"); _hosts.Columns.Add("vendor", "Vendor"); _hosts.Columns.Add("hostname", "Hostname"); _hosts.Columns[0].Width = 120; _hosts.Columns[1].Width = 75; _hosts.Columns[2].Width = 70; _hosts.Columns[3].Width = 135; _hosts.Columns[4].Width = 190; _hosts.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        p.Controls.AddRange([_range, scan, _scanStatus, _hosts]); FillPage(p, _hosts, 93); return p;
    }
    TabPage BuildDante()
    {
        var p = Page("Dante"); p.Controls.Add(Hint("mDNS browse (_netaudio-arc/-cmc, 3 queries) on the selected NIC. Demo data, if shown, is labelled.", 18, 16)); var browse = Button("Browse Dante", new Point(18, 48), new Size(130, 32)); browse.Click += async (_, _) => await DanteAsync(browse);
        _danteOutput.Location = new Point(18, 94); p.Controls.AddRange([browse, _danteOutput]); FillPage(p, _danteOutput, 94); return p;
    }
    TabPage BuildOsc()
    {
        var p = Page("OSC"); p.Controls.Add(Hint("UDP listener needs no administrator rights. Starts with safe loopback messages for /gyrosc/gyro and /voices/3/osc/14/freq.", 18, 16)); p.Controls.Add(Hint("UDP PORT", 18, 50, 8f)); _oscPort.Minimum = 1; _oscPort.Maximum = 65535; _oscPort.Value = 8000; _oscPort.Location = new Point(18, 68); _oscPort.Width = 100; _oscPort.BackColor = Surface; _oscPort.ForeColor = Fg;
        var osc = Button("Start listener", new Point(130, 66), new Size(125, 32)); osc.Click += async (_, _) => await OscAsync(osc); _oscOutput.Location = new Point(18, 115);
        _oscRates.Location = new Point(270, 74); _oscRates.Size = new Size(620, 20); _oscRates.AutoSize = false; _oscRates.AutoEllipsis = true; _oscRates.ForeColor = Accent; _oscRates.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; _oscRates.Text = "Rates appear here (messages/s per address).";
        p.Controls.AddRange([_oscPort, osc, _oscRates, _oscOutput]); FillPage(p, _oscOutput, 115); return p;
    }
    TabPage BuildLoad()
    {
        var p = Page("Load"); p.Controls.Add(Hint("Take a one-shot three-second reading from the laptop's selected NIC.", 18, 16)); var load = Button("Sample 3 seconds", new Point(18, 48), new Size(145, 32)); load.Click += async (_, _) => await LoadAsync(load);
        _loadOutput.Location = new Point(18, 95); _loadOutput.Text = "Includes utilisation, Mbps in/out, packets, multicast, errors and discards.\r\n\r\nCaveat: this is the laptop NIC on this access port, not the switch uplink."; p.Controls.AddRange([load, _loadOutput]); FillPage(p, _loadOutput, 95); return p;
    }
    TabPage BuildMonitor()
    {
        var p = Page("Monitor"); p.Controls.Add(Hint("Continuous one-second access-port NIC view — this is not switch uplink traffic.", 18, 16)); var startStop = Button("Start monitor", new Point(18, 48), new Size(140, 32)); startStop.Click += (_, _) => { _monitorPrev = null; _monitorTimer.Enabled = !_monitorTimer.Enabled; startStop.Text = _monitorTimer.Enabled ? "Stop monitor" : "Start monitor"; _status.Text = _monitorTimer.Enabled ? "Monitoring selected laptop NIC every second." : "Monitor stopped."; };
        _monitorStats.Location = new Point(18, 99); _monitorStats.AutoSize = true; _monitorStats.MaximumSize = new Size(900, 0); /* wrap the advice line */ _monitorStats.ForeColor = Fg; _monitorStats.Font = new Font("Consolas", 10f); _monitorStats.Text = "Start the monitor to collect live NIC values."; _history.Location = new Point(18, 232); p.Controls.AddRange([startStop, _monitorStats, _history, Hint("Utilisation rolling history (%)", 18, 212, 8f)]); FillPage(p, _history, 232); return p;
    }

    void LoadNics() { _adapters.AddRange(_preloaded ?? CaptureEngine.EthernetNics()); for (int i = 0; i < _adapters.Count; i++) _nics.Items.Add(CaptureEngine.DisplayName(_adapters[i])); if (_nics.Items.Count > 0) _nics.SelectedIndex = 0; else _nics.Items.Add("(no Ethernet NIC found — offline samples still work)"); }
    NetworkInterface? SelectedNic() => _nics.SelectedIndex >= 0 && _nics.SelectedIndex < _adapters.Count ? _adapters[_nics.SelectedIndex] : null;
    void SetDefaultRange() { var nic = SelectedNic(); if (nic is not null && (_range.Text.Length == 0 || _range.Text == "192.168.1.0/24")) _range.Text = HostScanner.DefaultRange(nic); }
    void LoadSample() { var n = Decoder.Decode(Samples.CiscoAccessVoice()); _listenOutput.Text = Neighbor.Merge(n is null ? [] : [n]).Format("Onboard RJ45 (sample)", "built-in Cisco LLDP decode", 1); _status.Text = "Sample decoded: core-sw-01.lab.local, Gi1/0/24, VLAN 20 / voice VLAN 30."; }

    // ---------- actions ----------

    async Task ListenAsync(Button button)
    {
        var nic = SelectedNic();
        if (nic is null) { MessageBox.Show(this, "Select an Ethernet adapter first."); return; }
        if (!CaptureEngine.IsAdmin())
        {
            var answer = MessageBox.Show(this, "Live capture needs Administrator (pktmon / tshark).\nRestart ProSight elevated?", "ProSight", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) RelaunchElevated();
            return;
        }
        int seconds = (int)_seconds.Value;
        button.Enabled = false; _status.Text = $"Listening {seconds}s on {nic.Name}…";
        // +5 s covers process start and pktmon's etl→pcap conversion; the bar holds at 99 % until it really ends.
        var op = BeginProgress("Listening", TimeSpan.FromSeconds(seconds + 5)); bool ok = false;
        try
        {
            var r = await Task.Run(() => CaptureEngine.Listen(nic, seconds));
            _listenOutput.Text = Neighbor.Merge(r.Neighbors).Format(nic.Name, r.Backend, r.FrameCount) + (r.Note.Length > 0 ? "\r\n\r\n" + r.Note : "");
            _status.Text = r.Note.StartsWith("WARNING", StringComparison.Ordinal) ? "Listen complete — result may be from another adapter, see details." : $"Listen complete via {r.Backend}.";
            ok = true;
        }
        catch (Exception ex) { _listenOutput.Text = ex.Message; _status.Text = "Live capture failed — see details."; }
        finally { EndProgress(op, ok); button.Enabled = true; }
    }

    static void RelaunchElevated()
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        try { Process.Start(psi); Application.Exit(); }
        catch (System.ComponentModel.Win32Exception) { /* user declined UAC — stay in the unelevated instance */ }
    }

    async Task ScanAsync(Button button)
    {
        if (_scanCts is not null) { _scanCts.Cancel(); return; }
        int op = 0; bool ok = false;
        try
        {
            var addresses = HostScanner.Expand(_range.Text); var total = addresses.Count;
            _hosts.Rows.Clear(); _scanCts = new CancellationTokenSource(); button.Text = "Stop scan";
            op = BeginProgress("Scanning", expected: null); _scanStatus.Text = $"Scanning 0/{total}…";
            int best = -1; // Parallel completions can report out of order; never move backwards.
            var progress = new Progress<ScanProgress>(sp => { if (sp.Percent < best) return; best = sp.Percent; _scanStatus.Text = sp.Phase + "…"; SetProgress(op, Math.Min(sp.Percent, 99)); });
            var found = await HostScanner.ScanAsync(addresses, SelectedNic(), progress, _scanCts.Token);
            foreach (var h in found) _hosts.Rows.Add(h.Ip, h.RttMs is { } rtt ? rtt.ToString("0") : "—", h.Method, h.Mac, h.Vendor, h.Hostname);
            int arpOnly = found.Count(h => h.Method == "arp");
            _scanStatus.Text = $"{found.Count} live host(s) of {total}" + (arpOnly > 0 ? $" ({arpOnly} only via ARP)" : "") + (Oui.Source.Length == 0 && found.Any(h => h.Mac.Length > 0) ? " · no OUI file → vendors blank" : "");
            ok = true;
        }
        catch (OperationCanceledException) { _scanStatus.Text = "Scan stopped."; }
        catch (Exception ex) { _scanStatus.Text = ex.Message; }
        finally { if (op != 0) EndProgress(op, ok); _scanCts?.Dispose(); _scanCts = null; button.Text = "Scan live hosts"; }
    }

    async Task DanteAsync(Button button)
    {
        const int browseMs = 3200; // 3 queries at 0/1/2 s + jitter window
        button.Enabled = false; _status.Text = "Browsing Dante mDNS…";
        var nic = SelectedNic();
        IPAddress? local = nic is null ? null : HostScanner.PrimaryIPv4(nic)?.Address;
        // Without an IPv4 address on the NIC the query can only go out the OS default route; say so rather than imply the NIC.
        var name = nic is null ? "default route" : local is null ? $"{nic.Name} (no IPv4 address — sent via default route)" : nic.Name;
        var op = BeginProgress("Searching Dante", TimeSpan.FromMilliseconds(browseMs)); bool ok = false;
        try
        {
            var live = await Task.Run(() => DanteMdns.Browse(local, browseMs));
            _danteOutput.Text = DanteMdns.Format(live, "mDNS (live)", name) + (live.Count > 0 ? "" : DemoSection());
            _status.Text = live.Count > 0 ? $"Found {live.Count} Dante device(s) on {name}." : "No Dante devices answered on this NIC.";
            ok = true;
        }
        catch (Exception ex)
        {
            _danteOutput.Text = $"Live mDNS failed on {name}: {ex.Message}" + DemoSection();
            _status.Text = "mDNS unavailable.";
        }
        finally { EndProgress(op, ok); button.Enabled = true; }
    }

    // Offline demo data must never be readable as a live result.
    static string DemoSection() =>
        "\r\n\r\n==================== OFFLINE DEMO DATA — NOT FROM THIS NETWORK ====================\r\n" +
        DanteMdns.Format(DanteMdns.LabFoh(), "built-in FOH demo rack", "n/a (demo)");

    async Task OscAsync(Button button)
    {
        if (_oscCts is not null) { _oscCts.Cancel(); return; }
        var port = (int)_oscPort.Value; _oscCts = new CancellationTokenSource(); var cts = _oscCts;
        button.Text = "Stop listener"; _oscPort.Enabled = false; _oscOutput.Text = $"ProSight OSC UDP {port}\r\nWaiting… loopback samples will appear below.\r\n";
        _oscCounts = new(); _oscRateWatch.Restart(); _oscFlush.Start();
        try
        {
            await Task.Run(() =>
            {
                using var udp = Osc.Bind(port);
                Osc.Send("127.0.0.1", port, Osc.GyroSample()); Osc.Send("127.0.0.1", port, Osc.FreqSample());
                while (!cts.IsCancellationRequested)
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data;
                    try { data = udp.Receive(ref remote); }
                    catch (System.Net.Sockets.SocketException) { continue; } // 500 ms timeout / ICMP reset
                    try
                    {
                        foreach (var msg in Osc.DecodePacket(data))
                        { _oscLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {remote.Address}  {msg.Format()}"); _oscLastAddress = msg.Address; _oscCounts.AddOrUpdate(msg.Address, 1, (_, n) => n + 1); }
                    }
                    catch (FormatException ex)
                    {
                        // A single bad datagram from the network must not kill the listener.
                        _oscLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {remote.Address}  <malformed {data.Length} B: {ex.Message}>");
                    }
                }
            });
        }
        catch (Exception ex) { _oscLines.Enqueue("OSC listener failed: " + ex.Message + (ex is System.Net.Sockets.SocketException ? $" (is UDP {port} already in use?)" : "")); }
        finally
        {
            FlushOsc(); _oscFlush.Stop(); cts.Dispose();
            if (_oscCts == cts) _oscCts = null;
            if (!IsDisposed) { _oscPort.Enabled = true; button.Text = "Start listener"; }
        }
    }

    void FlushOsc()
    {
        if (IsDisposed) return;
        if (_oscRateWatch.ElapsedMilliseconds >= 1000)
        {
            var secs = _oscRateWatch.Elapsed.TotalSeconds; _oscRateWatch.Restart();
            var counts = Interlocked.Exchange(ref _oscCounts, new ConcurrentDictionary<string, int>());
            _oscRates.Text = counts.IsEmpty ? "No messages in the last second."
                : string.Join("   ·   ", counts.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}  {kv.Value / secs:0}/s"));
        }
        if (_oscLines.IsEmpty) return;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 500 && _oscLines.TryDequeue(out var line); i++) sb.Append(line).Append("\r\n");
        _oscOutput.AppendText(sb.ToString());
        if (_oscOutput.TextLength > OscMaxChars) { _oscOutput.Text = _oscOutput.Text[^(OscMaxChars / 2)..]; _oscOutput.SelectionStart = _oscOutput.TextLength; _oscOutput.ScrollToCaret(); }
        if (_oscLastAddress is { } a) _status.Text = $"OSC received {a}";
    }

    async Task LoadAsync(Button button)
    {
        var nic = SelectedNic(); if (nic is null) { MessageBox.Show(this, "Select an Ethernet adapter first."); return; }
        const int secs = 3;
        button.Enabled = false; _status.Text = $"Sampling {nic.Name} for {secs} seconds…";
        var op = BeginProgress("Sampling load", TimeSpan.FromSeconds(secs)); bool ok = false;
        try { var r = await Task.Run(() => LoadMeter.Sample(nic, secs)); _loadOutput.Text = r.Format(); _status.Text = $"Load: {r.UtilPct:0.0}% — {r.Verdict}." + (r.Advisory.Length > 0 ? $" Link {r.LinkBps / 1_000_000} Mbps — see advice." : ""); ok = true; }
        catch (Exception ex) { _loadOutput.Text = ex.Message; _status.Text = "Load sample failed."; }
        finally { EndProgress(op, ok); button.Enabled = true; }
    }

    async Task MonitorTickAsync()
    {
        if (_monitorBusy) return; var nic = SelectedNic(); if (nic is null) return; _monitorBusy = true;
        try
        {
            // Rate from the previous tick's snapshot: no gaps between samples and no thread parked in Sleep.
            var (cur, r) = await Task.Run(() =>
            {
                var snap = LoadMeter.Snapshot(nic);
                var prev = _monitorPrev;
                return (snap, prev is not null && prev.NicId == nic.Id ? LoadMeter.Compute(nic, prev, snap) : null);
            });
            _monitorPrev = cur;
            if (IsDisposed || r is null) return;
            _history.Add(r.UtilPct);
            var flood = r.FloodMbpsIn is { } f ? $"{f,6:0.00} Mbps" : "   n/a";
            _monitorStats.Text = $"Utilisation  {r.UtilPct,6:0.0}%     In  {r.MbpsIn,7:0.00} Mbps     Out  {r.MbpsOut,7:0.00} Mbps     Flood in {flood}\r\nPackets      {r.Pps,6:0}/s     Multicast {r.MulticastPct,5:0.0}%     Errors {r.ErrorsPerSec:0.0}/s     Discards {r.DiscardsPerSec:0.0}/s\r\nVerdict      {r.Verdict}     (laptop access-port NIC view, not the switch uplink)" + (r.Advisory.Length > 0 ? "\r\nAdvice       " + r.Advisory : "");
        }
        catch (Exception) { if (!IsDisposed) _monitorStats.Text = "Unable to read this NIC."; } // async-void tick: never let it escape
        finally { _monitorBusy = false; }
    }

    // ---------- helpers ----------

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
    static Icon? TryLoadIcon() { try { return new Icon(typeof(MainForm), "app.ico"); } catch (Exception) { return null; } }
    static Image? TryLoadLogo()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("ProSight.Resources.etr-logo.png");
            if (s is null) return null;
            // GDI+ requires the source stream to outlive a Bitmap built from it; copy so the stream can be disposed.
            using var decoded = new Bitmap(s);
            return new Bitmap(decoded);
        }
        catch (Exception) { return null; }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) { _oscCts?.Cancel(); _scanCts?.Cancel(); _monitorTimer.Stop(); _progressTimer.Stop(); _oscFlush.Stop(); base.OnFormClosing(e); }
}

public sealed class UtilHistory : Control
{
    readonly Queue<double> _values = new(); public UtilHistory() { DoubleBuffered = true; BackColor = Color.FromArgb(30, 34, 40); }
    public void Add(double value) { if (_values.Count >= 90) _values.Dequeue(); _values.Enqueue(Math.Clamp(value, 0, 100)); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var grid = new Pen(Color.FromArgb(60, 71, 80)); using var line = new Pen(Color.FromArgb(117, 181, 208), 2); for (int p = 0; p <= 100; p += 25) { var y = (Height - 1) * (1 - p / 100d); e.Graphics.DrawLine(grid, 0, (float)y, Width, (float)y); } if (_values.Count < 2) return; var a = _values.ToArray(); var points = a.Select((v, i) => new PointF((float)i * (Width - 1) / (a.Length - 1), (float)((Height - 1) * (1 - v / 100d)))).ToArray(); e.Graphics.DrawLines(line, points); }
}
