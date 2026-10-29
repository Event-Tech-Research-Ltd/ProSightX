using System.ComponentModel;
using System.Diagnostics;

namespace ProSight;

public static class ProcessRunner
{
    /// <summary>Runs a tool; returns its exit code (-1 if it could not start or exceeded <paramref name="timeout"/>) and its output.</summary>
    public static (int Code, string Output) Run(string file, TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a); // no hand-built quoting
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (-1, "could not start");
            // Drain both pipes concurrently; otherwise a chatty child fills the pipe buffer and both sides block forever.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                p.WaitForExit();
                return (-1, $"timed out after {timeout.TotalSeconds:0} s");
            }
            p.WaitForExit(); // ensures redirected output has been fully read
            Task.WaitAll(stdout, stderr);
            return (p.ExitCode, (stderr.Result + " " + stdout.Result).Trim());
        }
        catch (Win32Exception ex)
        {
            return (-1, ex.Message); // not found / access denied
        }
    }

    public static string? Which(string name)
    {
        var ext = OperatingSystem.IsWindows() && Path.GetExtension(name).Length == 0 ? ".exe" : "";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var p = Path.Combine(dir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string Tail(string s, int max = 300)
    {
        s = s.Trim().Replace("\r", "").Replace('\n', ' ');
        return s.Length > max ? "…" + s[^max..] : s;
    }

    /// <summary>The capture filter shared by tshark and tcpdump: LLDP EtherType, CDP and LLDP multicast MACs.</summary>
    public const string DiscoveryFilter = "ether proto 0x88cc or ether dst 01:00:0c:cc:cc:cc or ether dst 01:80:c2:00:00:0e";
}
