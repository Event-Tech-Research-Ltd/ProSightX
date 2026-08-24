# PortSight 2 for Visual Studio 2022

PortSight is a Windows .NET 8 WinForms tool for checking a normal laptop RJ45 connection using ordinary Cat5/6 patch leads. It helps identify a managed switch's port/VLAN information and gives an honest, laptop-side view of the connected network. No special lab or probe cables are required.

## Open and run

1. Install Visual Studio 2022 with **Desktop development with .NET** and the .NET 8 SDK.
2. Open `PortSight.sln`.
3. Press **F5** and accept the single Windows UAC prompt. The app starts elevated, so `Listen` can use pktmon/tshark without triggering a second elevation prompt.

There are no third-party NuGet packages.

## Tabs

| Tab | What it does |
|---|---|
| **Listen** | Select an Ethernet NIC and a duration. **Built-in Cisco sample** decodes LLDP offline. **Listen live** captures LLDP/CDP with Wireshark `tshark` if installed, or Windows `pktmon`; it displays the switch name, port ID, VLANs, chassis and management details when advertised. A managed switch must actually advertise LLDP/CDP. |
| **Who's up?** | Scans the entered CIDR (default is the selected NIC's `/24`) or a short range such as `10.0.0.1-50`. It runs parallel ICMP probes, then TCP probes on 80/443/22/445/3389/8080. Only confirmed live hosts appear, with IP, RTT, method and reverse-DNS hostname when available. This is deliberately subnet/range scoped, not an Internet scanner. |
| **Dante** | Sends an mDNS browse query for `_netaudio-arc._udp.local` at `224.0.0.251:5353`. If there are no replies, it displays a clearly-labelled FOH lab sample rack so the UI is usable offline. mDNS normally stays on the local L2/VLAN. |
| **OSC** | Listens on UDP 8000 by default (editable). It injects loopback samples for `/gyrosc/gyro` and `/voices/3/osc/14/freq`. OSC itself does not require administrator rights. |
| **Load** | Takes one three-second sample: utilisation, inbound/outbound Mbps, packets/sec, multicast %, errors/discards and a simple verdict. |
| **Monitor** | Starts/stops an approximately one-second continuous NIC reading with a rolling utilisation sparkline and the same live metrics. |

## Important limits

- **Listen** needs a managed switch that emits LLDP/CDP; it cannot invent switch or VLAN details where the switch is silent. `pktmon` is built into supported Windows versions; `tshark` is optional.
- **Who's up?** only lists hosts that answer an ICMP request or one of the TCP probes. Quiet/firewalled hosts can therefore be absent.
- **Load** and **Monitor** are measurements at the laptop NIC on the access port (traffic delivered to this laptop plus multicast/flooding visible there), not the utilisation of a switch uplink.

## Command-line build

```powershell
dotnet build PortSight.sln -c Release
```

The executable is written to `PortSight\bin\Release\net8.0-windows\PortSight.exe`.
