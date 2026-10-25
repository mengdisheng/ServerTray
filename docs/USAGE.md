# User guide

[Back to README](../README.md) · [简体中文](USAGE.zh-CN.md)

## Install and start

Extract the complete Windows x64 portable ZIP and run `ServerTray.exe`. The .NET runtime is included. Settings are stored in `%LOCALAPPDATA%\ServerTray`, so the executable does not need to write to its own folder. Windows 11 x64 is the supported target.

The interface currently uses Simplified Chinese. Common actions are **添加服务器** (Add server), **编辑服务器** (Edit server), **设置** (Settings), **导入** (Import), **导出** (Export) and **核验主机密钥** (Verify host key).

## Add an SSH server

Enter a display name, SSH hostname or IP address, port and username. Select password or private-key authentication under SSH settings. An imported private key is encrypted and stored locally; the app does not depend on its original file path. Leaving the password or passphrase empty while editing preserves the saved value.

SSH.NET supports the supported OpenSSH/PEM RSA, Ed25519 and ECDSA key formats. RSA authentication uses SHA-2 signatures. Use a dedicated unprivileged account. The remote system needs readable `/proc` files and standard Linux utilities; no sudo or installed monitoring agent is required.

The first connection stops for host-key verification. Compare its SHA256 fingerprint with the server console or another trusted source, then accept it. A changed fingerprint blocks further connections until you verify it again. A reinstall may legitimately change a key, but the displayed fingerprint alone is not evidence that a change is safe.

For a jump host, create the jump server profile first, save its credentials and verify its key. Select that profile as the destination's jump host. Destination and jump credentials and host keys are checked separately. Only one jump is supported; a jump profile cannot itself use another jump. SSH Agent forwarding is not used.

## Organize servers

Search by name, group or tag. Use the up/down controls in server details to change order. Disable monitoring in the editor to keep a profile without collecting data. A jump host referenced by other profiles cannot be removed until those references are changed.

## Read metrics and history

Collection defaults to 15 seconds with a 10-second timeout and at most four concurrent collectors. Static information is read on connection, and root filesystem space is refreshed every 60 seconds. Include rules select network interfaces before exclude rules remove matches. Wildcards `*` and `?` are supported. Default exclusions remove loopback and common virtual/VPN interfaces; change them if those interfaces carry the traffic you want to measure.

The first sample establishes a baseline. CPU and rates show **—** until a second valid counter sample exists. Restarted hosts, counter rollbacks and network-interface changes reset the baseline. Missing values and stale readings are not zero utilization. Disk I/O sums recognized whole-device counters; it does not measure a particular process or mount point.

Network totals are the counters reported for the selected interfaces in the server's current boot. They are not monthly usage, billing totals or a quota. Monitoring continues only while this Windows application is running.

Charts offer 1 hour, 6 hours, 24 hours and 7 days. History uses UTC internally and the Windows time zone by default; settings allow another Windows time zone. Minute aggregates are kept for 7 days by default, configurable from 1 to 30 days. There is also a one-million-row cap. Missing collection periods remain gaps. Each chart query returns at most 1,200 points.

## Configure alerts

Global rules can be replaced by a server-specific rule set. CPU, memory and root disk alerts each have a separate switch. Defaults are:

| Event | Trigger | Recovery |
|---|---|---|
| CPU | At least 85% for 120 seconds | Below 80% for 60 seconds |
| Memory or root disk | At least 90% for 120 seconds | Below 85% for 60 seconds |
| Offline | No successful sample for more than `max(75 seconds, 3 × interval)` | A successful connection/sample |
| Expiry | Manually set expiry date reaches 30, 7, 1 or 0 remaining days | Each milestone is notified once |

Authentication errors, key verification failures, timeouts and collection errors have distinct statuses. Expiry reminders do not require a working connection. Saved alert state prevents routine duplicate notifications after restart.

A local network outage produces a grouped reminder and suspends per-server notifications. Startup, resume and network recovery allow approximately 60 seconds to collect current state before summarizing it; old notifications are not replayed. Normal events within approximately three seconds are grouped. Gaps reset sustained-resource timers. Notifications depend on Windows notification and focus settings, and events remain available in the app for up to seven days and 1,000 records.

## Import, export and move data

JSON export excludes passwords, private keys, passphrases and trusted-host state. It **does include server addresses, usernames, names, groups and tags**. Review an export before sharing it.

Imports validate the schema and show additions, changes and deletions before replacing the active configuration. Changing a connection identity does not reuse its saved credentials. Moving to another Windows account or computer requires entering credentials and verifying host keys again: DPAPI data is tied to the original Windows user environment.

Configuration writes use an atomic replacement and keep the previous version as `config.json.bak`. If startup rejects a damaged configuration, the original remains on disk; review the backup before restoring it. Close the app before manually replacing files. See the [configuration reference](CONFIGURATION.md).

## Tray and startup

- Single-click the tray icon for the quick panel; double-click it for the main window.
- Closing the main window keeps monitoring active. Right-click the tray and select **退出 ServerTray** (Exit) to stop it.
- Green indicates normal connections, yellow indicates something needs attention, and gray indicates the local network is unavailable.
- Windows sign-in startup is disabled by default. Enable it in settings after choosing a permanent executable location. Moving the portable folder requires updating the startup setting.
- Launching the app again with the same Windows user and data directory brings the existing window forward.

Supported user-facing command-line options:

```powershell
.\ServerTray.exe --minimized
.\ServerTray.exe --data-dir "$env:LOCALAPPDATA\ServerTrayCustom"
.\ServerTray.exe --demo --data-dir "$env:LOCALAPPDATA\ServerTrayDemo"
```

Use a separate directory for demo mode, which generates synthetic metrics without accessing servers. Benchmark/fixture switches are development tools described in [PERFORMANCE.md](PERFORMANCE.md).

## Troubleshooting

| Symptom | What to check |
|---|---|
| No window after closing it | Open the tray icon, or launch the executable again. |
| Waiting for key verification | Compare and accept the key through the dedicated verification action. |
| Authentication failed | Check username, authentication type, saved secret, key passphrase and server SSH policy; save corrected credentials and retry. |
| No rate yet | Wait for two successful samples; a reboot or interface change restarts this calculation. |
| Network rate seems too low | Review interface filters, especially VPN and bridge exclusions. |
| No desktop notification | Check Windows notifications/focus settings and the app's event list; current-state summaries wait about 60 seconds. |
| Import rejected | Check schema version, unknown fields, allowed ranges and jump references. The example uses a disabled fictional host. |

Preview limitations and pending hardware tests are listed in the [performance report](PERFORMANCE.md). When reporting an issue, provide the application version and steps to reproduce with fictional data. Do not upload your data directory, credentials or unredacted logs.
