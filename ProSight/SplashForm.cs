namespace ProSight;

/// <summary>
/// Loading screen: the logo, a progress bar with percentage and the current startup step. Runs <see cref="Startup"/>,
/// then opens the main window with the prepared adapters and closes itself.
/// </summary>
public sealed class SplashForm : Form
{
    static readonly Color Bg = Color.FromArgb(8, 8, 10), Ink = Color.FromArgb(238, 240, 243), Muted = Color.FromArgb(154, 159, 168),
        Steel = Color.FromArgb(154, 173, 192), Rim = Color.FromArgb(58, 61, 68);

    readonly SlimBar _bar = new();
    readonly Label _step = new() { Text = "Starting ProSight", AutoSize = false, ForeColor = Muted, AutoEllipsis = true };
    readonly Label _percent = new() { Text = "0%", AutoSize = false, ForeColor = Ink, TextAlign = ContentAlignment.TopRight };

    public SplashForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ProSight";
        Icon = TryLoadIcon();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 300);
        BackColor = Bg; ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);
        ShowInTaskbar = true;

        var logo = new PictureBox { Image = TryLoadLogo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(92, 92), Location = new Point(194, 30), BackColor = Color.Transparent };
        var title = new Label { Text = "ProSight", Font = new Font("Segoe UI Semibold", 20f), ForeColor = Ink, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 130, 480, 40) };
        var version = new Label
        {
            Text = $"Version {typeof(Startup).Assembly.GetName().Version?.ToString(3)}  ·  Event Tech Research",
            Font = new Font("Consolas", 8.5f), ForeColor = Muted, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 170, 480, 20),
        };
        _step.Bounds = new Rectangle(40, 222, 330, 20);
        _percent.Bounds = new Rectangle(370, 222, 70, 20);
        _bar.Bounds = new Rectangle(40, 246, 400, 8);
        Controls.AddRange([logo, title, version, _step, _percent, _bar]);
    }

    /// <summary>Thin grey rim, echoing the ring around the Event Tech Research mark.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Rim);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    public async Task<StartupResult> RunStartupAsync()
    {
        var progress = new Progress<StartupProgress>(p =>
        {
            if (IsDisposed) return;
            _bar.Value = p.Percent;
            _percent.Text = $"{p.Percent}%";
            _step.Text = p.Step + (p.Percent < 100 ? "…" : "");
        });
        return await Startup.RunAsync(progress);
    }

    static Icon? TryLoadIcon() { try { return new Icon(typeof(SplashForm), "app.ico"); } catch (Exception) { return null; } }

    static Image? TryLoadLogo()
    {
        try
        {
            using var s = typeof(SplashForm).Assembly.GetManifestResourceStream("ProSight.Resources.etr-logo.png");
            if (s is null) return null;
            using var decoded = new Bitmap(s); // GDI+ needs the stream to outlive a Bitmap; copy so it can be disposed
            return new Bitmap(decoded);
        }
        catch (Exception) { return null; }
    }
}

/// <summary>Shows the loading screen first, then the main window; the app exits when the main window closes.</summary>
public sealed class StartupContext : ApplicationContext
{
    public StartupContext()
    {
        var splash = new SplashForm();
        splash.Shown += async (_, _) =>
        {
            StartupResult result;
            try { result = await splash.RunStartupAsync(); }
            catch (Exception) { result = new StartupResult([], CaptureEngine.IsAdmin(), NetPlatform.Current.Name); } // never strand the user on the splash
            var main = new global::ProSight.MainForm(result.Adapters);
            MainForm = main; // ApplicationContext exits the app when its MainForm closes
            main.Show();
            splash.Close();
        };
        splash.Show();
    }
}

/// <summary>
/// Thin brand-coloured progress bar. The stock ProgressBar ignores custom colours under Windows visual styles (it draws
/// in system green), so the loading screen draws its own.
/// </summary>
public sealed class SlimBar : Control
{
    int _value;
    static readonly Color Track = Color.FromArgb(29, 31, 36), Fill = Color.FromArgb(154, 173, 192);

    public SlimBar() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

    public int Value
    {
        get => _value;
        set { var v = Math.Clamp(value, 0, 100); if (v == _value) return; _value = v; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var track = new SolidBrush(Track);
        using var fill = new SolidBrush(Fill);
        e.Graphics.FillRectangle(track, ClientRectangle);
        e.Graphics.FillRectangle(fill, 0, 0, ClientSize.Width * _value / 100, ClientSize.Height);
    }
}
