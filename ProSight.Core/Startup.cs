using System.Diagnostics;
using System.Net.NetworkInformation;

namespace ProSight;

public readonly record struct StartupProgress(int Percent, string Step);

/// <summary>What the loading screen prepared, handed to the main window so it doesn't redo the work.</summary>
public sealed record StartupResult(List<NetworkInterface> Adapters, bool IsAdmin, string Platform);

/// <summary>
/// The work behind the loading screen, shared by the Windows and Mac apps. Every step is real startup work
/// (no fake delays beyond a short minimum so the screen doesn't just flicker), and percentages follow step completion.
/// </summary>
public static class Startup
{
    /// <summary>Shortest time the loading screen stays up, so a fast start doesn't flash it.</summary>
    public static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(900);

    public static async Task<StartupResult> RunAsync(IProgress<StartupProgress> progress)
    {
        var clock = Stopwatch.StartNew();
        progress.Report(new(5, "Starting ProSight"));

        progress.Report(new(15, "Finding wired network adapters"));
        // Enumerating adapters (and, on macOS, asking networksetup for their names) can take a second or two.
        var adapters = await Task.Run(() =>
        {
            try { return CaptureEngine.EthernetNics(); }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { return new List<NetworkInterface>(); }
        });
        progress.Report(new(60, adapters.Count switch
        {
            0 => "No wired adapter found — offline samples still work",
            1 => "Found 1 wired adapter",
            var n => $"Found {n} wired adapters",
        }));

        progress.Report(new(65, "Loading device vendor list"));
        await Task.Run(() => _ = Oui.Source); // reads oui.csv/manuf once (~1 MB) instead of during the first scan

        progress.Report(new(90, "Checking permissions"));
        bool admin = CaptureEngine.IsAdmin();

        var remaining = MinimumVisible - clock.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        progress.Report(new(100, "Ready"));
        return new StartupResult(adapters, admin, NetPlatform.Current.Name);
    }
}
