using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ProSight.Desktop;

/// <summary>
/// Loading screen: logo, progress bar with percentage and the current startup step. Runs <see cref="Startup"/>, then
/// makes the main window the app's main window and closes itself.
/// </summary>
public sealed class SplashWindow : Window
{
    static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#08080a")), Ink = new SolidColorBrush(Color.Parse("#eef0f3")),
        Muted = new SolidColorBrush(Color.Parse("#9a9fa8")), Rim = new SolidColorBrush(Color.Parse("#3a3d44"));
    static readonly FontFamily Mono = new("Menlo, SF Mono, Consolas, monospace");

    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6, Foreground = new SolidColorBrush(Color.Parse("#9aadc0")) };
    readonly TextBlock _step = new() { Text = "Starting ProSight…", Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _percent = new() { Text = "0%", Foreground = Ink, FontFamily = Mono, HorizontalAlignment = HorizontalAlignment.Right };

    public SplashWindow()
    {
        Title = "ProSight";
        Width = 480; Height = 300; CanResize = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;

        var stepRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        stepRow.Children.Add(_step);
        Grid.SetColumn(_percent, 1);
        stepRow.Children.Add(_percent);

        Content = new Border
        {
            BorderBrush = Rim, BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(40, 30, 40, 0), Spacing = 6,
                Children =
                {
                    new Image { Width = 92, Height = 92, Source = LoadLogo(), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "ProSight", FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) },
                    new TextBlock
                    {
                        Text = $"Version {typeof(Startup).Assembly.GetName().Version?.ToString(3)}  ·  Event Tech Research",
                        FontFamily = Mono, FontSize = 11, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new StackPanel { Margin = new Thickness(0, 24, 0, 0), Spacing = 8, Children = { stepRow, _bar } },
                },
            },
        };

        Opened += async (_, _) => await StartAsync();
    }

    async Task StartAsync()
    {
        var progress = new Progress<StartupProgress>(p =>
        {
            _bar.Value = Math.Clamp(p.Percent, 0, 100);
            _percent.Text = $"{p.Percent}%";
            _step.Text = p.Step + (p.Percent < 100 ? "…" : "");
        });
        StartupResult result;
        try { result = await Startup.RunAsync(progress); }
        catch (Exception) { result = new StartupResult([], CaptureEngine.IsAdmin(), NetPlatform.Current.Name); } // never strand the user here

        var main = new MainWindow(result.Adapters);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = main;
        main.Show();
        Close();
    }

    static Bitmap? LoadLogo()
    {
        try
        {
            using var s = typeof(SplashWindow).Assembly.GetManifestResourceStream("ProSight.Desktop.logo.png");
            return s is null ? null : new Bitmap(s);
        }
        catch (Exception) { return null; }
    }
}
