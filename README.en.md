# ServerTray

A lightweight, native Windows tray monitor for Linux servers over SSH. No remote agent, Python installation or central service is required.

This delivery is **0.1.0-preview**, a release candidate. Short benchmarks are recorded; the final-binary eight-hour soak and physical multi-display verification are still pending. It is not yet signed off against every release gate.

[简体中文](README.md) · [Performance and validation](docs/PERFORMANCE.md) · [Security](SECURITY.md) · [MIT license](LICENSE)

## Features

- Native .NET 10 / WPF tray application with a quick panel, full dashboard and system/light/dark themes.
- Configurable hosts, groups, tags, ordering, password/encrypted private-key authentication and a single SSH jump host.
- CPU, memory, swap, root filesystem, load, disk I/O, network rates, boot counters and uptime.
- Bounded local history, 1h/6h/24h/7d charts, sustained-threshold alerts and recovery notifications.
- Per-host overrides, import preview and credential-free JSON export.
- Explicit host-key verification and Windows DPAPI-encrypted credentials.

The first release targets Windows 11 x64 and 1–20 Linux hosts at a 15-second interval, with four concurrent collectors. Shorter intervals and higher latency change the resource requirements. The current UI is Simplified Chinese; all interface strings are centralized for translation.

## Use

Extract the portable ZIP and run `ServerTray.exe`. Add a server, then verify the SHA256 host-key fingerprint through a trusted channel before accepting it. A dedicated unprivileged SSH account is recommended. The fixed collector reads Linux `/proc` and runs standard `sh`, `head`, `cat`, `tail`, `df`, `uname` and `getconf` commands. It never uploads scripts, installs packages or invokes sudo.

Closing the window keeps monitoring active. Use the tray's exit menu to stop it. Startup at Windows sign-in is opt-in. The portable distribution includes its .NET runtime.

Data defaults to `%LOCALAPPDATA%\ServerTray`. Credentials and trusted fingerprints are separate from exported configuration. History is collected only while this application is running. Network totals are counters from the current server boot, not monthly billing data. No remote service control is included.

## Build and validate

On Windows with Python 3.10+ and the SDK specified in `global.json`:

```powershell
./tools/build.ps1
dotnet test ServerTray.sln -c Release
```

Portable files, dependency notices, SBOM and ZIP output go to `artifacts/release/`. Package versions are locked. See [performance documentation](docs/PERFORMANCE.md) for real SSH fixtures, measurement definitions and the eight-hour soak procedure. A soak test is only marked complete after the full duration with sufficient active measurement coverage.

No real host, domain, account, key, database or monitoring configuration is included. There is no product telemetry, cloud upload or automatic update service.
