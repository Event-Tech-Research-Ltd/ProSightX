using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ProSight.Desktop;

/// <summary>
/// The Avalonia twin of the Windows Forms MainForm: same six tabs, same shared engine (ProSight.Core), same progress bar.
/// Built in code (no XAML) so there is no separate markup to keep in sync.
/// </summary>
public sealed class MainWindow : Window
{
    // Event Tech Research palette: near-black ground, white ink, ProSight steel accent.
    static readonly IBrush Bg = Brush("#08080a"), Surface = Brush("#0f1013"), Line = Brush("#24262c"),
        Ink = Brush("#eef0f3"), Muted = Brush("#9a9fa8"), Steel = Brush("#9aadc0");
    static readonly FontFamily Mono = new("Menlo, SF Mono, Consolas, monospace");
    const int OscMaxChars = 200_000;

    readonly ComboBox _nics = new() { MinWidth = 520 };
    readonly TextBlock _status = new() { Foreground = Steel, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8, MinWidth = 240 };
    readonly TextBlock _progressText = new() { Text = "IDLE", Foreground = Muted, FontSize = 11, FontFamily = Mono };
    readonly NumericUpDown _seconds = new() { Minimum = 5, Maximum = 120, Value = 65, Increment = 5, FormatString = "0", Width = 130 };
    readonly TextBox _listenOut = Output(), _hostsOut = Output(), _danteOut = Output(), _oscOut = Output(), _loadOut = Output();
    readonly TextBox _range = new() { Width = 220 };
    readonly TextBlock _scanStatus = new() { Foreground = Steel, VerticalAlignment = VerticalAlignment.Center };
    readonly NumericUpDown _oscPort = new() { Minimum = 1, Maximum = 65535, Value = 8000, FormatString = "0", Width = 130 };
    readonly TextBlock _oscRates = new() { Foreground = Steel, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _monitorStats = new() { FontFamily = Mono, FontSize = 12.5, Foreground = Ink, TextWrapping = TextWrapping.Wrap, Text = "Start the monitor to collect live NIC values." };
    readonly Sparkline _history = new() { Height = 160 };

    readonly List<NetworkInterface> _adapters = [];
    CancellationTokenSource? _oscCts, _scanCts;
    LoadSnapshot? _monitorPrev; bool _monitorBusy;
    readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _oscFlush = new() { Interval = TimeSpan.FromMilliseconds(150) };
    int _progressOp; string _progressWhat = ""; DateTime _progressStart; TimeSpan _progressExpected;
    readonly ConcurrentQueue<string> _oscLines = new();
    ConcurrentDictionary<string, int> _oscCounts = new(); readonly Stopwatch _oscRateWatch = new();
    volatile string? _oscLastAddress;

    public MainWindow()
    {
        Title = "ProSight";
        Width = 1080; Height = 760; MinWidth = 900; MinHeight = 640;
        Background = Bg;

        var logo = new Image { Width = 48, Height = 48, Source = LoadLogo() };
        var title = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "ProSight", FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Ink },
                new TextBlock { Text = "Laptop RJ45 + standard Cat5/6  ·  switch discovery, live hosts and network health", Foreground = Muted },
            },
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { logo, title } };

        LoadNics();
        _nics.SelectionChanged += (_, _) => SetDefaultRange();
        _status.Text = CaptureEngine.IsAdmin() ? "Ready — running with administrator rights."
            : NetPlatform.Current.ListenNeedsElevatedProcess ? "Ready. Everything except Listen live works without administrator rights."
            : "Ready. Listen live will ask for your password when it runs; everything else needs none.";

        var nicRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        nicRow.Children.Add(Labelled("NETWORK ADAPTER", _nics));
        var progressBox = Labelled("PROGRESS", new StackPanel { Spacing = 4, Children = { _progressText, _progress } });
        progressBox.Margin = new Thickness(20, 0, 0, 0);
        Grid.SetColumn(progressBox, 1);
        nicRow.Children.Add(progressBox);
        _progressTimer.Tick += (_, _) => TickProgress();

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Listen", Content = BuildListen() });
        tabs.Items.Add(new TabItem { Header = "Who's up?", Content = BuildHosts() });
        tabs.Items.Add(new TabItem { Header = "Dante", Content = BuildDante() });
        tabs.Items.Add(new TabItem { Header = "OSC", Content = BuildOsc() });
        tabs.Items.Add(new TabItem { Header = "Load", Content = BuildLoad() });
        tabs.Items.Add(new TabItem { Header = "Monitor", Content = BuildMonitor() });

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(22, 16, 22, 16) };
        _status.Margin = new Thickness(0, 12, 0, 0); nicRow.Margin = new Thickness(0, 12, 0, 12);
        root.Children.Add(header);
        Grid.SetRow(_status, 1); root.Children.Add(_status);
        Grid.SetRow(nicRow, 2); root.Children.Add(nicRow);
        Grid.SetRow(tabs, 3); root.Children.Add(tabs);
        Content = root;

        _monitorTimer.Tick += async (_, _) => await MonitorTickAsync();
        _oscFlush.Tick += (_, _) => FlushOsc();
        Closing += (_, _) => { _oscCts?.Cancel(); _scanCts?.Cancel(); _monitorTimer.Stop(); _progressTimer.Stop(); _oscFlush.Stop(); };
    }

    // ---------- tabs ----------

    Control BuildListen()
    {
        var sample = SecondaryButton("Built-in Cisco sample"); sample.Click += (_, _) => LoadSample();
        var listen = PrimaryButton("Listen live"); listen.Click += async (_, _) => await ListenAsync(listen);
        _listenOut.Text = "Choose Built-in Cisco sample to test the decoder offline.\n\n" +
            (OperatingSystem.IsMacOS()
                ? "Live capture uses tshark if Wireshark is installed, otherwise the built-in tcpdump — macOS asks for your password once per capture."
                : "Live capture uses tshark when installed, otherwise Windows pktmon.");
        return Page("LLDP arrives every ~30 s, CDP every ~60 s. Stops at the first one when tshark is available.",
            Row(Labelled("MAX WAIT (SECONDS)", _seconds), sample, listen), _listenOut);
    }

    Control BuildHosts()
    {
        _range.Text = SelectedNic() is { } nic ? HostScanner.DefaultRange(nic) : "192.168.1.0/24";
        var scan = PrimaryButton("Scan live hosts"); scan.Click += async (_, _) => await ScanAsync(scan);
        _hostsOut.Text = "Enter a range (10.40.0.0/24 or 10.40.0.1-50) and scan.";
        return Page("Ping, then 6 TCP ports in parallel, then ARP for silent on-link devices. Vendors need oui.csv (see README).",
            Row(Labelled("RANGE", _range), scan, _scanStatus), _hostsOut);
    }

    Control BuildDante()
    {
        var browse = PrimaryButton("Browse Dante"); browse.Click += async (_, _) => await DanteAsync(browse);
        return Page("mDNS browse (_netaudio-arc/-cmc, 3 queries) on the selected adapter. Demo data, if shown, is labelled.", Row(browse), _danteOut);
    }

    Control BuildOsc()
    {
        var osc = PrimaryButton("Start listener"); osc.Click += async (_, _) => await OscAsync(osc);
        _oscRates.Text = "Rates appear here (messages/s per address).";
        return Page("UDP listener; needs no administrator rights. Starts with loopback test messages.",
            Row(Labelled("UDP PORT", _oscPort), osc, _oscRates), _oscOut);
    }

    Control BuildLoad()
    {
        var load = PrimaryButton("Sample 3 seconds"); load.Click += async (_, _) => await LoadAsync(load);
        _loadOut.Text = "Includes utilisation, Mbps in/out, packets, multicast, errors and discards.\n\nCaveat: this is the laptop's own connection, not the switch uplink.";
        return Page("One three-second reading of the selected adapter.", Row(load), _loadOut);
    }

    Control BuildMonitor()
    {
        var startStop = PrimaryButton("Start monitor");
        startStop.Click += (_, _) =>
        {
            _monitorPrev = null;
            _monitorTimer.IsEnabled = !_monitorTimer.IsEnabled;
            startStop.Content = _monitorTimer.IsEnabled ? "Stop monitor" : "Start monitor";
            _status.Text = _monitorTimer.IsEnabled ? "Monitoring the selected adapter every second." : "Monitor stopped.";
        };
        var body = new StackPanel { Spacing = 12, Children = { _monitorStats, Caption("UTILISATION, LAST 90 SECONDS (%)"), _history } };
        return Page("Continuous one-second view of the laptop's connection — not switch uplink traffic.", Row(startStop), body);
    }

    // ---------- actions ----------

    void LoadNics()
    {
        _adapters.AddRange(CaptureEngine.EthernetNics());
        var names = _adapters.Select(CaptureEngine.DisplayName).ToList();
        if (names.Count == 0) names.Add("(no wired adapter found — offline samples still work)");
        _nics.ItemsSource = names;
        _nics.SelectedIndex = 0;
    }

    NetworkInterface? SelectedNic() => _nics.SelectedIndex >= 0 && _nics.SelectedIndex < _adapters.Count ? _adapters[_nics.SelectedIndex] : null;

    void SetDefaultRange()
    {
        var nic = SelectedNic();
        if (nic is not null && (string.IsNullOrEmpty(_range.Text) || _range.Text == "192.168.1.0/24")) _range.Text = HostScanner.DefaultRange(nic);
    }

    void LoadSample()
    {
        var n = Decoder.Decode(Samples.CiscoAccessVoice());
        _listenOut.Text = Neighbor.Merge(n is null ? [] : [n]).Format("Onboard RJ45 (sample)", "built-in Cisco LLDP decode", 1);
        _status.Text = "Sample decoded: core-sw-01.lab.local, Gi1/0/24, VLAN 20 / voice VLAN 30.";
    }

    async Task ListenAsync(Button button)
    {
        var nic = SelectedNic();
        if (nic is null) { _status.Text = "Select a wired adapter first."; return; }
        if (NetPlatform.Current.ListenNeedsElevatedProcess && !CaptureEngine.IsAdmin())
        {
            if (await ConfirmAsync("Live capture needs administrator rights.\nRestart ProSight elevated?") && CaptureEngine.RelaunchElevated())
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }
        int seconds = (int)(_seconds.Value ?? 65);
        button.IsEnabled = false;
        _status.Text = OperatingSystem.IsMacOS() && !CaptureEngine.IsAdmin()
            ? $"Listening up to {seconds}s on {nic.Name} — enter your Mac password if asked…"
            : $"Listening up to {seconds}s on {nic.Name}…";
        var op = BeginProgress("Listening", TimeSpan.FromSeconds(seconds + 5)); bool ok = false;
        try
        {
            var r = await Task.Run(() => CaptureEngine.Listen(nic, seconds));
            _listenOut.Text = Neighbor.Merge(r.Neighbors).Format(NetPlatform.Current.FriendlyName(nic), r.Backend, r.FrameCount)
                + (r.Note.Length > 0 ? "\n\n" + r.Note : "");
            _status.Text = r.Note.StartsWith("WARNING", StringComparison.Ordinal)
                ? "Listen complete — result may be from another adapter, see details."
                : $"Listen complete via {r.Backend}.";
            ok = true;
        }
        catch (Exception ex) { _listenOut.Text = ex.Message; _status.Text = "Live capture failed — see details."; }
        finally { EndProgress(op, ok); button.IsEnabled = true; }
    }

    async Task ScanAsync(Button button)
    {
        if (_scanCts is not null) { _scanCts.Cancel(); return; }
        int op = 0; bool ok = false;
        try
        {
            var addresses = HostScanner.Expand(_range.Text ?? ""); var total = addresses.Count;
            _scanCts = new CancellationTokenSource(); button.Content = "Stop scan";
            op = BeginProgress("Scanning", expected: null); _scanStatus.Text = $"Scanning 0/{total}…";
            int best = -1; // parallel completions can report out of order; never move backwards
            var progress = new Progress<ScanProgress>(sp => { if (sp.Percent < best) return; best = sp.Percent; _scanStatus.Text = sp.Phase + "…"; SetProgress(op, Math.Min(sp.Percent, 99)); });
            var found = await HostScanner.ScanAsync(addresses, SelectedNic(), progress, _scanCts.Token);
            var sb = new StringBuilder();
            sb.AppendLine($"{"IP",-16} {"RTT ms",7}  {"Seen via",-13} {"MAC",-18} {"Vendor",-26} Hostname");
            foreach (var h in found)
                sb.AppendLine($"{h.Ip,-16} {(h.RttMs is { } rtt ? rtt.ToString("0") : "—"),7}  {h.Method,-13} {h.Mac,-18} {Clip(h.Vendor, 26),-26} {h.Hostname}");
            _hostsOut.Text = sb.ToString();
            int arpOnly = found.Count(h => h.Method.StartsWith("arp", StringComparison.Ordinal));
            _scanStatus.Text = $"{found.Count} live host(s) of {total}" + (arpOnly > 0 ? $" ({arpOnly} via ARP only)" : "")
                + (Oui.Source.Length == 0 && found.Any(h => h.Mac.Length > 0) ? " · no OUI file → vendors blank" : "");
            ok = true;
        }
        catch (OperationCanceledException) { _scanStatus.Text = "Scan stopped."; }
        catch (Exception ex) { _scanStatus.Text = ex.Message; }
        finally { if (op != 0) EndProgress(op, ok); _scanCts?.Dispose(); _scanCts = null; button.Content = "Scan live hosts"; }
    }

    async Task DanteAsync(Button button)
    {
        const int browseMs = 3200;
        button.IsEnabled = false; _status.Text = "Browsing Dante mDNS…";
        var nic = SelectedNic();
        IPAddress? local = nic is null ? null : HostScanner.PrimaryIPv4(nic)?.Address;
        var name = nic is null ? "default route" : local is null ? $"{nic.Name} (no IPv4 address — sent via default route)" : NetPlatform.Current.FriendlyName(nic);
        var op = BeginProgress("Searching Dante", TimeSpan.FromMilliseconds(browseMs)); bool ok = false;
        try
        {
            var live = await Task.Run(() => DanteMdns.Browse(local, browseMs));
            _danteOut.Text = DanteMdns.Format(live, "mDNS (live)", name) + (live.Count > 0 ? "" : DemoSection());
            _status.Text = live.Count > 0 ? $"Found {live.Count} Dante device(s) on {name}." : "No Dante devices answered on this adapter.";
            ok = true;
        }
        catch (Exception ex) { _danteOut.Text = $"Live mDNS failed on {name}: {ex.Message}" + DemoSection(); _status.Text = "mDNS unavailable."; }
        finally { EndProgress(op, ok); button.IsEnabled = true; }
    }

    // Offline demo data must never be readable as a live result.
    static string DemoSection() =>
        "\n\n==================== OFFLINE DEMO DATA — NOT FROM THIS NETWORK ====================\n" +
        DanteMdns.Format(DanteMdns.LabFoh(), "built-in FOH demo rack", "n/a (demo)");

    async Task OscAsync(Button button)
    {
        if (_oscCts is not null) { _oscCts.Cancel(); return; }
        var port = (int)(_oscPort.Value ?? 8000); _oscCts = new CancellationTokenSource(); var cts = _oscCts;
        button.Content = "Stop listener"; _oscPort.IsEnabled = false;
        _oscOut.Text = $"ProSight OSC UDP {port}\nWaiting… loopback samples will appear below.\n";
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
                        {
                            _oscLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {remote.Address}  {msg.Format()}");
                            _oscLastAddress = msg.Address;
                            _oscCounts.AddOrUpdate(msg.Address, 1, (_, n) => n + 1);
                        }
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
            _oscPort.IsEnabled = true; button.Content = "Start listener";
        }
    }

    void FlushOsc()
    {
        if (_oscRateWatch.ElapsedMilliseconds >= 1000)
        {
            var secs = _oscRateWatch.Elapsed.TotalSeconds; _oscRateWatch.Restart();
            var counts = Interlocked.Exchange(ref _oscCounts, new ConcurrentDictionary<string, int>());
            _oscRates.Text = counts.IsEmpty ? "No messages in the last second."
                : string.Join("   ·   ", counts.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}  {kv.Value / secs:0}/s"));
        }
        if (_oscLines.IsEmpty) return;
        var sb = new StringBuilder(_oscOut.Text);
        for (int i = 0; i < 500 && _oscLines.TryDequeue(out var line); i++) sb.Append(line).Append('\n');
        var text = sb.ToString();
        if (text.Length > OscMaxChars) text = text[^(OscMaxChars / 2)..];
        _oscOut.Text = text;
        _oscOut.CaretIndex = text.Length; // keep the newest line in view
        if (_oscLastAddress is { } a) _status.Text = $"OSC received {a}";
    }

    async Task LoadAsync(Button button)
    {
        var nic = SelectedNic(); if (nic is null) { _status.Text = "Select a wired adapter first."; return; }
        const int secs = 3;
        button.IsEnabled = false; _status.Text = $"Sampling {nic.Name} for {secs} seconds…";
        var op = BeginProgress("Sampling load", TimeSpan.FromSeconds(secs)); bool ok = false;
        try
        {
            var r = await Task.Run(() => LoadMeter.Sample(nic, secs));
            _loadOut.Text = r.Format();
            _status.Text = $"Load: {r.UtilPct:0.0}% — {r.Verdict}." + (r.Advisory.Length > 0 ? $" Link {r.LinkBps / 1_000_000} Mbps — see advice." : "");
            ok = true;
        }
        catch (Exception ex) { _loadOut.Text = ex.Message; _status.Text = "Load sample failed."; }
        finally { EndProgress(op, ok); button.IsEnabled = true; }
    }

    async Task MonitorTickAsync()
    {
        if (_monitorBusy) return; var nic = SelectedNic(); if (nic is null) return; _monitorBusy = true;
        try
        {
            // Rate from the previous tick's snapshot: continuous, no gaps, no thread parked in Sleep.
            var prev = _monitorPrev;
            var (cur, r) = await Task.Run(() =>
            {
                var snap = LoadMeter.Snapshot(nic);
                return (snap, prev is not null && prev.NicId == nic.Id ? LoadMeter.Compute(nic, prev, snap) : null);
            });
            _monitorPrev = cur;
            if (r is null) return;
            _history.Add(r.UtilPct);
            var flood = r.FloodMbpsIn is { } f ? $"{f,6:0.00} Mbps" : "   n/a";
            _monitorStats.Text =
                $"Utilisation  {r.UtilPct,6:0.0}%     In  {r.MbpsIn,7:0.00} Mbps     Out  {r.MbpsOut,7:0.00} Mbps     Flood in {flood}\n" +
                $"Packets      {r.Pps,6:0}/s     Multicast {r.MulticastPct,5:0.0}%     Errors {r.ErrorsPerSec:0.0}/s     Discards {r.DiscardsPerSec:0.0}/s\n" +
                $"Verdict      {r.Verdict}" + (r.Advisory.Length > 0 ? "\nAdvice       " + r.Advisory : "");
        }
        catch (Exception) { _monitorStats.Text = "Unable to read this adapter."; } // timer callback: never let it escape
        finally { _monitorBusy = false; }
    }

    // ---------- progress (same contract as the Windows app) ----------

    int BeginProgress(string what, TimeSpan? expected)
    {
        _progressOp++; _progressWhat = what; _progressStart = DateTime.UtcNow; _progressExpected = expected ?? TimeSpan.Zero;
        _progressText.Foreground = Steel; SetProgress(_progressOp, 0);
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
        else _progressText.Text = $"{_progressWhat.ToUpperInvariant()}  STOPPED";
        _progressText.Foreground = Muted;
    }

    // ---------- helpers ----------

    async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new Window
        {
            Title = "ProSight", Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Surface,
        };
        var yes = PrimaryButton("Yes"); yes.Click += (_, _) => dialog.Close(true);
        var no = SecondaryButton("No"); no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ink },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }

    static Control Page(string hint, Control controls, Control body)
    {
        var top = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = hint, Foreground = Muted, TextWrapping = TextWrapping.Wrap }, controls } };
        var dock = new DockPanel { Margin = new Thickness(4, 14, 4, 4) };
        DockPanel.SetDock(top, Dock.Top);
        body.Margin = new Thickness(0, 14, 0, 0);
        dock.Children.Add(top);
        dock.Children.Add(body);
        return dock;
    }

    static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var i in items) { i.VerticalAlignment = VerticalAlignment.Bottom; row.Children.Add(i); }
        return row;
    }

    static Control Labelled(string label, Control control) =>
        new StackPanel { Spacing = 4, Children = { Caption(label), control } };

    static TextBlock Caption(string text) => new() { Text = text, FontSize = 11, Foreground = Muted, FontFamily = Mono };

    static Button PrimaryButton(string text) =>
        new() { Content = text, Background = Ink, Foreground = Bg, FontWeight = FontWeight.SemiBold, Padding = new Thickness(16, 7), CornerRadius = new CornerRadius(999) };

    static Button SecondaryButton(string text) =>
        new() { Content = text, Background = Surface, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(16, 7), CornerRadius = new CornerRadius(999) };

    static TextBox Output() => new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = Mono, FontSize = 12.5, Background = Surface, Foreground = Ink, BorderBrush = Line,
    };

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    static Bitmap? LoadLogo()
    {
        try
        {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("ProSight.Desktop.logo.png");
            return s is null ? null : new Bitmap(s);
        }
        catch (Exception) { return null; }
    }
}

/// <summary>Rolling utilisation history: 90 points, 0–100 %, grid every 25 %.</summary>
public sealed class Sparkline : Control
{
    readonly Queue<double> _values = new();
    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#24262c")), 1);
    static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.Parse("#9aadc0")), 2);
    static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#0f1013"));

    public void Add(double value)
    {
        if (_values.Count >= 90) _values.Dequeue();
        _values.Enqueue(Math.Clamp(value, 0, 100));
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        ctx.FillRectangle(Fill, new Rect(0, 0, w, h));
        for (int p = 0; p <= 100; p += 25)
        {
            var y = (h - 1) * (1 - p / 100d);
            ctx.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }
        if (_values.Count < 2) return;
        var a = _values.ToArray();
        Point Pt(int i) => new(i * (w - 1) / (a.Length - 1), (h - 1) * (1 - a[i] / 100d));
        for (int i = 1; i < a.Length; i++) ctx.DrawLine(LinePen, Pt(i - 1), Pt(i));
    }
}
