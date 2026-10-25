# Architecture

[Back to README](../README.md) · [Performance evidence](PERFORMANCE.md)

ServerTray is a standalone .NET 10 application. The Windows desktop owns scheduling and local storage; Linux targets only execute fixed read-only commands over SSH. No agent, central server or cloud account is required.

## Components

| Location | Responsibility |
|---|---|
| `src/ServerTray.Core/Configuration.cs` | Versioned settings, validation and atomic persistence. |
| `src/ServerTray.Core/SshMonitorSource.cs` | SSH.NET authentication, independent hop/destination host-key checks, connection reuse and bounded command reads. |
| `src/ServerTray.Core/Metrics.cs` | Linux output parsing, counter deltas, baseline reset and metric snapshots. |
| `src/ServerTray.Core/MonitorEngine.cs` | Staggered collection, concurrency limits, retry/backoff and network/power transitions. |
| `src/ServerTray.Core/HistoryStore.cs` | Minute aggregates, SQLite history, retention and bounded chart queries. |
| `src/ServerTray.Core/Alerts.cs` | Sustained thresholds, hysteresis, expiry, persisted state and event retention. |
| `src/ServerTray.App` | WPF views/view models, tray, Windows DPAPI, single instance, themes and notifications. |
| `tests` | Core and Windows-specific tests. |
| `tools/ServerTray.Bench` | Configuration validation and real SSH protocol/transport checks. |
| `tools/ssh_fixture.py` | Synthetic loopback SSH service for repeatable transport tests. |

`IMonitorSource` separates collection from scheduling and the UI. New sources should implement that interface rather than creating another scheduling or notification loop. UI strings are centralized in `Resources/Strings.resx`.

## Collection flow

The scheduler selects due hosts with jittered/staggered start times and a global concurrency bound. A host has at most one active collection. Expired rounds are skipped instead of accumulating a backlog. Failed transient connections use exponential backoff; authentication and host-key problems require user action. One slow host cannot reserve the whole collector pool.

Each host reuses its SSH connection. Static information is read on connection. A fixed batched command reads Linux counters each round; `df` is included only when root filesystem space is due. No user configuration is interpolated into remote shell code. CPU, disk I/O and interface rates are derived from successive counters without waiting on the server. A reboot, rollback or interface-set change resets the affected baseline.

Successful samples feed history and alert evaluation. The UI receives state changes on the WPF dispatcher. Stale or missing values retain their status and are not represented as zeros. Resume and network transitions reset continuity so a gap cannot count as sustained high load.

## Resource bounds

- Default sampling is 15 seconds, with at most four concurrent collectors; the configuration permits 5–300 seconds and 1–8 collectors.
- Raw samples stay in a bounded in-memory buffer covering at most the latest 20 minutes. Persistent data is aggregated by minute.
- History defaults to seven days, supports 1–30 days, and also caps minute records at one million rows. Chart queries are capped at 1,200 points.
- SSH output is capped at 64 KiB per command. Configuration is capped at 1 MiB; profile and text fields have explicit limits.
- Alerts retain at most seven days and 1,000 records. Rolling logs are bounded to two files of approximately 1 MiB each.
- Lists use virtualization. Charts draw when data/view state changes, and hidden windows stop drawing. WPF software composition and chart rasterization avoid a large GPU-driver allocation in the tray process.

Bounds are design constraints, not evidence of a performance target. The [performance report](PERFORMANCE.md) records actual measurements and incomplete acceptance checks.

## Persistence and trust

Configuration is replaced atomically with one prior backup. Credentials use Windows DPAPI `CurrentUser` and are stored separately from exportable JSON. Trusted fingerprints are also separate. A process running as the same Windows user may access DPAPI secrets; the application does not isolate credentials from other software running under that user.

Unknown or changed SSH host keys stop authentication until the user verifies them. Jump and destination identities are checked independently. The source tree includes only fictional host examples and synthetic test credentials. Generated output, runtime configuration, private keys, databases and logs are excluded from version control.

## Development checks

The Windows CI workflow restores locked NuGet packages, builds/tests, scans source and history, audits dependency vulnerabilities, runs real SSH fixture checks and packages the portable application. Review [CONTRIBUTING.md](../CONTRIBUTING.md) before changing collection, persistence or rendering. An eight-hour soak and physical power/display tests remain separate release gates; CI does not establish those claims.
