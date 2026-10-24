using ServerTray.Core;
using Xunit;

namespace ServerTray.Tests;

public sealed class CoreTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(1700000000);
    internal static RawMetrics Raw(int second, string boot = "00000000-0000-0000-0000-000000000001", ulong rx = 1000) => new() { At = Epoch.AddSeconds(second), Uptime = 100 + second, BootId = boot, CpuTotal = (ulong)(1000 + second * 100), CpuIdle = (ulong)(500 + second * 50), MemoryTotal = 1000, MemoryAvailable = 200, DiskTotal = 10000, DiskUsed = 1000, Network = new() { ["eth0"] = new(rx, rx), ["lo"] = new(50000, 50000) } };
    [Fact] public void FirstSampleDoesNotInventCpuOrRate() { var s = new MetricDeriver().Derive("a", Raw(0), [], ["lo"]); Assert.Null(s.Cpu); Assert.Null(s.Rx); Assert.Equal(1000UL, s.RxTotal); }
    [Fact] public void DeltasUseRemoteMonotonicUptime() { var d = new MetricDeriver(); d.Derive("a", Raw(0), [], ["lo"]); var s = d.Derive("a", Raw(15, rx: 2500) with { At = Epoch.AddHours(1) }, [], ["lo"]); Assert.Equal(50d, s.Cpu); Assert.Equal(100d, s.Rx); }
    [Fact] public void RebootResetsRates() { var d = new MetricDeriver(); d.Derive("a", Raw(0), [], []); var s = d.Derive("a", Raw(15, "00000000-0000-0000-0000-000000000002"), [], []); Assert.Null(s.Cpu); Assert.Null(s.Tx); }
    [Fact] public void NetworkCounterRollbackDoesNotSpike() { var d = new MetricDeriver(); d.Derive("a", Raw(0), [], []); Assert.Null(d.Derive("a", Raw(15, rx: 1), [], []).Rx); }
    [Fact] public void ChangedInterfaceSetResetsRates() { var d = new MetricDeriver(); d.Derive("a", Raw(0), [], []); var raw = Raw(15); raw.Network.Add("eth1", new(100000, 100000)); Assert.Null(d.Derive("a", raw, [], []).Rx); }
    [Fact] public void IncludeAndExcludePatternsAreAppliedBeforeTotals() { var d = new MetricDeriver(); var s = d.Derive("a", Raw(0), ["eth*"], ["lo"]); Assert.Equal(new[] { "eth0" }, s.Interfaces); }
    [Fact] public void MissingDiskIsNotZeroAndLastKnownIsRetained() { var d = new MetricDeriver(); var s = d.Derive("a", Raw(0) with { DiskTotal = null, DiskUsed = null }, [], []); Assert.Null(s.Disk); d.Derive("a", Raw(15), [], []); Assert.Equal(10d, d.Derive("a", Raw(30) with { DiskTotal = null, DiskUsed = null }, [], []).Disk); }
    [Fact] public void SchemaRejectsFutureAndUnknownProperties() { Assert.Throws<ConfigurationException>(() => ConfigFile.Parse("{\"schemaVersion\":2}")); Assert.Throws<System.Text.Json.JsonException>(() => ConfigFile.Parse("{\"password\":\"fixture-only\"}")); }
    [Theory][InlineData(0)][InlineData(4)][InlineData(301)] public void InvalidIntervalsAreRejected(int i) => Assert.Throws<ConfigurationException>(() => (new AppConfiguration { Monitoring = new() { IntervalSeconds = i } }).Validate());
    [Fact] public void JumpCyclesAreRejected() { var a = Profile("a") with { JumpServerId = "b" }; var b = Profile("b") with { JumpServerId = "a" }; Assert.Throws<ConfigurationException>(() => (new AppConfiguration { Servers = [a, b] }).Validate()); }
    [Fact] public void DuplicateServerIdsAreRejected() => Assert.Throws<ConfigurationException>(() => (new AppConfiguration { Servers = [Profile("a"), Profile("a")] }).Validate());
    [Fact] public void AtomicConfigurationKeepsValidPreviousBackup()
    {
        var dir = Temp(); try { var path = Path.Combine(dir, "settings.json"); ConfigFile.Save(path, new()); ConfigFile.Save(path, new() { Servers = [Profile("a")] }); Assert.Empty(ConfigFile.Load(path + ".bak").Servers); Assert.Single(ConfigFile.Load(path).Servers); } finally { Directory.Delete(dir, true); }
    }
    [Fact] public void SustainedAlertAndHysteresisRecovery()
    {
        var e = new AlertEngine(); var rules = new AlertRules { HighSeconds = 30, RecoverySeconds = 15 };
        for (var t = 0; t <= 30; t += 15) e.Evaluate(State(t, 90), rules, TimeZoneInfo.Utc, Epoch.AddSeconds(t));
        Assert.Single(e.Events); Assert.Equal("resource-high", e.Events[0].Kind);
        e.Evaluate(State(45, 82), rules, TimeZoneInfo.Utc, Epoch.AddSeconds(45)); Assert.Single(e.Events);
        e.Evaluate(State(60, 79), rules, TimeZoneInfo.Utc, Epoch.AddSeconds(60)); e.Evaluate(State(75, 79), rules, TimeZoneInfo.Utc, Epoch.AddSeconds(75));
        Assert.Equal(2, e.Events.Length); Assert.Equal("resource-recovered", e.Events[0].Kind);
    }
    [Fact] public void InterruptionDoesNotCountAsSustainedHigh()
    {
        var e = new AlertEngine(); var r = new AlertRules { HighSeconds = 30 }; e.Evaluate(State(0, 90), r, TimeZoneInfo.Utc, Epoch); e.Evaluate(State(1000, 90), r, TimeZoneInfo.Utc, Epoch.AddSeconds(1000)); Assert.Empty(e.Events);
    }
    [Fact] public void IndividualResourceRulesCanBeDisabled()
    {
        var e = new AlertEngine(); var rules = new AlertRules { Cpu = false, Memory = false, HighSeconds = 0 };
        e.Evaluate(State(0, 99) with { Sample = State(0, 99).Sample! with { Memory = 99, Disk = 99 } }, rules, TimeZoneInfo.Utc, Epoch);
        Assert.Single(e.Events); Assert.Equal("disk", e.Events[0].Metric);
    }
    [Fact] public void ExpiryIsIndependentOfConnectionSuccess()
    {
        var e = new AlertEngine(); var s = new ServerState(Profile("a") with { ExpiresOn = DateOnly.FromDateTime(Epoch.DateTime.AddDays(7)) }, ConnectionStatus.AuthenticationFailed);
        e.Evaluate(s, new(), TimeZoneInfo.Utc, Epoch); Assert.Single(e.Events); Assert.Equal("expiry", e.Events[0].Kind);
    }
    [Fact] public void OfflineDoesNotHideAuthenticationFailure()
    {
        var e = new AlertEngine(); e.Evaluate(new(Profile("a"), ConnectionStatus.AuthenticationFailed, IsOffline: true), new(), TimeZoneInfo.Utc, Epoch);
        Assert.Single(e.Events); Assert.Equal("offline", e.Events[0].Kind);
    }
    [Fact] public void MissingVersionOnePropertiesUseCompatibleDefaults()
    {
        var c = ConfigFile.Parse("{\"schemaVersion\":1,\"alerts\":{\"cpuHigh\":80}}"); Assert.True(c.Alerts.Cpu); Assert.Equal(80, c.Alerts.CpuHigh); Assert.Equal(15, c.Monitoring.IntervalSeconds);
    }
    [Fact] public void ImportDiffIncludesGlobalAndConnectionChangesAndIsBounded()
    {
        var previous = new AppConfiguration { Servers = [Profile("a")] };
        var next = previous with { AutoStart = true, Monitoring = new() { IntervalSeconds = 30 }, Servers = [Profile("a") with { Host = "replacement.example.invalid" }] };
        var diff = ConfigurationDiff.Describe(previous, next);
        Assert.Contains(diff, s => s.StartsWith("autoStart:")); Assert.Contains(diff, s => s.StartsWith("monitoring.intervalSeconds:")); Assert.Contains(diff, s => s.Contains("replacement.example.invalid"));
        Assert.Equal(2, ConfigurationDiff.Describe(previous, next, 1).Length);
    }
    [Fact] public void SlowConfiguredIntervalDoesNotResetEverySample()
    {
        var e = new AlertEngine(); for (var i = 0; i <= 120; i += 60) e.Evaluate(State(i, 90), new(), TimeZoneInfo.Utc, Epoch.AddSeconds(i), 60); Assert.Single(e.Events);
    }
    [Fact] public void OfflineDeduplicationSurvivesRestart()
    {
        var dir = Temp(); try { var p = Path.Combine(dir, "alerts.json"); var offline = new ServerState(Profile("a"), ConnectionStatus.Offline); new AlertEngine(p).Evaluate(offline, new(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow); var next = new AlertEngine(p); next.Evaluate(offline, new(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow); Assert.Single(next.Events); } finally { Directory.Delete(dir, true); }
    }
    [Fact] public void ExpiryUsesConfiguredTimeZoneAndDeduplicates()
    {
        var e = new AlertEngine(); var now = new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.Zero); var s = State(0, 1) with { Profile = Profile("a") with { ExpiresOn = new(2026, 1, 2) } }; var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture", TimeSpan.FromHours(8), "Fixture", "Fixture"); e.Evaluate(s, new(), zone, now); e.Evaluate(s, new(), zone, now); Assert.Single(e.Events); Assert.Equal(0, e.Events[0].Value);
    }
    [Fact] public void HistoryUsesMinuteAveragesAndDoesNotFillGaps()
    {
        var dir = Temp(); try { using var h = new HistoryStore(Path.Combine(dir, "h.sqlite")); h.Add(State(0, 20).Sample!); h.Add(State(15, 40).Sample!); h.Add(State(180, 60).Sample!); h.Flush(7, Epoch.AddSeconds(181)); var rows = h.Query("a", 1, Epoch.AddSeconds(181)); Assert.Equal(2, rows.Length); Assert.Equal(30d, rows[0].Cpu); Assert.True(rows[1].Stamp - rows[0].Stamp >= 120); } finally { Directory.Delete(dir, true); }
    }
    [Fact] public void HistoryRejectsUnboundedRanges() { var dir = Temp(); try { using var h = new HistoryStore(Path.Combine(dir, "h.sqlite")); Assert.Throws<ArgumentOutOfRangeException>(() => h.Query("a", 999999, Epoch)); } finally { Directory.Delete(dir, true); } }
    [Fact] public void CollectorRejectsMissingAndOversizedData() { Assert.Throws<MetricFormatException>(() => LinuxCollector.Parse("ST1\nSTAT\ncpu 1\n", Epoch)); Assert.Throws<MetricFormatException>(() => LinuxCollector.Parse(new string('x', 65537), Epoch)); }
    [Fact] public void CommandNeverContainsSudoPythonOrUserInterpolation() { Assert.DoesNotContain("sudo", LinuxCollector.Script); Assert.DoesNotContain("python", LinuxCollector.Script); Assert.DoesNotContain("{", LinuxCollector.Script); }
    internal static ServerProfile Profile(string id) => new() { Id = id, Name = id, Host = "server.example.invalid", Username = "monitor" };
    private static ServerState State(int t, double cpu) => new(Profile("a"), ConnectionStatus.Online, new() { ServerId = "a", At = Epoch.AddSeconds(t), Cpu = cpu, Memory = 50, Disk = 50 });
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "servertray-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
}
