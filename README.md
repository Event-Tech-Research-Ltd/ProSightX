# ProSight 2

ProSight is a .NET 8 tool for Windows and macOS (Apple Silicon and Intel) for checking a normal laptop RJ45 connection using ordinary Cat5/6 patch leads. It helps identify a managed switch's port/VLAN information and gives an honest, laptop-side view of the connected network. No special lab or probe cables are required.

## Projects

| Project | What it is | Runs on |
|---|---|---|
| `ProSight.Core` | Decoders, parsers, scanners and the per-OS network layer (`Platform/`). No NuGet packages. | Windows, macOS |
| `ProSight` | The original Windows Forms app. | Windows |
| `ProSight.Desktop` | The Avalonia app — the Mac version (also runs on Windows). | macOS, Windows |

## Open and run (Windows)

1. Install Visual Studio 2022 with **Desktop development with .NET** and the .NET 8 SDK.
2. Open `ProSight.sln`.
3. Press **F5**. ProSight starts **unprivileged**; every tab except **Listen live** works without Administrator. Listen live offers to restart ProSight elevated (one UAC prompt) when needed.

The Windows Forms app uses no third-party NuGet packages. `ProSight.Desktop` uses Avalonia (MIT licence).

## Standalone builds (nothing to install on the target machine)

Both scripts bundle the .NET runtime, so the people using ProSight don't need .NET installed. Only the machine that builds needs the .NET 8 SDK.

| Platform | Command (run on that OS) | Result |
|---|---|---|
| Windows (Intel/AMD) | `build-windows.cmd` or `.\build-windows.ps1` | `dist\win-x64\ProSight\ProSight.exe` — one file, plus a `.zip` |
| Windows on Arm | `.\build-windows.ps1 -Runtime win-arm64` | `dist\win-arm64\ProSight\ProSight.exe` |
| Mac (Apple Silicon) | `./build-mac.sh` | `dist/osx-arm64/ProSight.app`, plus a `.zip` |
| Mac (Intel) | `./build-mac.sh osx-x64` | `dist/osx-x64/ProSight.app` |

The Windows exe is self-extracting: the first launch unpacks native parts to a temp folder, so it starts a little slower once. Unsigned downloads trigger SmartScreen ("Windows protected your PC" → More info → Run anyway); sign the exe with an Authenticode code-signing certificate (`signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 /f cert.pfx ProSight.exe`) to avoid that. For the Mac equivalent, see *Distributing the Mac app*.

## Build and run (Mac, Apple Silicon)

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download) for macOS Arm64.
2. In Terminal, in this folder: `./build-mac.sh` (or `./build-mac.sh osx-x64` for Intel Macs).
3. Open `dist/osx-arm64/ProSight.app`. The script also runs the self-test and makes a `.zip`.

For a quick run during development: `dotnet run --project ProSight.Desktop`. (Building the whole `ProSight.sln` on a Mac fails on the Windows Forms project; build `ProSight.Desktop` instead.)

**How the Mac version differs**

- **Listen live** asks for your Mac password each time it runs (macOS administrator prompt), then uses the built-in `tcpdump`. If Wireshark is installed with its ChmodBPF helper, it uses `tshark` with no password and stops at the first announcement.
- **Network adapter** lists wired ports by their macOS names (e.g. *USB 10/100/1000 LAN (en7)*); Wi-Fi and Thunderbolt Bridge are left out.
- **Who's up?** reads the ARP cache with `arp -an`. macOS doesn't say whether an entry is fresh, so devices found only through ARP are shown as `arp` if their entry appeared during the scan, or `arp (cached)` if it was already there (it may be up to ~20 minutes old).
- **Load / Monitor**: *Flood in* shows n/a — macOS doesn't expose multicast byte counts.
- **Local Network permission**: macOS 15 and later ask once whether ProSight may use the local network. Click **Allow**, or Who's up?, Dante and OSC will find nothing. (Change it later in System Settings → Privacy & Security → Local Network.)
- **Vendor names**: put `oui.csv` in `~/Library/Application Support/ProSight/` (files added inside the signed app would break its signature).

**Distributing the Mac app**

`build-mac.sh` ad-hoc signs the app, which is enough on the Mac that built it. On other Macs, Gatekeeper blocks ad-hoc apps downloaded from the internet. To distribute: sign with a Developer ID Application certificate (`codesign --deep --options runtime --timestamp -s "Developer ID Application: …"`), then notarize with `xcrun notarytool submit … --wait` and `xcrun stapler staple ProSight.app`. Set your own `CFBundleIdentifier` in `ProSight.Desktop/Info.plist` first.

## Tabs

| Tab | What it does |
|---|---|
| **Listen** | Waits up to the set time (default 65 s, longer than one CDP interval) and **stops at the first LLDP/CDP frame** when tshark is installed. pktmon captures every adapter, so frames are attributed to the selected NIC where the capture records it; if they can't be, the result carries an explicit warning that it may be another adapter's port (e.g. a dock). |
| **Who's up?** | Scans the CIDR/range. Per host: ICMP, then TCP 80/443/22/445/3389/8080 **in parallel** (shorter timeouts on-link), then the OS ARP table (Reachable entries only) to catch on-link devices that answer nothing else. Columns: IP, RTT, seen via, MAC, vendor, reverse DNS. |
| **Dante** | Sends an mDNS browse query for `_netaudio-arc._udp.local` at `224.0.0.251:5353`. The query is sent and joined on the **selected NIC** (not the OS default route). Only instances advertised under `_netaudio` are listed, with IPs resolved via SRV → A. Queries ARC and CMC services three times over ~3 s; TXT keys are shown exactly as the device advertises them. If none answer, an offline demo rack is shown in a separate, explicitly labelled section — never mixed with live results. mDNS normally stays on the local L2/VLAN. |
| **OSC** | Listens on UDP 8000 by default (editable). It injects loopback samples for `/gyrosc/gyro` and `/voices/3/osc/14/freq`. Decodes messages and `#bundle`s; malformed datagrams are logged and skipped rather than stopping the listener. Shows live messages/s per address. OSC itself does not require administrator rights. |
| **Load** | Three-second reading: utilisation, Mbps in/out, **multicast+broadcast (flood) Mbps**, packets/s, errors/discards. Any error is flagged; a link below 1 Gbps gets a cabling advisory (gigabit needs all four pairs). |
| **Monitor** | Continuous: each 1 s tick is computed from the previous tick's counters, so there are no gaps. Same metrics as Load plus a rolling utilisation sparkline. |

## Verify a build

Run `ProSight.exe --selftest` on Windows, or `ProSight.app/Contents/MacOS/ProSight --selftest` on a Mac. It checks the LLDP decoder, OSC, range parsing, the mDNS parser (incl. malformed packets) and pcapng adapter attribution against fixed inputs, then reports what this PC's network APIs return (flood counters, ARP, OUI file, admin). Results are shown and saved to `%TEMP%\prosight-selftest.txt`; exit code 0 = all passed.

## Vendor names (optional)

Who's up? shows MAC vendors only from a real registry file — nothing is hard-coded. ProSight looks for, in order:
`oui.csv` next to `ProSight.exe` (download the IEEE MA-L list from https://standards-oui.ieee.org/oui/oui.csv), `manuf` next to the exe, then Wireshark's `manuf`. With none present the Vendor column stays blank.

## Progress

Any operation that makes you wait (Listen, Who's up?, Dante, Load) shows a progress bar with a percentage next to the adapter picker. The host scan reports real probes completed; timed operations advance against their known duration and hold at 99 % until the work actually finishes.

## Important limits

- **Listen** needs a managed switch that emits LLDP/CDP; it cannot invent switch or VLAN details where the switch is silent. `pktmon` is built into supported Windows versions; `tshark` is optional.
- **Who's up?** only lists hosts that answer an ICMP request or one of the TCP probes. Quiet/firewalled hosts can therefore be absent.
- **Load** and **Monitor** are measurements at the laptop NIC on the access port (traffic delivered to this laptop plus multicast/flooding visible there), not the utilisation of a switch uplink.

## Command-line build

```powershell
dotnet build ProSight.sln -c Release
```

The executable is written to `ProSight\bin\Release\net8.0-windows\ProSight.exe`.
