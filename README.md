# ServerTray

A lightweight, native Windows tray monitor for Linux servers over SSH. No remote agent, Python installation or central service is required.

**Status: 0.1.0-preview.** The application is usable, but it has not passed every stable-release gate. Short benchmarks are published; an uninterrupted eight-hour soak, high-latency fleet tests and physical multi-display checks remain pending.

[简体中文](README.zh-CN.md) · [User guide](docs/USAGE.md) · [Configuration](docs/CONFIGURATION.md) · [Performance](docs/PERFORMANCE.md) · [Security](SECURITY.md) · [MIT license](LICENSE)

## Features

- Native .NET 10 / WPF tray application with a quick panel, full dashboard and system/light/dark themes.
- Configurable hosts, groups, tags, ordering, password/encrypted private-key authentication and a single SSH jump host.
- CPU, memory, swap, root filesystem, load, disk I/O, network rates, boot counters and uptime.
- Bounded local history, 1h/6h/24h/7d charts, sustained-threshold alerts and recovery notifications.
- Per-host overrides, import preview and credential-free JSON export.
- Explicit host-key verification and Windows DPAPI-encrypted credentials.

The first release targets Windows 11 x64 and 1–20 Linux hosts at a 15-second interval, with four concurrent collectors. Shorter intervals and higher latency change the resource requirements. The current UI is Simplified Chinese; all interface strings are centralized for translation.

## Quick start

1. Download the Windows x64 portable ZIP from [Releases](https://github.com/mengdisheng/ServerTray/releases), or build it using the instructions below.
2. Extract the whole ZIP to a writable folder and run `ServerTray.exe`. No administrator privileges or separate .NET installation are needed.
3. Select **添加服务器** (Add server), enter an SSH address and a dedicated unprivileged account, then save its password or private key.
4. Select **核验主机密钥** (Verify host key) and compare the SHA256 fingerprint through a trusted channel before accepting it. Metrics appear after connection; rates need two successful samples.

The remote host needs Linux `/proc` and standard `sh`, `head`, `cat`, `tail`, `df`, `uname` and `getconf` commands. The collector never uploads files, installs packages or invokes sudo. See the [user guide](docs/USAGE.md) for jump hosts, alerts, interface selection and troubleshooting.

Closing the window keeps monitoring active. Use the tray's exit menu to stop it. Startup at Windows sign-in is opt-in. The portable distribution includes its .NET runtime.

Data defaults to `%LOCALAPPDATA%\ServerTray`. Credentials and trusted fingerprints are separate from exported configuration. History is collected only while this application is running. Network totals are counters from the current server boot, not monthly billing data. No remote service control is included.

To explore the interface with synthetic data and no server connections:

```powershell
.\ServerTray.exe --demo --data-dir "$env:LOCALAPPDATA\ServerTrayDemo"
```

## Build from source

Requirements: Windows 11 x64, Git, Python 3.10+ and the .NET 10 SDK specified in [global.json](global.json). Run from the repository root:

```powershell
git clone https://github.com/mengdisheng/ServerTray.git
cd ServerTray
./tools/build.ps1
```

The script restores locked dependencies, runs tests and the source privacy scan, publishes a self-contained Windows build, and creates dependency notices, a CycloneDX SBOM and a ZIP in `artifacts/release/`. Internet access is needed for the first restore. Exit any application running from that output directory before rebuilding. You can select a private SDK installation with `./tools/build.ps1 -DotNet 'C:\SDK\dotnet.exe'`.

To run from source or repeat focused checks:

```powershell
dotnet run --project src/ServerTray.App -c Release
dotnet test ServerTray.sln -c Release
python tools/scan_public.py --history
dotnet run --project tools/ServerTray.Bench -c Release -- --validate-config examples/config.example.json
```

## Measured performance

The final preview build was measured for 181 seconds per scenario using independent encrypted loopback SSH connections, a 15-second sampling interval and four collectors. CPU is expressed as a percentage of **one logical core**; memory is Windows Private Bytes.

| Scenario | Steady average CPU | Peak private memory |
|---|---:|---:|
| 1 host, tray | 0.40% | 60.0 MiB |
| 5 hosts, tray | 0.59% | 63.8 MiB |
| 20 hosts, tray | 0.60% | 69.1 MiB |
| 20 hosts, dashboard and 7-day chart | 0.71% | 87.4 MiB |

These short tests are not proof of eight-hour stability or a 20-host WAN deployment. See the [full report](docs/PERFORMANCE.md) for hardware, binary hashes, P95 measurements, reproducible commands and remaining release gates.

## Privacy and security

No real host, domain, account, key, database or monitoring configuration is included. There is no product telemetry, cloud upload or automatic update service.

Credentials are encrypted with Windows DPAPI for the current Windows user and are not exported. **Exported JSON still includes addresses, SSH usernames and labels**: review it before sharing. Never upload `%LOCALAPPDATA%\ServerTray`, logs or real configurations to an issue. Read [SECURITY.md](SECURITY.md) before reporting a vulnerability.

## Project documentation

- [User guide](docs/USAGE.md) and [configuration reference](docs/CONFIGURATION.md)
- [Architecture and resource limits](docs/ARCHITECTURE.md)
- [Performance and verification](docs/PERFORMANCE.md), including outstanding acceptance tests
- [Contributing](CONTRIBUTING.md), [security policy](SECURITY.md) and [changelog](CHANGELOG.md)

English is the default documentation language. A [Simplified Chinese README](README.zh-CN.md), [Chinese user guide](docs/USAGE.zh-CN.md) and [Chinese performance report](docs/PERFORMANCE.zh-CN.md) are also available. The application UI currently uses Simplified Chinese; strings live in `Resources/Strings.resx` for future translations.

Licensed under the [MIT license](LICENSE). Packaged dependencies retain their own licenses; each portable build includes `THIRD_PARTY_NOTICES.md`, `sbom.cdx.json` and the applicable license texts.
