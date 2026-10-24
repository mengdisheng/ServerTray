# Contributing

Use the .NET SDK in `global.json`. Run `dotnet test -c Release`, `python tools/scan_public.py`, and `git diff --check` before sending a change. Windows application tests require Windows; the core is separate from WPF.

Keep remote collection fixed, read-only and bounded. Do not interpolate user input into shell code. New data sources should implement `IMonitorSource` without moving scheduling or alert rules into the UI. Avoid independent connection loops, per-frame graph animations and unbounded producer queues.

Configuration additions need defaults, validation, an updated example and compatibility tests. Never silently reinterpret a future schema. UI text belongs in `Resources/Strings.resx`. Changes to sampling, connection reuse, history, charts or persistence require relevant performance re-measurement.

Use synthetic domains under `.example.invalid`, loopback addresses or documentation networks in examples. Never commit a real configuration or data directory. The `fixture-only` credentials in the loopback test harness are deliberately synthetic and have no access outside that harness.

Published performance claims must include workload, interval, warmup, duration and measurement definitions. A short run is not an eight-hour soak, and accelerated time is not a substitute for a real-duration stability test.
