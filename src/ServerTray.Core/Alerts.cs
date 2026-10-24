using System.Text.Json;

namespace ServerTray.Core;

public sealed record AlertEvent(string Id, DateTimeOffset At, string ServerId, string Kind, string? Metric = null, double? Value = null);
public sealed record AlertSnapshot(Dictionary<string, bool> Active, HashSet<string> Expiry, List<AlertEvent> Events);
public sealed class AlertEngine
{
    private sealed class RuleState { public DateTimeOffset? Since, Last; }
    private readonly object gate = new();
    private readonly Dictionary<string, RuleState> timers = [];
    private readonly Dictionary<string, bool> active;
    private readonly HashSet<string> expiry;
    private readonly List<AlertEvent> events;
    private readonly string? path;
    public event Action<AlertEvent>? Raised;
    public AlertEngine(string? persistencePath = null)
    {
        path = persistencePath;
        AlertSnapshot? old = null;
        if (path is not null && File.Exists(path)) { try { if (new FileInfo(path).Length <= 1_048_576) old = JsonSerializer.Deserialize<AlertSnapshot>(File.ReadAllText(path)); } catch (Exception e) when (e is JsonException or IOException) { } }
        active = old?.Active ?? []; expiry = old?.Expiry ?? []; events = old?.Events ?? [];
        Trim(DateTimeOffset.UtcNow);
    }
    public AlertEvent[] Events { get { lock (gate) return events.AsEnumerable().Reverse().ToArray(); } }
    public void Prune(DateTimeOffset now) { lock (gate) Trim(now); }
    public void ResetContinuity() { lock (gate) timers.Clear(); }
    public void Evaluate(ServerState state, AlertRules rules, TimeZoneInfo zone, DateTimeOffset now, int defaultIntervalSeconds = 15)
    {
        lock (gate)
        {
            var id = state.Profile.Id;
            if (state.Status is ConnectionStatus.Disabled or ConnectionStatus.Paused or ConnectionStatus.Pending or ConnectionStatus.Connecting) { ResetTimers(id); return; }
            if (rules.Offline)
            {
                var key = id + ":offline"; var down = state.IsOffline || state.Status == ConnectionStatus.Offline;
                if (down && !active.GetValueOrDefault(key)) { active[key] = true; Emit(id, "offline", now); }
                else if (state.Status == ConnectionStatus.Online && active.GetValueOrDefault(key)) { active[key] = false; Emit(id, "recovered", now); }
            }
            if (rules.Expiry && state.Profile.ExpiresOn is { } date)
            {
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime); var remaining = date.DayNumber - today.DayNumber;
                int? stage = remaining <= 0 ? 0 : remaining <= 1 ? 1 : remaining <= 7 ? 7 : remaining <= 30 ? 30 : null;
                if (stage is { } n && expiry.Add($"{id}:{date:yyyy-MM-dd}:{n}")) Emit(id, "expiry", now, value: remaining);
            }
            if (state.Status != ConnectionStatus.Online || state.Sample is not { } sample) { ResetTimers(id); return; }
            CheckRule("cpu", sample.Cpu, rules.CpuHigh, rules.Cpu);
            CheckRule("memory", sample.Memory, rules.MemoryHigh, rules.Memory);
            CheckRule("disk", sample.Disk, rules.DiskHigh, rules.Disk);
            void CheckRule(string metric, double? value, double threshold, bool enabled)
            {
                if (rules.Resources && enabled) Check(id, metric, value, threshold, rules, now, state.Profile.IntervalSeconds ?? defaultIntervalSeconds);
                else timers.Remove(id + ":" + metric);
            }
        }
    }
    private void Check(string id, string metric, double? value, double threshold, AlertRules rules, DateTimeOffset now, int interval)
    {
        var key = id + ":" + metric;
        if (!timers.TryGetValue(key, out var timer)) timers[key] = timer = new();
        if (timer.Last is { } last && (now < last || now - last > TimeSpan.FromSeconds(interval * 2.5))) timer.Since = null;
        timer.Last = now;
        var raised = active.GetValueOrDefault(key);
        var eligible = value is { } v && (raised ? v < threshold - rules.Hysteresis : v >= threshold);
        if (!eligible) { timer.Since = null; return; }
        timer.Since ??= now;
        if ((now - timer.Since.Value).TotalSeconds < (raised ? rules.RecoverySeconds : rules.HighSeconds)) return;
        active[key] = !raised; timer.Since = null;
        Emit(id, raised ? "resource-recovered" : "resource-high", now, metric, value);
    }
    private void ResetTimers(string id) { foreach (var key in timers.Keys.Where(k => k.StartsWith(id + ":", StringComparison.Ordinal)).ToArray()) timers.Remove(key); }
    public void System(string kind, DateTimeOffset now) { lock (gate) Emit("", kind, now); }
    private void Emit(string server, string kind, DateTimeOffset now, string? metric = null, double? value = null)
    {
        var record = new AlertEvent(Guid.NewGuid().ToString("N"), now, server, kind, metric, value); events.Add(record); Trim(now);
        if (path is not null) AtomicFile.Write(path, JsonSerializer.Serialize(new AlertSnapshot(active, expiry, events)));
        Raised?.Invoke(record);
    }
    private void Trim(DateTimeOffset now) { events.RemoveAll(e => e.At < now.AddDays(-7)); if (events.Count > 1000) events.RemoveRange(0, events.Count - 1000); if (expiry.Count > 10000) expiry.Clear(); if (active.Count > 10000) active.Clear(); }
}
