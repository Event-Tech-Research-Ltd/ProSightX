
## What it does

ProSight is a field tool for AV, events, venue and IT technicians. Patch your laptop's RJ45 port into a drop with an ordinary Cat5e/6 lead, and it tells you:

| Tab | Answers | Admin needed? |
|---|---|---|
| **Listen** | Which switch, port and VLAN is this socket on? (LLDP / Cisco CDP, incl. LLDP-MED voice VLAN) | Yes — Windows restarts elevated; a Mac asks for your password |
| **Who's up?** | Which devices are live on this subnet? IP, response time, MAC, vendor, hostname | No |
| **Dante** | Which Dante audio devices are on this VLAN, and at what IP? | No |
| **OSC** | Is Open Sound Control traffic arriving, and how fast per address? | No |
| **Load** | How busy and healthy is this connection right now? Speed, utilisation, flood traffic, errors | No |
| **Monitor** | The same, second by second, with a 90-second graph | No |

Every wait shows a progress bar with a percentage, and the app opens with a loading screen while it finds your adapters.

Example **Listen** result (from the built-in sample, which you can run with no switch attached):

```text
ProSight 2.1.0
Adapter     Onboard RJ45 (sample)
Backend     built-in Cisco LLDP decode  frames 1
Protocol    LLDP
Switch      core-sw-01.lab.local
Platform    C9300-48P
Chassis     00:1A:A2:4C:10:18
Port        Gi1/0/24
Port desc   GigabitEthernet1/0/24
Native VLAN 20
Voice VLAN  30
Mgmt IP     10.10.10.1
Link        1000BASE-T FD
```

## Download

Standalone builds — the .NET runtime is included, so there's nothing else to install.

| Platform | File |
|---|---|
| Windows 10/11, Intel/AMD | `ProSight-2.1.0-win-x64.zip` → `ProSight.exe` |
| Windows on Arm | `ProSight-2.1.0-win-arm64.zip` |
| macOS 12+, Apple Silicon | `ProSight-2.1.0-osx-arm64.zip` → `ProSight.app` |
| macOS 12+, Intel | `ProSight-2.1.0-osx-x64.zip` |

Get them from [Releases](https://github.com/eventtech-io/ProSight/releases), or [build them yourself](#build-from-source).

**First launch**

- **Windows:** if you see *"Windows protected your PC"*, click **More info → Run anyway**. The first launch takes a few seconds longer while the exe unpacks.
- **Mac:** drag **ProSight** into **Applications**. If macOS won't open it, go to **System Settings → Privacy & Security** and click **Open Anyway**. When asked whether ProSight may find devices on your local network, click **Allow**. Otherwise Who's up?, Dante and OSC find nothing.

Those warnings appear for builds that aren't signed with a publisher certificate; see [Signing](#signing-and-distribution).

## Quick start at a wall socket

1. Patch the laptop's wired port into the socket. Most Macs need a USB-C or Thunderbolt Ethernet adapter. Wi-Fi won't do.
2. Open ProSight and pick the wired adapter (connected physical adapters are listed first).
3. **Load → Sample 3 seconds.** `1000 Mbps` is good. `100 Mbps` on a gigabit drop usually means a damaged pair.
4. **Listen → Listen live.** Wait up to a minute for the switch, port and VLAN.
5. **Who's up? → Scan live hosts**, and on an audio VLAN, **Dante → Browse Dante**.

There's also a [beginner's guide](https://github.com/eventtech-io/ProSight/wiki) with the jargon explained. *(Link placeholder — point it at wherever you publish the guide.)*

## Recommended extras

- **[Wireshark](https://www.wireshark.org/)** — ProSight uses its `tshark` when present: Listen stops at the first announcement instead of waiting the full time. On a Mac with Wireshark's ChmodBPF helper, Listen also needs no password.
- **Vendor names** — save the IEEE MA-L list ([oui.csv](https://standards-oui.ieee.org/oui/oui.csv)) to `%APPDATA%\ProSight\` on Windows or `~/Library/Application Support/ProSight/` on a Mac. ProSight also reads Wireshark's `manuf` file. No vendor data is bundled.

## How it works

| | Windows | macOS |
|---|---|---|
| **Listen** capture | `tshark` (Npcap, exact adapter) → built-in `pktmon` fallback | `tshark` (if ChmodBPF) → built-in `/usr/sbin/tcpdump` via the admin-password prompt |
| Adapter names | Windows adapter names | `networksetup -listallhardwareports` (Wi-Fi and Thunderbolt Bridge excluded) |
| ARP neighbours | `GetIpNetTable2`, Reachable entries only | `arp -an`; entries that appear during the scan count as live, older ones are marked `arp (cached)` |
| Flood (multicast + broadcast) Mbps | `GetIfEntry2` octet counters, self-checked | n/a — macOS doesn't expose multicast byte counts |
| Dante discovery | mDNS `_netaudio-arc` / `_netaudio-cmc`, 3 queries over ~3 s, pinned to the selected adapter | same; shares UDP 5353 with mDNSResponder or falls back to unicast replies |

Decoders and parsers (LLDP/CDP, LLDP-MED, OSC incl. bundles, mDNS, pcap/pcapng) are written from the protocol specs in plain C#, shared by both apps, and hardened against malformed packets from the network.

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download) on the build machine.

| Target | Command (run on that OS) | Output |
|---|---|---|
| Windows x64 | `build-windows.cmd` or `.\build-windows.ps1` | `dist\win-x64\ProSight\ProSight.exe` + `.zip` |
| Windows Arm | `.\build-windows.ps1 -Runtime win-arm64` | `dist\win-arm64\…` |
| Mac Apple Silicon | `./build-mac.sh` | `dist/osx-arm64/ProSight.app` + `.zip` (ad-hoc signed, self-test run) |
| Mac Intel | `./build-mac.sh osx-x64` | `dist/osx-x64/…` |

For development:

- **Windows:** open `ProSight.sln` in Visual Studio 2022 and press **F5**.
- **Mac:** run `dotnet run --project ProSight.Desktop`. Building the whole solution on a Mac fails on the Windows Forms project, so build `ProSight.Desktop` on its own.

### Self-test

Both apps check their decoders and parsers against fixed inputs, then report what the machine supports (flood counters, ARP, vendor file, admin rights). Exit code `0` means every check passed.

```text
ProSight.exe --selftest                                  # Windows: result window, also saved to %TEMP%\prosight-selftest.txt
/Applications/ProSight.app/Contents/MacOS/ProSight --selftest   # macOS: prints to Terminal
```

## Project layout

```text
ProSight.sln
├── ProSight.Core/        Shared library (net8.0, no NuGet packages)
│   ├── Decoder.cs, Osc.cs, DanteMdns.cs, Pcap.cs, HostScanner.cs, LoadMeter.cs, Oui.cs, SelfTest.cs, Startup.cs
│   └── Platform/         INetPlatform + Windows, macOS and generic implementations
├── ProSight/             Windows Forms app (net8.0-windows) — the Windows build
├── ProSight.Desktop/     Avalonia app (net8.0) — the Mac build; also runs on Windows
├── build-windows.ps1     Standalone single-file exe
└── build-mac.sh          ProSight.app bundle, icon, ad-hoc signing, zip
```

`ProSight.Desktop` depends on [Avalonia](https://avaloniaui.net/) 11.2 (MIT). Nothing else outside the .NET runtime.

## Security and privacy

- **Least privilege.** ProSight runs as a normal user. Elevation is requested only when you click **Listen live**, and only for the capture.
- **Captures stay local and are deleted.** Capture files go to a temporary folder and are removed after each Listen. pktmon can see other adapters' traffic, so nothing is kept.
- **The macOS root shell sees only checked input.** Adapter names are validated against a strict BSD pattern, and every argument is quoted before the admin prompt. A timer inside the root shell always ends the capture, even if ProSight is killed.
- **Scans are bounded.** Who's up? scans only the range you enter, capped at 1,024 addresses. **Only scan networks you're authorised to test.**
- **No data leaves the machine.** No telemetry and no update checks. Reverse DNS goes to your configured resolver.

## Limits

- Listen needs a **managed** switch that sends LLDP or CDP. Unmanaged switches announce nothing.
- Load and Monitor measure the **laptop's own port**, not the switch uplink.
- Who's up? only finds devices that answer ping, one of six TCP ports (80, 443, 22, 445, 3389, 8080) or ARP on your subnet.
- Dante discovery uses mDNS, which doesn't cross routers. Be on the Dante VLAN.

## Signing and distribution

Unsigned builds work, but trigger SmartScreen on Windows and Gatekeeper on other Macs.

- **Windows:** sign `ProSight.exe` with an Authenticode certificate:
  `signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 /f cert.pfx ProSight.exe`
- **macOS:**
  1. Set your own `CFBundleIdentifier` in `ProSight.Desktop/Info.plist`.
  2. Sign with a Developer ID: `codesign --deep --options runtime --timestamp -s "Developer ID Application: …" ProSight.app`
  3. Notarize and staple: `xcrun notarytool submit … --wait`, then `xcrun stapler staple ProSight.app`.

## Contributing

Issues and pull requests are welcome. Please run `--selftest` on your platform before submitting, and include it in your PR description. Keep `ProSight.Core` free of UI code and NuGet dependencies, and put anything OS-specific behind `INetPlatform`.
