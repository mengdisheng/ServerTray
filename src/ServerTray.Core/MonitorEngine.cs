using System.Collections.Concurrent;
using System.Diagnostics;
using Renci.SshNet.Common;

namespace ServerTray.Core;

public sealed class MonitorEngine : IAsyncDisposable
{
    private sealed class Worker(ServerProfile profile, IMonitorSource source, long now)
    {
        public readonly ServerProfile Profile = profile;
        public readonly IMonitorSource Source = source;
        public readonly MetricDeriver Deriver = new();
        public readonly Queue<MetricSample> Recent = new();
        public ServerState State = new(profile, profile.Enabled ? ConnectionStatus.Pending : ConnectionStatus.Disabled);
        public HostKeyChallenge? Challenge;
        public long Due, LastDisk, LastSuccessTick, GraceTick = now;
        public int Failures;
        public bool Busy, Blocked;
        public ConnectionStatus BeforePause;
    }
    private readonly AppConfiguration config;
    private readonly Dictionary<string, Worker> workers;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim capacity;
    private readonly object gate = new();
    private readonly HashSet<Task> active = [];
    private Task? scheduler;
    private bool suspended;
    private readonly TimeProvider clock;
    private long Now => (long)clock.GetElapsedTime(0, clock.GetTimestamp()).TotalMilliseconds;
    public event Action<ServerState>? Updated;
    public event Action<string>? SystemEvent;
    public bool IsNetworkAvailable { get; private set; } = true;
    public MonitorEngine(AppConfiguration configuration, Func<ServerProfile, IMonitorSource> factory, TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        configuration.Validate(); config = configuration; capacity = new(config.Monitoring.MaxConcurrency);
        workers = config.Servers.ToDictionary(p => p.Id, p => new Worker(p, factory(p), Now));
        var enabled = workers.Values.Where(w => w.Profile.Enabled).ToArray();
        for (var i = 0; i < enabled.Length; i++) enabled[i].Due = Now + (long)(i * 1000.0 / Math.Max(1, enabled.Length));
    }
    public IReadOnlyList<ServerState> States { get { lock (gate) return workers.Values.Select(w => w.State).ToArray(); } }
    public int ConnectionCount { get { lock (gate) return workers.Values.Sum(w => w.Source.ConnectionCount); } }
    public HostKeyChallenge? Challenge(string id) { lock (gate) return workers.GetValueOrDefault(id)?.Challenge; }
    public MetricSample[] Recent(string id) { lock (gate) return workers.TryGetValue(id, out var w) ? w.Recent.ToArray() : []; }
    public void Start() => scheduler ??= Task.Run(Schedule);
    public void Retry(string? id = null) { lock (gate) foreach (var w in workers.Values.Where(w => id is null || w.Profile.Id == id)) { w.Blocked = false; w.Due = Now; } }
    public void SetNetworkAvailable(bool value)
    {
        if (IsNetworkAvailable == value) return; IsNetworkAvailable = value;
        if (value) { SystemEvent?.Invoke("network-restored"); Resume(); }
        else { SystemEvent?.Invoke("network-lost"); Pause(); }
    }
    public void Pause()
    {
        suspended = true;
        lock (gate) foreach (var w in workers.Values) { w.Deriver.Reset(); if (w.Profile.Enabled) { if (w.State.Status != ConnectionStatus.Paused) w.BeforePause = w.State.Status; w.State = w.State with { Status = ConnectionStatus.Paused }; Updated?.Invoke(w.State); } }
    }
    public void Resume()
    {
        lock (gate)
        {
            suspended = false;
            foreach (var w in workers.Values)
            {
                w.Deriver.Reset(); w.GraceTick = Now; w.LastDisk = 0; w.Due = Now + Random.Shared.Next(0, 1000);
                if (w.Profile.Enabled) { w.State = w.State with { Status = w.Blocked ? w.BeforePause : ConnectionStatus.Pending, IsOffline = false }; Updated?.Invoke(w.State); }
            }
        }
    }
    private async Task Schedule()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if (suspended || !IsNetworkAvailable) continue;
                lock (gate)
                {
                    var now = Now;
                    foreach (var w in workers.Values.OrderBy(w => w.Due))
                    {
                        if (!w.Profile.Enabled) continue;
                        var ageSeconds = (now - Math.Max(w.LastSuccessTick, w.GraceTick)) / 1000d;
                        if (ageSeconds > Math.Max(75, 3 * (w.Profile.IntervalSeconds ?? config.Monitoring.IntervalSeconds)) && !w.State.IsOffline)
                        { w.State = w.State with { Status = w.State.Status == ConnectionStatus.Stale ? ConnectionStatus.Offline : w.State.Status, IsOffline = true }; Updated?.Invoke(w.State); }
                        if (w.Blocked || w.Busy) continue;
                        if (now < w.Due || !capacity.Wait(0)) continue;
                        w.Busy = true;
                        var task = Task.Run(() => Poll(w)); active.Add(task);
                        _ = task.ContinueWith(t => { lock (gate) active.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            } while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task Poll(Worker w)
    {
        var started = Stopwatch.GetTimestamp();
        var schedulingDelay = Math.Max(0, Now - w.Due);
        try
        {
            if (w.State.Sample is null) Publish(w, w.State with { Status = ConnectionStatus.Connecting });
            var disk = Now - w.LastDisk >= 60000 || w.LastDisk == 0;
            var raw = await w.Source.CollectAsync(disk, stop.Token).ConfigureAwait(false);
            MetricSample sample;
            lock (gate)
            {
                sample = w.Deriver.Derive(w.Profile.Id, raw, w.Profile.IncludeInterfaces ?? config.Monitoring.IncludeInterfaces, w.Profile.ExcludeInterfaces ?? config.Monitoring.ExcludeInterfaces, Stopwatch.GetElapsedTime(started).TotalMilliseconds) with { SchedulingDelayMs = schedulingDelay };
                if (disk) w.LastDisk = Now;
                w.Recent.Enqueue(sample); while (w.Recent.Count > 241 || (w.Recent.Count > 0 && sample.At - w.Recent.Peek().At > TimeSpan.FromMinutes(20))) w.Recent.Dequeue();
                w.Failures = 0; w.Challenge = null; w.LastSuccessTick = Now;
            }
            Publish(w, new(w.Profile, suspended ? ConnectionStatus.Paused : ConnectionStatus.Online, sample, w.Source.Info));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e)
        {
            var status = e switch { HostKeyException h => h.Challenge.Expected is null ? ConnectionStatus.HostKeyRequired : ConnectionStatus.HostKeyChanged, MissingCredentialException or CredentialFormatException or SshAuthenticationException => ConnectionStatus.AuthenticationFailed, MetricFormatException => ConnectionStatus.CollectionFailed, TimeoutException or OperationCanceledException or SshOperationTimeoutException => ConnectionStatus.TimedOut, _ => w.State.Status == ConnectionStatus.Offline ? ConnectionStatus.Offline : ConnectionStatus.Stale };
            lock (gate) { w.Failures++; w.Challenge = (e as HostKeyException)?.Challenge; w.Blocked = status is ConnectionStatus.HostKeyRequired or ConnectionStatus.HostKeyChanged or ConnectionStatus.AuthenticationFailed; }
            // Exception messages can contain hostnames or credential material. Publish only fixed diagnostic codes.
            Publish(w, w.State with { Status = suspended ? ConnectionStatus.Paused : status, Error = status.ToString() });
        }
        finally
        {
            lock (gate) { var interval = w.Profile.IntervalSeconds ?? config.Monitoring.IntervalSeconds; var delay = w.Failures == 0 ? interval : Math.Min(120, interval * Math.Pow(2, Math.Min(4, w.Failures - 1))); w.Due = Now + (long)(delay * 1000) + (w.Failures > 0 ? Random.Shared.Next(0, 1000) : 0); w.Busy = false; }
            capacity.Release();
        }
    }
    private void Publish(Worker w, ServerState state) { lock (gate) w.State = state; Updated?.Invoke(state); }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        if (scheduler is not null) await scheduler.ConfigureAwait(false);
        Task[] tasks; lock (gate) tasks = active.ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var worker in workers.Values) await worker.Source.DisposeAsync().ConfigureAwait(false);
        capacity.Dispose(); stop.Dispose();
    }
}
