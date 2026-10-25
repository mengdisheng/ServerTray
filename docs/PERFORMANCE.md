# Performance and validation

[Back to README](../README.md) · [简体中文](PERFORMANCE.zh-CN.md)

**Status: 0.1.0-preview; stable-release acceptance is incomplete.** Short tests do not establish eight-hour stability, and 20 loopback SSH connections do not represent 20 independent Linux hosts across a WAN.

## Environment and definitions

Measurements were recorded on 2026-10-24:

- Windows 11 Pro x64, build 26300; Intel Core i5-14600KF, 20 logical processors, 31.8 GiB RAM; Samsung 990 EVO Plus SSD.
- Display environment: NVIDIA RTX 5070 and a GameViewer virtual display adapter. The candidate uses WPF software composition and rasterized charts to avoid loading a large graphics driver into the tray process.
- .NET SDK 10.0.402; self-contained .NET 10.0.13 runtime; Release, win-x64, ReadyToRun publish.
- Paramiko 5.1.0 loopback-only SSH fixture. Each configured server establishes an independent persistent encrypted connection. Sampling is 15 seconds, with at most four collectors.
- CPU is `process CPU-time delta / wall-time delta × 100`, expressed as a percentage of **one logical core**, without dividing by the number of processors.
- Memory is Windows Private Bytes divided by 2²⁰ (MiB), not working set. Peaks include warmup. Short tests warm up for 60 seconds; the eight-hour procedure warms up for 30 minutes.
- Samples every five seconds record CPU, private memory, handles, threads, online hosts, SSH connections, collection-duration P95, scheduling-delay P95, sample-age P95 and draw count. JSON reports are atomically updated every 30 seconds.
- Collection duration includes connection/static-info work when due. Sample age includes the sampling interval and staggered scheduling; it is not network RTT. Summary P95 values in `measurements/summary.json` are percentiles of periodic P95 snapshots, not a pooled percentile of every collection.
- Startup timing runs from process creation to WPF `ContentRendered`; interaction is checked separately. A minimized process does not produce a main-window startup measurement.

## Final preview measurements

The distributed executable was measured for 181 seconds per scenario, using separate processes and data directories. No UI Automation probing was attached during these tests. An additional soak ran concurrently; it did not complete the required eight hours. Binary SHA256 values, raw five-second series and summaries are in [measurements/summary.json](measurements/summary.json) and its neighboring files.

| Scenario | Steady average CPU, one core | Peak private memory | SSH connections at completion |
|---|---:|---:|---:|
| 1 host, tray | 0.40% | 60.0 MiB | 1 |
| 5 hosts, tray | 0.59% | 63.8 MiB | 5 |
| 20 hosts, tray | 0.60% | 69.1 MiB | 20 |
| 20 hosts, dashboard and dense 7-day chart | 0.71% | 87.4 MiB | 20 |

The final preview's repeated main-window launch measured **773 ms**. It followed earlier UI checks and is not a Windows-reboot/cache-cold launch. The software-rendering version's first-run baseline was **1.42 seconds**. An earlier GPU-composed build took approximately 3.2 seconds on first launch and 0.7 seconds when warm, which motivated the rendering change.

For the chart workload, SQLite was seeded with 20 × 10,080 synthetic minute records. Each seven-day curve returned approximately 1,008 points. UI Automation probing itself previously added roughly 20–30% of a single core. That diagnostic run was excluded from ordinary performance claims; repeat measurements selected the chart range through a launch option without attaching a probe.

## Development baselines

These earlier builds are retained as context; they are not substituted for final-binary acceptance:

| Scenario | Duration | Steady average CPU | Peak private memory |
|---|---:|---:|---:|
| 1 host, tray | 181 s | 0.50% | 50.3 MiB |
| 5 hosts, tray | 181 s | 0.54% | 54.0 MiB |
| 20 hosts, tray | 301 s | 0.35% | 65.5 MiB |
| 20 hosts, dense 7-day chart, GPU comparison | 241 s | 0.81% | 123.4 MiB |

A 361-second all-disconnect/recovery baseline used software rendering before the final theme adjustment. The proxy disconnected at 60 seconds and returned at 150 seconds. Online hosts fell from 20 to zero, reached 16 around 175 seconds and then returned to all 20. Whole-run average CPU was 0.56% of one core; peak private memory was 71.1 MiB. Backoff avoided reconnecting every second.

The attempted final-binary eight-hour run was interrupted before eight hours. Its local report remains `complete: false`; no eight-hour pass is claimed. The checked-in summary retains an explicitly incomplete progress snapshot. Local generated reports under `artifacts/` are not published as application data.

## Remote collection and transport

The fixed collector ran 20 times as an ordinary user on one Linux host, including root filesystem reads. No component was installed, no file was uploaded and sudo was not used. Only an anonymous aggregate is published.

| Metric | Measurement |
|---|---:|
| Command length | 332 bytes |
| Mean remote CPU time / P95 | 15.5 ms / 20 ms |
| Mean single-core utilization at a 15-second interval | 0.1033% |
| Peak RSS | 1.75 MiB |
| Maximum raw output | 3,332 bytes |
| Completion duration including network, P95 | 1,828 ms |

A loopback TCP proxy measured **encrypted bidirectional TCP payload**, excluding the initial handshake, over five 15-second rounds with keepalives included: 1,456, 1,424, 1,360, 1,424 and 1,456 bytes. The maximum was below 16 KiB. IP/TCP headers, retransmissions and link-layer overhead are not included. This does not bound every host's `/proc` output: large interface/device sets produce more data, and command output above 64 KiB stops that collection.

## Release gates

| Requirement | Evidence / remaining work |
|---|---|
| 20 hosts, tray CPU ≤2% of one core; private memory ≤120 MiB | Final preview short test: 0.60%, 69.1 MiB. |
| Dashboard/history private memory ≤200 MiB | Final preview short test: 87.4 MiB. |
| Eight hours, post-warmup private-memory growth ≤15 MiB and no sustained growth | **Pending.** The interrupted run is insufficient. |
| SSD main window interactive within two seconds | Software first-run baseline 1.42 s; final repeated launch 0.773 s; basic interaction checked. Broader cold-start validation remains advisable. |
| Remote collection CPU ≤0.5% of one core | Passed for the single Linux sample above, not every distribution/hardware combination. |
| Per-round bidirectional traffic ≤16 KiB | Loopback encrypted payload passed; real packet-level overhead and target variation need separate validation. |
| 20 slow hosts, all disconnected, recovery | Single-command timeout protocol check passed; 20-host outage/recovery baseline passed; high-latency 20-host WAN validation **pending**. |
| Multi-monitor, high DPI, real sleep/resume | PerMonitorV2 declared; resume state-machine tests passed; physical acceptance **pending**. |
| Windows notification activation | Implemented; notification center, focus settings and old-notification activation acceptance **pending**. |

Incomplete or failed gates must be completed or fixed before a stable release. A preview tag is not a claim that every gate has passed.

## Functional verification

- **33 core tests** passed: configuration defaults/future-schema refusal, atomic backups, import differences, reboot/rollback/interface changes, late samples, minute history/gaps, alert durations/deduplication, independent resource switches, expiry while offline, concurrency, authentication retry blocking, cancellation and resume grace.
- **4 Windows tests** passed: DPAPI encryption/decryption/removal, host trust separated by address/port, and Chinese resource embedding.
- **8 real SSH protocol checks** passed: password and encrypted-key authentication, unknown/changed host keys, authentication failure, output caps, command timeout, one jump and connection reuse.
- Main window, light/dark themes and seven-day curves were visually checked. Closing the final preview window kept its process alive. Relaunching with the same data directory exited the second process and restored the first window.

Windows CI restores locked packages, builds/tests, scans source/history, audits vulnerable dependencies, runs SSH fixture checks and creates notices and portable ZIPs. An unavailable vulnerability database is a failed audit, not a pass. Local results above do not preempt the actual hosted CI result.

## Reproduce

Build on Windows with the SDK in `global.json` and Python 3.10+:

```powershell
./tools/build.ps1
python -m pip install -r tools/requirements-test.txt
New-Item -ItemType Directory -Force artifacts/fixtures
python tools/ssh_fixture.py --receipt artifacts/fixtures/ssh.json --client-key artifacts/fixtures/client-key.pem
```

Leave that terminal running. In another terminal at the repository root:

```powershell
dotnet run --project tools/ServerTray.Bench -c Release -- --receipt artifacts/fixtures/ssh.json --self-test --private-key artifacts/fixtures/client-key.pem
./tools/benchmark.ps1 -Servers 1 -Seconds 180
# Wait for each run to finish before starting the next.
./tools/benchmark.ps1 -Servers 5 -Seconds 180
./tools/benchmark.ps1 -Servers 20 -Seconds 300
./tools/benchmark.ps1 -Servers 20 -Seconds 28800
```

For the complete matrix, repeat with `-Visible` and an appropriate chart workload. Do not attach UI Automation or a diagnostic probe during measurements. Keep the Windows host awake. The eight-hour report requires `complete: true`, `activeCoverage >= 0.98`, a full 28,800-second duration and review of the post-warmup series. Compare growth after warmup and inspect the trend; a favorable endpoint difference alone does not establish stability. Early exit, insufficient records or sleep-induced coverage loss is not a pass.

Transport/outage checks:

```powershell
python tools/transport_probe.py --fixture artifacts/fixtures/ssh.json --receipt artifacts/fixtures/proxy.json --control artifacts/fixtures/control.json
# Use another terminal; only the client under test should use this proxy.
dotnet run --project tools/ServerTray.Bench -c Release -- --receipt artifacts/fixtures/proxy.json --wire-check
./tools/benchmark.ps1 -Receipt artifacts/fixtures/proxy.json -Servers 20 -Seconds 360
./tools/outage.ps1 -AfterSeconds 60 -OfflineSeconds 90
```

Warm up SSH before counting steady traffic. The proxy stores byte counts, not SSH contents. Test fixtures bind only to loopback and use synthetic credentials. Keep generated keys and reports in the ignored `artifacts/` folder. Real deployments should reproduce network and fleet measurements in their own environment without publishing target identities.
