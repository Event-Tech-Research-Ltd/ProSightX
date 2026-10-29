using System.Text;

namespace ProSight;

/// <summary>
/// "ProSight --selftest": checks the decoders and parsers on this machine without a switch or NuGet test packages.
/// Pass/fail checks use fixed inputs; INFO lines report what this computer's network APIs return.
/// The UI decides how to show the report; exit code 0 = all passed. The report is also saved to the temp folder.
/// </summary>
public static class SelfTest
{
    public static (string Report, int Failed) RunChecks()
    {
        var log = new StringBuilder();
        int failed = 0, passed = 0;
        void Check(string name, Func<bool> test)
        {
            bool ok;
            string detail = "";
            try { ok = test(); }
            catch (Exception ex) { ok = false; detail = $" ({ex.GetType().Name}: {ex.Message})"; }
            if (ok) passed++; else failed++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{detail}");
        }
        void Info(string line) => log.AppendLine($"INFO  {line}");
        bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }

        // --- LLDP decoder (built-in Cisco frame) ---
        var n = Decoder.Decode(Samples.CiscoAccessVoice());
        Check("LLDP: decodes the sample frame", () => n is { Protocol: "LLDP" });
        Check("LLDP: switch name", () => n?.SwitchName == "core-sw-01.lab.local");
        Check("LLDP: port ID", () => n?.PortId == "Gi1/0/24");
        Check("LLDP: native VLAN 20", () => n?.NativeVlan == "20");
        Check("LLDP: voice VLAN 30 (LLDP-MED)", () => n?.VoiceVlan == "30");
        Check("LLDP: management IP", () => n?.Mgmt == "10.10.10.1");
        Check("LLDP: MAU type 1000BASE-T FD", () => n?.Duplex == "1000BASE-T FD");
        Check("LLDP: truncated frame doesn't throw", () => { var f = Samples.CiscoAccessVoice(); Decoder.Decode(f[..40]); return true; });

        // --- OSC ---
        Check("OSC: encode/decode round trip", () =>
        {
            var m = Osc.DecodePacket(Osc.Encode(Osc.GyroSample())).Single();
            return m.Address == "/gyrosc/gyro" && m.Args.Count == 3 && Math.Abs((float)m.Args[1] + 0.48f) < 1e-6;
        });
        Check("OSC: #bundle with two messages", () =>
        {
            var a = Osc.Encode(Osc.GyroSample()); var b = Osc.Encode(Osc.FreqSample());
            var bundle = Cat(Pad("#bundle"), new byte[8], Be32(a.Length), a, Be32(b.Length), b);
            var ms = Osc.DecodePacket(bundle);
            return ms.Count == 2 && ms[1].Address == "/voices/3/osc/14/freq";
        });
        Check("OSC: truncated packet → FormatException only", () => Throws<FormatException>(() => Osc.DecodePacket(Osc.Encode(Osc.GyroSample())[..^2])));
        Check("OSC: garbage → FormatException only", () => Throws<FormatException>(() => Osc.DecodePacket([0xFF, 0xFF, 0xFF, 0xFF, 0xFF])));
        Check("OSC: oversized bundle element rejected", () => Throws<FormatException>(() => Osc.DecodePacket(Cat(Pad("#bundle"), new byte[8], Be32(9999)))));

        // --- Range parsing ---
        Check("Range: 10.0.0.1-50 → 50 addresses", () => HostScanner.Expand("10.0.0.1-50").Count == 50);
        Check("Range: 10.40.0.0/24 → 254 (no network/broadcast)", () =>
        {
            var r = HostScanner.Expand("10.40.0.0/24");
            return r.Count == 254 && r[0].ToString() == "10.40.0.1" && r[^1].ToString() == "10.40.0.254";
        });
        Check("Range: 10.0.0.1-10.0.0.9 → 9", () => HostScanner.Expand("10.0.0.1-10.0.0.9").Count == 9);
        Check("Range: legacy '10.1-5' rejected", () => Throws<ArgumentException>(() => HostScanner.Expand("10.1-5")));
        Check("Range: end before start rejected", () => Throws<ArgumentException>(() => HostScanner.Expand("10.0.0.50-1")));
        Check("Range: /20 (4,094) over the cap rejected", () => Throws<ArgumentException>(() => HostScanner.Expand("10.0.0.0/20")));

        // --- mDNS parser ---
        Check("mDNS: self-pointing name doesn't crash", () =>
        {
            var evil = Cat(Header(an: 1), [0xC0, 12]);
            DanteMdns.ParseDatagrams([evil]);
            return true;
        });
        Check("mDNS: PTR → SRV → A resolves one device + IP", () =>
        {
            var d = DanteMdns.ParseDatagrams([DanteReply("FOH-CL5", "_netaudio-arc._udp.local", 10, 40, 0, 10)]);
            return d.Count == 1 && d[0].Name == "FOH-CL5" && d[0].Ip == "10.40.0.10" && d[0].Model == "CL5";
        });
        Check("mDNS: non-queried _netaudio-chan PTR is not a device", () =>
            DanteMdns.ParseDatagrams([DanteReply("01@FOH-CL5", "_netaudio-chan._udp.local", 10, 40, 0, 10)]).Count == 0);
        Check("mDNS: A record from a non-Dante host is not a device", () =>
            DanteMdns.ParseDatagrams([Cat(Header(an: 1), Name("printer.local"), Be16(1), Be16(1), Be32(120), Be16(4), [192, 168, 1, 50])]).Count == 0);

        // --- pcapng interface attribution ---
        Check("pcapng: frames carry their capture adapter's name", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"prosight-selftest-{Guid.NewGuid():N}.pcapng");
            try
            {
                File.WriteAllBytes(path, Cat(
                    Block(0x0A0D0D0A, Cat(Le32(0x1A2B3C4D), Le16(1), Le16(0), new byte[8])),
                    Block(1, Cat(Le16(1), Le16(0), Le32(0), Opt(3, "Onboard I219"), Le32(0))),
                    Block(1, Cat(Le16(1), Le16(0), Le32(0), Opt(3, "USB dock"), Le32(0))),
                    Block(6, Cat(Le32(1), Le32(0), Le32(0), Le32(4), Le32(4), [1, 2, 3, 4]))));
                var f = Pcap.Extract(path).Single();
                return f.InterfaceId == 1 && f.InterfaceName == "USB dock" && f.Data.Length == 4;
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        });

        // --- macOS helpers (pure functions; checked on every OS) ---
        Check("macOS: networksetup ports parsed", () =>
        {
            var m = MacNetPlatform.ParseHardwarePorts("Hardware Port: Wi-Fi\nDevice: en0\nEthernet Address: aa\n\nHardware Port: USB 10/100/1000 LAN\nDevice: en7\nEthernet Address: bb\n");
            return m.Count == 2 && m["en7"] == "USB 10/100/1000 LAN" && m["en0"] == "Wi-Fi";
        });
        Check("macOS: arp -an parsed (short octets padded, own/incomplete skipped)", () =>
        {
            var e = MacNetPlatform.ParseArp(
                "? (10.40.0.12) at 0:1d:c1:2:3:4 on en7 ifscope [ethernet]\n" +
                "? (10.40.0.99) at (incomplete) on en7 ifscope [ethernet]\n" +
                "? (10.40.0.5) at 8c:85:90:aa:bb:cc on en7 ifscope permanent [ethernet]\n" +
                "? (224.0.0.251) at 1:0:5e:0:0:fb on en7 ifscope permanent [ethernet]\n");
            return e.Count == 1 && e[0].Address.ToString() == "10.40.0.12" && e[0].Mac == "00:1D:C1:02:03:04" && !e[0].ConfirmedReachable;
        });
        Check("macOS: interface names checked before reaching a root shell", () =>
            MacNetPlatform.IsSafeInterfaceName("en7") && !MacNetPlatform.IsSafeInterfaceName("en7; rm -rf /") && !MacNetPlatform.IsSafeInterfaceName("en0'"));
        Check("macOS: shell + AppleScript quoting round-trips quotes and backslashes", () =>
            MacNetPlatform.ShellQuote("a'b") == "'a'\\''b'" &&
            MacNetPlatform.AppleScriptString("say \"hi\" \\ ok") == "\"say \\\"hi\\\" \\\\ ok\"");

        // --- This computer (informational: depends on hardware, not pass/fail) ---
        try
        {
            var platform = NetPlatform.Current;
            Info($"Platform: {platform.Name}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            var nics = CaptureEngine.EthernetNics();
            Info($"Wired adapters: {nics.Count}" + (nics.Count > 0 ? $" — default: {platform.FriendlyName(nics[0])} ({nics[0].OperationalStatus})" : ""));
            foreach (var nic in nics.Take(4))
            {
                var flood = platform.FloodInOctets(nic);
                Info($"{platform.FriendlyName(nic)}: flood counters {(flood is null ? "UNAVAILABLE (Flood in will show n/a)" : "available")}" +
                     (CaptureEngine.IsLikelyVirtual(nic) ? ", looks virtual" : ""));
            }
            Info($"ARP: {platform.ArpTable().Count} IPv4 neighbour(s) in the table right now");
            Info($"OUI file: {(Oui.Source.Length > 0 ? Oui.Source : "none (Vendor column will be blank)")}");
            Info($"Administrator: {(CaptureEngine.IsAdmin() ? "yes" : platform.ListenNeedsElevatedProcess ? "no (Listen live will ask to restart elevated)" : "no (Listen live will ask for your password)")}");
        }
        catch (Exception ex) { Info($"environment probe failed: {ex.Message}"); }

        log.Insert(0, $"ProSight {typeof(SelfTest).Assembly.GetName().Version?.ToString(3)} self-test — {passed} passed, {failed} failed{Environment.NewLine}{Environment.NewLine}");
        var report = log.ToString();
        var file = Path.Combine(Path.GetTempPath(), "prosight-selftest.txt");
        try { File.WriteAllText(file, report); report += $"{Environment.NewLine}Saved to {file}"; } catch (IOException) { }
        return (report, failed);
    }

    // ---- byte builders ----
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];
    static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    static byte[] Le16(int v) => [(byte)v, (byte)(v >> 8)];
    static byte[] Le32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    static byte[] Pad(string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); return Cat(b, new byte[(4 - b.Length % 4) % 4]); }
    static byte[] Header(int an) => Cat(Be16(0), Be16(0x8400), Be16(0), Be16(an), Be16(0), Be16(0));
    static byte[] Name(string fqdn) =>
        Cat(Cat(fqdn.Split('.').Select(l => Cat([(byte)l.Length], Encoding.ASCII.GetBytes(l))).ToArray()), [0]);
    static byte[] Rr(string owner, int type, byte[] rdata) => Cat(Name(owner), Be16(type), Be16(0x8001), Be32(120), Be16(rdata.Length), rdata);

    /// <summary>A response with PTR (service → instance), SRV (instance → host), TXT (model) and A (host → IP).</summary>
    static byte[] DanteReply(string instance, string service, byte a, byte b, byte c, byte d)
    {
        var fqdn = $"{instance}.{service}";
        var host = $"{instance.Replace('@', '-')}.local";
        return Cat(Header(an: 4),
            Rr(service, 12, Name(fqdn)),
            Rr(fqdn, 33, Cat(Be16(0), Be16(0), Be16(4440), Name(host))),
            Rr(fqdn, 16, Cat([(byte)"model=CL5".Length], Encoding.ASCII.GetBytes("model=CL5"))),
            Rr(host, 1, [a, b, c, d]));
    }

    static byte[] Opt(int code, string value)
    {
        var v = Encoding.UTF8.GetBytes(value);
        return Cat(Le16(code), Le16(v.Length), v, new byte[(4 - v.Length % 4) % 4]);
    }

    static byte[] Block(int type, byte[] body)
    {
        body = Cat(body, new byte[(4 - body.Length % 4) % 4]);
        int len = 12 + body.Length;
        return Cat(Le32(type), Le32(len), body, Le32(len));
    }
}
