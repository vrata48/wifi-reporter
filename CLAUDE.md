# CLAUDE.md

Context for Claude Code working on this repo.

## What this is

**Wi-Fi Reporter**: a Windows service (.NET 8, C#) that reports the Wi-Fi quality of a PC to Home Assistant
over MQTT, plus (since 0.3.0) its hardware inventory and basic system metrics. It replaces an earlier
Python prototype. The owner runs it on several home PCs to monitor Wi-Fi quality per device and per access
point (main router vs. mesh node) over time.

The repo is **public**. Personal details (PC names, home network, IPs, BSSIDs) live only in the git-ignored
`CLAUDE.local.md`, `config.json` and `ha/local.json`; never put them into tracked files or commit messages.

## Status (2026-10-11)

Originally written in a cloud sandbox without Windows; since verified on the owner's desktop
(Windows 11 Pro build 26300, Qualcomm FastConnect 7800, .NET 10 SDK building the net8.0 target).
GitHub: public repo `vrata48/wifi-reporter`.

| Area | State |
|---|---|
| MQTT client, HA discovery, reconnect, last will, iperf parsing | **Tested** on Linux against mosquitto 2.0, and against the real HA Mosquitto add-on from the desktop |
| Reading Wi-Fi (`once`) | **Verified** on the desktop: SSID, BSSID, rates, RSSI, channel |
| `console` with real MQTT → sensors in HA | **Verified** |
| Windows service (`install`, LocalSystem, `C:\Program Files\WifiReporter`) | **Verified**: runs, gets SSID/BSSID/rates (`restricted: false`), iperf3 works |
| `deploy/install.ps1` (update, uninstall, fresh install from bundled config.json) | **Verified** on the desktop from an elevated session. Self-elevation, password prompt and winget fallback not run yet |
| `install-task` / `uninstall-task` | Not run yet (not needed on the desktop) |
| GitHub Actions workflow | **Verified**: builds `WifiReporter-win-x64.zip` |
| System info 0.3.0 (inventory, stats, Online) | **Verified** on the desktop as service + `once` unelevated, and on a Dell notebook (hybrid GPU); in HA the inventory keeps its values and stats go unavailable when the service is stopped. Not tried on a Lenovo yet |
| 0.3.1 (Wi-Fi adapter in the inventory, dead-connection detection, retained state) | **Verified** on the desktop (service) |

Deployed on four PCs, all via `install.cmd`, none restricted. Not verified yet: console output printed
directly to an interactive terminal (redirected output works).

## Known open questions / risks

- **Location privacy (Windows 11 24H2+)**: `WlanQueryInterface(wlan_intf_opcode_current_connection)`
  returns `ERROR_ACCESS_DENIED` without location consent. On the desktop the LocalSystem service is **not**
  restricted, but this may depend on the machine's location settings, so check each new PC
  (`install.ps1` prints a warning when `restricted` is true). The code degrades gracefully
  (`Restricted = true`, still reports RSSI and channel) and `install-task` exists as a fallback.
- **iperf3** (ar51an build from winget) needs `cygwin1.dll` next to `iperf3.exe`; the service cannot use a
  per-user winget path reliably, so both are copied next to `WifiReporter.exe`.
- **Band detection** is a heuristic from the channel number (1–14 → 2.4 GHz). 6 GHz channel numbers
  overlap and would be misreported; irrelevant for the current home network (no 6 GHz), but a proper fix
  would use the channel frequency (e.g. `WlanGetNetworkBssList` for the connected BSSID, also
  location-gated).
- `AttachConsole`/`AllocConsole` with `OutputType=WinExe`: when stdout is redirected (pipe/file) the app
  does not attach (attaching used to swallow the output). Direct terminal output is untested; the prompt
  may print before the output. PowerShell does not wait for a WinExe unless its output is piped.
- `Installer` with a path containing spaces (`C:\Program Files\WifiReporter`) works with `sc.exe`;
  `schtasks.exe` not tried yet.

## Architecture

```
src/WifiReporter/
  Program.cs              entry point, command dispatch (no args = started by SCM)
  Config.cs               config.json (camelCase), %ProgramData%\WifiReporter\config.json
  Log.cs                  daily log files in %ProgramData%\WifiReporter\logs, 7 days
  Reporter.cs             main loop: read Wi-Fi + system stats → publish; inventory on connect; periodic iperf;
                          MQTT reconnect w/ backoff
  Iperf.cs                runs iperf3 -J (download with -R, then upload), parses JSON
  Wlan/WlanNative.cs      P/Invoke for wlanapi.dll
  Wlan/WlanReader.cs      current connection, RSSI (opcode 0x10000102), channel (opcode 8)
  Hardware/SystemModels.cs     ISystemMonitor, SystemInventory / SystemStats records + their JSON
  Hardware/SystemInfoReader.cs inventory: CPU/OS from registry, board/BIOS/RAM from SMBIOS, GPU + Wi-Fi adapter
                               (Net class, *IfType 71) via SetupAPI,
                               disks via IOCTL on \\.\PhysicalDriveN (access 0, no admin needed)
  Hardware/Smbios.cs           raw SMBIOS table parser (GetSystemFirmwareTable 'RSMB')
  Hardware/WindowsSystemMonitor.cs  live stats: CPU load (GetSystemTimes delta), RAM, fixed volumes, boot time
  Mqtt/MiniMqttClient.cs  MQTT 3.1.1 subset: CONNECT (auth + will), PUBLISH QoS0, PINGREQ, DISCONNECT; a connection
                          without PINGRESP for 1.5x keep-alive counts as lost (the main loop checks every 5 s)
  HomeAssistant/Discovery.cs  discovery payloads + topic names
  Service/ServiceHost.cs  native SCM integration (StartServiceCtrlDispatcherW etc.)
  Service/Installer.cs    sc.exe / schtasks.exe wrappers
deploy/
  install.ps1 / .cmd      deploy/update/uninstall on a PC: Program Files, iperf3, config, service, check
ha/
  computers.jinja         markdown-card template: Wi-Fi table of all wifi-reporter devices + BSSID → AP names
  hardware.jinja          markdown-card template: hardware card for the N-th PC (__INDEX__), empty if none
  build_dashboard.py      → dashboard.yaml (Home section + Hardware view) with example AP names / graph PCs,
                          plus dashboard.local.yaml from the git-ignored local.json (the real ones)
```

HA device name = PC name, sensor names short ("Signal", "Download"), because HA shows
"<device> <sensor>". Channel and upload retransmits have no sensor (removed in 0.2.0 by publishing an
empty retained discovery config) but stay in the MQTT payloads.

Topics: `wifi_reporter/<device>/{state,iperf,system,stats,availability}`, discovery under
`homeassistant/sensor/wifi_reporter_<device>/<key>/config` (plus `binary_sensor/.../online`). `<device>` is the
slugified device name. All topics are retained (since 0.3.1; before that an HA restart left the Wi-Fi
sensors unavailable until the next report). `system` (inventory) sensors have no availability/expire_after, so
HA shows the last known hardware while the PC is off; `stats` sensors behave like the Wi-Fi ones. Without a
Wi-Fi connection the state is published with all fields null, so HA shows "unknown" (dashboard: "LAN"). Inventory
sensors carry details as attributes via `json_attributes_template` on a `<key>_info` object. Stats are
published every interval even without Wi-Fi (Ethernet PCs). No LibreHardwareMonitor: temperatures/fans/GPU
load would need it plus the PawnIO driver (WinRing0 is flagged by Defender); deliberately left out for now,
and never on a work notebook.

## Conventions / constraints

- **No NuGet dependencies** was a sandbox constraint, not a hard requirement. Now that the project is on
  a normal machine, swapping in `Microsoft.Extensions.Hosting.WindowsServices` and/or `MQTTnet` is
  acceptable if it clearly simplifies things. Ask the owner first; he likes the single small exe.
- Target: Windows 10/11 x64, .NET 8, published self-contained single file.
- Keep the MQTT payload keys and discovery `unique_id`s stable: HA entities and dashboards depend on them.
- The owner develops AI-first (Claude Code); prefer complete, working changes over partial edits.

## Build & run

```powershell
dotnet build src/WifiReporter
dotnet run --project src/WifiReporter -- once
dotnet run --project src/WifiReporter -- console --config .\config.json
dotnet publish src/WifiReporter -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

`config.json` is git-ignored; copy `config.example.json` and fill in MQTT credentials.

On Linux (no Wi-Fi API) `console` falls back to demo data, which is handy for testing the MQTT side:
`dotnet run --project src/WifiReporter -- demo --config ./config.json`.

## Possible next steps

- Alerts / notifications (dropped to 2.4 GHz, RSSI below −72 dBm, iperf below a threshold). The dashboard
  only colors these.
- Optional tray icon or `status` command showing the last published values.
