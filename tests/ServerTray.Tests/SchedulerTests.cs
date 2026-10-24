using ServerTray.Core;
using Xunit;

namespace ServerTray.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task GlobalConcurrencyIsBoundedAndAllTargetsProgress()
    {
        var tracker = new Tracker(); var config = new AppConfiguration { Monitoring = new() { MaxConcurrency = 3 }, Servers = Enumerable.Range(0, 8).Select(i => CoreTests.Profile(i.ToString())).ToList() };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var online = new HashSet<string>();
        await using var engine = new MonitorEngine(config, _ => new Probe(tracker));
        engine.Updated += s => { if (s.Status == ConnectionStatus.Online) lock (online) { online.Add(s.Profile.Id); if (online.Count == 8) completed.TrySetResult(); } };
        engine.Start(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(12)); Assert.InRange(tracker.Peak, 1, 3); Assert.Equal(8, online.Count);
    }
    [Fact]
    public async Task AuthenticationFailureIsNotRetriedUntilExplicitRetry()
    {
        var calls = 0; var p = CoreTests.Profile("auth"); var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new MonitorEngine(new() { Servers = [p] }, _ => new Failing(() => { Interlocked.Increment(ref calls); return new MissingCredentialException(); }));
        engine.Updated += s => { if (s.Status == ConnectionStatus.AuthenticationFailed) seen.TrySetResult(); };
        engine.Start(); await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(1200); Assert.Equal(1, calls); engine.Retry(); await Task.Delay(1500); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task FailedTargetDoesNotBlockHealthyTargetAndInitialFailureIsStale()
    {
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var config = new AppConfiguration { Servers = [CoreTests.Profile("bad"), CoreTests.Profile("good")] };
        await using var engine = new MonitorEngine(config, p => p.Id == "bad" ? new Failing(() => new IOException()) : new Probe(new()));
        engine.Updated += s => { if (s.Profile.Id == "good" && s.Status == ConnectionStatus.Online) healthy.TrySetResult(); };
        engine.Start(); await healthy.Task.WaitAsync(TimeSpan.FromSeconds(6)); Assert.Equal(ConnectionStatus.Stale, engine.States.Single(s => s.Profile.Id == "bad").Status);
    }
    [Fact]
    public async Task ShutdownCancelsInFlightWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var engine = new MonitorEngine(new() { Servers = [CoreTests.Profile("slow")] }, _ => new Hanging(entered));
        engine.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task BlockedAuthenticationStillBecomesOfflineAndResumeRestartsGrace()
    {
        var clock = new ManualClock(); var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new MonitorEngine(new() { Servers = [CoreTests.Profile("blocked")] }, _ => new Failing(() => new MissingCredentialException()), clock);
        engine.Updated += s => { if (s.Status == ConnectionStatus.AuthenticationFailed) failed.TrySetResult(); if (s.IsOffline) offline.TrySetResult(); };
        engine.Start(); await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(76); await offline.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ConnectionStatus.AuthenticationFailed, engine.States[0].Status);
        engine.Pause(); engine.Resume(); clock.Advance(1); await Task.Delay(1200); Assert.Equal(ConnectionStatus.AuthenticationFailed, engine.States[0].Status); Assert.False(engine.States[0].IsOffline);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks = 1000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(int seconds) => Interlocked.Add(ref ticks, seconds * 1000L);
    }
    private sealed class Tracker { public int Active, Peak; }
    private sealed class Probe(Tracker tracker) : IMonitorSource
    {
        public StaticInfo Info => new("Fixture", "x86_64", 2);
        public async Task<RawMetrics> CollectAsync(bool disk, CancellationToken token)
        {
            var active = Interlocked.Increment(ref tracker.Active); int initial;
            do { initial = tracker.Peak; if (initial >= active) break; } while (Interlocked.CompareExchange(ref tracker.Peak, active, initial) != initial);
            try { await Task.Delay(80, token); return CoreTests.Raw(0) with { At = DateTimeOffset.UtcNow }; } finally { Interlocked.Decrement(ref tracker.Active); }
        }
        public void Abort() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Failing(Func<Exception> exception) : IMonitorSource { public StaticInfo? Info => null; public Task<RawMetrics> CollectAsync(bool disk, CancellationToken token) => Task.FromException<RawMetrics>(exception()); public void Abort() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Hanging(TaskCompletionSource entered) : IMonitorSource { public StaticInfo? Info => null; public async Task<RawMetrics> CollectAsync(bool disk, CancellationToken token) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); } public void Abort() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
