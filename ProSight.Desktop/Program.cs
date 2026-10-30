using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ProSight.Desktop;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // `ProSight --selftest` (or ProSight.app/Contents/MacOS/ProSight --selftest): report to the terminal, exit 0/1.
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var (report, failed) = SelfTest.RunChecks();
            Console.WriteLine(report);
            return failed == 0 ? 0 : 1;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "ProSight";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SplashWindow(); // swaps itself for MainWindow when startup finishes
        base.OnFrameworkInitializationCompleted();
    }
}
