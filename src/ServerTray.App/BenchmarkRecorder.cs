using System.Diagnostics;
using System.Text.Json;
using ServerTray.Core;

namespace ServerTray.App;

internal sealed class BenchmarkRecorder(App app, int duration, string path)
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly List<Row> rows = [];
    private double? startup;
    private Task? run;
    private bool finished;
    internal sealed record Row(double Seconds, double CpuSingleCorePercent, double PrivateMiB, double WorkingSetMiB, int Handles, int Threads, int Online, double P95CollectionMs, double P95SampleAgeSeconds, long ChartRenderCalls, double P95SchedulingDelayMs, int SshConnections);
    public void RecordStartup(double ms) => startup ??= ms;
    public void Start() => run = Task.Run(async () =>
    {
        using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var previous = elapsed.Elapsed.TotalSeconds;
        while (elapsed.Elapsed.TotalSeconds < duration && !finished)
        {
            await Task.Delay(5000).ConfigureAwait(false); process.Refresh(); var current = elapsed.Elapsed.TotalSeconds; var newCpu = process.TotalProcessorTime;
            var states = app.Engine.States; var fresh = states.Where(s => s.Sample is not null).ToArray();
            var row = new Row(current, (newCpu - cpu).TotalSeconds / (current - previous) * 100, process.PrivateMemorySize64 / 1048576d, process.WorkingSet64 / 1048576d, process.HandleCount, process.Threads.Count, states.Count(s => s.Status == ConnectionStatus.Online), P95(fresh.Select(s => s.Sample!.LatencyMs)), P95(fresh.Select(s => (DateTimeOffset.UtcNow - s.Sample!.At).TotalSeconds)), Interlocked.Read(ref TelemetryChart.RenderCalls), P95(fresh.Select(s => s.Sample!.SchedulingDelayMs)), app.Engine.ConnectionCount);
            lock (rows) { rows.Add(row); if (rows.Count > 10000) rows.RemoveAt(0); }
            cpu = newCpu; previous = current;
            if (rows.Count % 6 == 0) Write(false);
        }
        Write(true); await app.Dispatcher.InvokeAsync(app.ExitAsync).Task.Unwrap();
    });
    public void Finish() { finished = true; Write(elapsed.Elapsed.TotalSeconds >= duration); }
    private void Write(bool complete)
    {
        Row[] snapshot; lock (rows) snapshot = rows.ToArray(); var warmup = duration >= 3600 ? 1800 : 60; var steady = snapshot.Where(r => r.Seconds >= warmup).ToArray(); if (steady.Length == 0) steady = snapshot;
        var coverage = snapshot.Length == 0 ? 0 : snapshot.Select((r, i) => Math.Min(10, r.Seconds - (i == 0 ? 0 : snapshot[i - 1].Seconds))).Sum() / Math.Max(1, elapsed.Elapsed.TotalSeconds);
        var summary = new { schemaVersion = 1, mode = app.Fixture ? "real-ssh-loopback-fixture" : app.Demo ? "demo" : "configured-ssh", complete = complete && elapsed.Elapsed.TotalSeconds >= duration && coverage >= .98, requestedSeconds = duration, elapsedSeconds = elapsed.Elapsed.TotalSeconds, activeCoverage = coverage, warmupSeconds = warmup, servers = app.Config.Servers.Count, intervalSeconds = app.Config.Monitoring.IntervalSeconds, startupMs = startup, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = Environment.OSVersion.VersionString, logicalProcessors = Environment.ProcessorCount, cpuSingleCorePercent = steady.Length == 0 ? 0 : steady.Average(r => r.CpuSingleCorePercent), peakPrivateMiB = snapshot.Select(r => r.PrivateMiB).DefaultIfEmpty().Max(), privateGrowthMiB = steady.Length > 1 ? steady[^1].PrivateMiB - steady[0].PrivateMiB : 0, samples = snapshot };
        try { AtomicFile.Write(Path.GetFullPath(path), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })); } catch (IOException) { app.Log.Write("benchmark-write-error"); }
    }
    private static double P95(IEnumerable<double> source) { var a = source.Order().ToArray(); return a.Length == 0 ? 0 : a[Math.Min(a.Length - 1, (int)Math.Ceiling(a.Length * .95) - 1)]; }
}
