# Wi-Fi Reporter

A small Windows service that reports the Wi-Fi quality and the hardware of a PC to **Home Assistant**
over **MQTT**.

Every minute it publishes:

| Sensor | Meaning |
|---|---|
| Signal | RSSI in dBm |
| Quality | Windows signal quality, 0–100 % |
| RX / TX rate | current link rate in Mbit/s |
| Band, Standard | e.g. `5 GHz`, `Wi-Fi 6 (ax)` |
| SSID | network name |
| Access point | BSSID, tells you which router or AiMesh node the PC is on |

Every 30 minutes it also runs a short **iperf3** test against a server on the LAN and publishes the real
LAN **Download / Upload** throughput.

It also reports the **system** (since 0.3.0):

| Sensor | Meaning | While the PC is off |
|---|---|---|
| CPU, Motherboard, Model, RAM, GPU, Wi-Fi adapter, Disks, Windows | hardware inventory, details as attributes (cores, BIOS, RAM modules and speed, VRAM and driver, Wi-Fi driver version/date, disk sizes/bus/SSD-HDD, build) | **last known value** |
| CPU load, RAM used | % (CPU load averaged over the report interval) | unavailable |
| Disk C:, Disk D:, … | used % per fixed volume, free/total GB as attributes | unavailable |
| Boot time | last full start (with Fast Startup a shutdown + power-on does not count, same as Task Manager) | unavailable |
| Online | on / off, HA shows since when | **off** |

The inventory is read on every MQTT connect (so after a reboot an upgrade shows up) and published
retained, without availability: Home Assistant keeps showing it while the PC is off. Everything is read
without WMI or drivers: registry, SMBIOS, SetupAPI and storage IOCTLs. USB drives, SD cards and virtual
disks (WSL/Docker VHDX) are left out of *Disks*. A volume that appears later gets its sensor at the next
reconnect / restart.

Sensors appear in Home Assistant automatically (MQTT discovery) as a device called **"&lt;PC name&gt;"**, so
entities read e.g. "DESKTOP Signal". The channel and the upload retransmits are still in the MQTT payloads
(see below) but have no sensor of their own.

* Single `.exe`, no runtime to install, **no third-party dependencies** (own minimal MQTT 3.1.1 client,
  native Service Control Manager API, native Wi-Fi API).
* Reads Wi-Fi through `wlanapi.dll`, so it works the same on any Windows display language.

> **Status:** verified on Windows 11: Wi-Fi reading, MQTT / Home Assistant, iperf3 and the
> Windows service running as LocalSystem (it gets SSID/BSSID/rates, no location restriction).
> See [CLAUDE.md](CLAUDE.md) for the detailed state and open questions.

## Install / deploy to a PC

The release zip contains everything except the MQTT credentials: `WifiReporter.exe`, `install.cmd` /
`install.ps1`, `config.example.json` and iperf3 (`iperf3.exe` + `cygwin1.dll`, the ar51an build).

1. Get the zip: **Releases** (push a tag `v*`), the latest **Actions** run, or build it yourself
   (see [Build](#build)).
2. *(new PC, optional)* Put a filled-in `config.json` (MQTT credentials) next to `install.cmd`. Without
   it the script asks for the broker host (Enter = `homeassistant.local`) and the MQTT username and
   password. An update keeps the PC's existing config, so
   nothing is asked then. With the config in the folder, the same folder (USB stick, network share)
   installs every PC without typing. Without iperf3 in the folder (own build) the script installs it with
   `winget install ar51an.iPerf3`; if that is not possible, the test is skipped.
3. Double-click **`install.cmd`** (it asks for admin rights). It
   * copies the exe and iperf3 to `C:\Program Files\WifiReporter`,
   * creates `%ProgramData%\WifiReporter\config.json` if missing (an existing config is kept),
   * installs and starts the **Wi-Fi Reporter** service (automatic, delayed start, restarts on failure),
   * waits for the first report and prints SSID, access point and signal, or what went wrong.

Options (from a terminal in that folder):

```powershell
.\install.cmd -DeviceName NOTEBOOK                         # name in Home Assistant (default: computer name)
.\install.cmd -MqttUsername wifireporter -MqttPassword ... # set / change the credentials
.\install.cmd -MqttHost 192.168.1.10                       # set / change the broker (and iperf3 server)
.\install.cmd -Uninstall                                   # remove service and files (config + logs stay)
```

**Update** to a new version: run `install.cmd` from the new zip; it stops the service, replaces the exe and
starts it again, keeping the config.

Manual install without the script: copy the files to `C:\Program Files\WifiReporter`, then from an admin
terminal there `.\WifiReporter.exe install`, edit `%ProgramData%\WifiReporter\config.json` and
`Restart-Service WifiReporter`. Uninstall: `.\WifiReporter.exe uninstall`.

## Configuration

`%ProgramData%\WifiReporter\config.json`

```json
{
  "mqttHost": "homeassistant.local",
  "mqttPort": 1883,
  "mqttUsername": "",
  "mqttPassword": "",
  "deviceName": "",
  "discoveryPrefix": "homeassistant",
  "intervalSeconds": 60,
  "iperfEnabled": true,
  "iperfPath": "iperf3.exe",
  "iperfServer": "homeassistant.local",
  "iperfIntervalSeconds": 1800,
  "iperfDurationSeconds": 5
}
```

| Key | Meaning |
|---|---|
| `deviceName` | Name in Home Assistant. Empty = computer name. |
| `intervalSeconds` | How often the Wi-Fi state is published (min 10). If the broker is unreachable (e.g. a notebook away from home), the reconnect backs off up to this interval and logs only the first failure. |
| `iperfPath` | `iperf3.exe` on `PATH` or next to the exe, or a full path. |
| `iperfServer` | Host running `iperf3 -s` (e.g. the Home Assistant Pi). |
| `iperfIntervalSeconds` | How often the throughput test runs (min 60), on its own schedule. The first test runs right after (re)start. If the server is busy (another PC testing), it is retried 1–5 minutes later. |

## Commands

```
WifiReporter.exe console          run in the terminal with live log (Ctrl+C to stop)
WifiReporter.exe once             print the Wi-Fi state, inventory and system stats once (no MQTT)
WifiReporter.exe demo             publish fake data to MQTT (tests the Home Assistant side)
WifiReporter.exe install          install + start the Windows service           (admin)
WifiReporter.exe uninstall        stop + remove the Windows service             (admin)
WifiReporter.exe install-task     run at logon in your user session instead      (admin)
WifiReporter.exe uninstall-task   remove the logon task                          (admin)
```

All commands accept `--config <path>`. Logs: `%ProgramData%\WifiReporter\logs` (kept 7 days).

A good first check before installing: `.\WifiReporter.exe once`.

## Access denied / location

Since Windows 11 24H2, Windows hides Wi-Fi connection details (SSID, BSSID, link rates) from apps that
don't have **location** access. Signal and channel still work.

If `once` or the log says the details are restricted:

1. **Settings → Privacy & security → Location**: turn on **Location services** and
   **Let desktop apps access your location**.
2. If the **service** is still restricted after that, use the logon task instead, which runs in your
   user session where the location consent applies:

   ```powershell
   .\WifiReporter.exe uninstall
   .\WifiReporter.exe install-task
   ```

The JSON state contains `"restricted": true` while this is the case.

## iperf3 server

Any machine on the LAN running `iperf3 -s`. On Home Assistant OS, the *Advanced SSH & Web Terminal*
add-on with this config works well:

```yaml
packages:
  - iperf3
init_commands:
  - iperf3 -s -D
```

## MQTT topics

```
wifi_reporter/<device>/state          {"rssi":-62,"signal_quality":81,"rx_rate":1729,"tx_rate":865,"channel":100,
                                       "band":"5 GHz","phy":"Wi-Fi 6 (ax)","ssid":"HomeWiFi","bssid":"aa:..","adapter":"..",
                                       "restricted":false}   (retained; all null without a Wi-Fi connection)
wifi_reporter/<device>/iperf          {"iperf_download":547.2,"iperf_upload":314.0,"iperf_upload_retransmits":0}   (retained)
wifi_reporter/<device>/system         {"cpu":"Intel(R) Core(TM) i7-14700KF","cpu_info":{"cores":20,..},"board":"..",
                                       "model":"..","ram":"128 GB DDR5","ram_info":{..},"gpu":"..","gpu_info":{..},
                                       "wifi":"..","wifi_info":{"adapters":[..]},"disks":"..","disks_info":{..},
                                       "os":"Windows 11 Pro 26H2 (26300.9550)",
                                       "os_info":{..}}   (retained)
wifi_reporter/<device>/stats          {"cpu_load":3.3,"ram_used":17.1,"ram_used_gb":21.9,"ram_total_gb":127.8,
                                       "boot_time":"2026-10-09T01:35:53+00:00",
                                       "volumes":{"c":{"used":82.9,"free_gb":163.2,"total_gb":952.9,..}}}   (retained)
wifi_reporter/<device>/availability   online / offline (retained, last will)
homeassistant/sensor/wifi_reporter_<device>/<key>/config   discovery (retained)
homeassistant/binary_sensor/wifi_reporter_<device>/online/config
```

Everything is retained, so Home Assistant has all values right after its own restart; while a PC is off,
the availability topic (`offline`) makes the live sensors unavailable anyway.

## Build

Requires the .NET 8 SDK.

```powershell
dotnet build src/WifiReporter
dotnet run --project src/WifiReporter -- once                      # read Wi-Fi once
dotnet run --project src/WifiReporter -- console --config .\config.json
dotnet publish src/WifiReporter -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

`config.json` in the repo root is git-ignored: copy `config.example.json` and fill in MQTT credentials.

On non-Windows systems there is no Wi-Fi API, so `console` / `demo` publish fake data; useful for
testing the MQTT side.

GitHub Actions builds the zip on every push; pushing a tag `v*` creates a release.

For a local deploy folder, copy `publish\WifiReporter.exe`, `publish\config.example.json` and
`deploy\install.*` into one folder, plus `iperf3.exe`, `cygwin1.dll` and a filled-in `config.json`.

## Home Assistant tips

* **Ready-made dashboard** in [`ha/`](ha): a Wi-Fi table of all PCs (online, AP name, band, signal, link,
  iperf download/upload; an offline PC shows only its name, in grey) with download/signal graphs, and a *Hardware* view with one card
  per PC that stays filled while the PC is off. The table and the hardware cards find all wifi-reporter
  devices on their own; only the graphs have a fixed entity list, and the table names your access points
  by BSSID. Put both into `ha/local.json` (git-ignored; shape as `LOCAL_EXAMPLE` in `build_dashboard.py`),
  run `python ha/build_dashboard.py` and paste the parts of `ha/dashboard.local.yaml` (without local.json:
  `ha/dashboard.yaml`, with example values) into a sections dashboard (raw configuration editor):
  `computers_section` into a view's `sections`, `hardware_view` into `views`. Only built-in cards are used.
  A device renamed in HA ("Notebook" instead of the computer name) shows under its new name.
* Entities are named like `sensor.<pc>_signal`, `sensor.<pc>_download` (PCs set up before v0.2.0 keep their
  older ids such as `sensor.<pc>_wi_fi_wi_fi_signal`). Rename them in HA as you like; the `unique_id`s stay
  stable.
* HA always prefixes the friendly name with the device ("DESKTOP Signal"). For a card that shows just the
  metric names, use an Entities card with explicit names, one per PC:

  ```yaml
  type: entities
  title: NOTEBOOK
  entities:
    - type: section
      label: Connection
    - { entity: sensor.notebook_ssid, name: SSID }
    - { entity: sensor.notebook_access_point, name: Access point }
    - { entity: sensor.notebook_band, name: Band }
    - { entity: sensor.notebook_standard, name: Standard }
    - type: section
      label: Signal
    - { entity: sensor.notebook_signal, name: Signal }
    - { entity: sensor.notebook_quality, name: Quality }
    - type: section
      label: Link rate
    - { entity: sensor.notebook_rx_rate, name: RX rate }
    - { entity: sensor.notebook_tx_rate, name: TX rate }
    - type: section
      label: Real throughput (iperf3)
    - { entity: sensor.notebook_download, name: Download }
    - { entity: sensor.notebook_upload, name: Upload }
  ```
* The **Access point** sensor shows the BSSID. To see "router" vs "node", add a template sensor
  mapping your BSSIDs to names.
* Useful alerts: band changed to 2.4 GHz, signal below −72 dBm for 10 minutes, LAN download below a
  threshold.
