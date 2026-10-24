using System.Diagnostics;
using System.Text.Json;
using ServerTray.Core;

string? Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (Arg("--validate-config") is { } configurationFile) { var parsedConfig = ConfigFile.Load(configurationFile); parsedConfig.Validate(); Console.WriteLine("Valid schemaVersion=" + parsedConfig.SchemaVersion); return; }
var receipt = Arg("--receipt") ?? throw new ArgumentException("--receipt is required (loopback fixture).");
var fixture = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(receipt)); var port = fixture.GetProperty("port").GetInt32(); var pin = fixture.GetProperty("fingerprint").GetString()!;
var count = int.TryParse(Arg("--count"), out var parsed) ? Math.Clamp(parsed, 1, 20) : 20;
ServerProfile Profile(string id, string username = "monitor") => new() { Id = id, Name = id, Host = "127.0.0.1", Port = port, Username = username };
var credentials = new Credentials("fixture-only"); var pins = new Pins(pin);
async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
if (args.Contains("--wire-check"))
{
    var p = Profile("wire"); var config = new AppConfiguration { Servers = [p] };
    await using var source = new SshMonitorSource(p, config, credentials, pins);
    await source.CollectAsync(true, CancellationToken.None); await Task.Delay(300);
    long Bytes()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(receipt));
        return json.RootElement.GetProperty("connections").EnumerateArray().Sum(c => c.GetProperty("sent").GetInt64() + c.GetProperty("received").GetInt64());
    }
    var previous = Bytes(); var rounds = new List<long>();
    for (var i = 0; i < 5; i++)
    {
        await Task.Delay(15000); await source.CollectAsync(i % 4 == 0, CancellationToken.None); await Task.Delay(200);
        var current = Bytes(); rounds.Add(current - previous); previous = current;
    }
    var wireReport = new { mode = "encrypted-ssh-tcp-payload", complete = true, intervalSeconds = 15, rounds, maxBytesPerRound = rounds.Max(), handshakeExcluded = true, includesKeepAlive = true, passed = rounds.Max() <= 16384 };
    var outputPath = Arg("--output") ?? "artifacts/bench/wire.json"; AtomicFile.Write(Path.GetFullPath(outputPath), JsonSerializer.Serialize(wireReport, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(wireReport)); return;
}
if (args.Contains("--self-test"))
{
    var checks = new List<string>(); var p = Profile("direct"); var config = new AppConfiguration { Servers = [p] };
    await using (var source = new SshMonitorSource(p, config, credentials, pins))
    {
        var first = await source.CollectAsync(true, CancellationToken.None); await Task.Delay(100); var second = await source.CollectAsync(false, CancellationToken.None);
        if (first.MemoryTotal <= 0 || second.CpuTotal <= first.CpuTotal || source.Info?.Cores != 2) throw new InvalidOperationException("Invalid real SSH metric result."); checks.Add("password-auth-static-metrics-and-reused-connection");
    }
    await using (var s = new SshMonitorSource(p, config, credentials, new Pins(null))) { await Expect<HostKeyException>(async () => await s.CollectAsync(true, CancellationToken.None)); checks.Add("unknown-host-key-is-blocked"); }
    await using (var s = new SshMonitorSource(p, config, credentials, new Pins("SHA256:wrong"))) { await Expect<HostKeyException>(async () => await s.CollectAsync(true, CancellationToken.None)); checks.Add("changed-host-key-is-blocked"); }
    await using (var s = new SshMonitorSource(p, config, new Credentials("incorrect-fixture-password"), pins)) { await Expect<Renci.SshNet.Common.SshAuthenticationException>(async () => await s.CollectAsync(true, CancellationToken.None)); checks.Add("invalid-password-is-rejected"); }
    var oversized = Profile("oversize", "oversize");
    await using (var s = new SshMonitorSource(oversized, config with { Servers = [oversized] }, credentials, pins)) { await Expect<MetricFormatException>(async () => await s.CollectAsync(true, CancellationToken.None)); checks.Add("oversized-output-is-disconnected"); }
    var slow = Profile("slow", "slow") with { TimeoutSeconds = 1 };
    await using (var s = new SshMonitorSource(slow, config with { Servers = [slow] }, credentials, pins)) { var sw = Stopwatch.StartNew(); try { await s.CollectAsync(true, CancellationToken.None); throw new InvalidOperationException("Expected timeout."); } catch (Exception e) when (e is not InvalidOperationException) { if (sw.Elapsed.TotalSeconds > 5) throw new InvalidOperationException("Timeout was not bounded."); } checks.Add("slow-command-has-bounded-timeout"); }
    var jump = Profile("jump"); var target = Profile("target") with { JumpServerId = "jump" }; var jumpConfig = config with { Servers = [jump, target] };
    await using (var s = new SshMonitorSource(target, jumpConfig, credentials, pins)) { var result = await s.CollectAsync(true, CancellationToken.None); if (result.MemoryTotal <= 0) throw new InvalidOperationException("Jump failed."); checks.Add("single-hop-ssh-tunnel"); }
    var keyPath = Arg("--private-key"); if (keyPath is not null) { var keyed = p with { Authentication = AuthenticationKind.PrivateKey }; await using var s = new SshMonitorSource(keyed, config with { Servers = [keyed] }, new KeyCredentials(File.ReadAllText(keyPath)), pins); await s.CollectAsync(true, CancellationToken.None); checks.Add("encrypted-private-key-authentication"); }
    Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true })); return;
}
var seconds = int.TryParse(Arg("--seconds"), out var sCount) ? Math.Max(10, sCount) : 120;
var output = Arg("--output") ?? "artifacts/bench/core.json"; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
var profiles = Enumerable.Range(1, count).Select(i => Profile("fixture-" + i)).ToList();
var configuration = new AppConfiguration { Servers = profiles }; var states = new System.Collections.Concurrent.ConcurrentDictionary<string, ServerState>();
using var history = new HistoryStore(Path.ChangeExtension(output, ".sqlite")); var alerts = new AlertEngine();
await using var engine = new MonitorEngine(configuration, p => new SshMonitorSource(p, configuration, credentials, pins));
engine.Updated += state => { states[state.Profile.Id] = state; if (state.Sample is { } sample && state.Status == ConnectionStatus.Online) history.Add(sample); alerts.Evaluate(state, new(), TimeZoneInfo.Utc, DateTimeOffset.UtcNow); };
engine.Start(); using var process = Process.GetCurrentProcess(); var clock = Stopwatch.StartNew(); var cpuStart = process.TotalProcessorTime;
var measurements = new List<object>();
while (clock.Elapsed.TotalSeconds < seconds)
{
    await Task.Delay(5000); process.Refresh();
    measurements.Add(new { elapsedSeconds = clock.Elapsed.TotalSeconds, privateMiB = process.PrivateMemorySize64 / 1048576d, handles = process.HandleCount, online = states.Values.Count(s => s.Status == ConnectionStatus.Online), errors = states.Values.Where(s => s.Status is not (ConnectionStatus.Online or ConnectionStatus.Connecting or ConnectionStatus.Pending)).Select(s => s.Status.ToString()).ToArray() });
    if (measurements.Count % 12 == 0) history.Flush(7, DateTimeOffset.UtcNow);
}
history.Flush(7, DateTimeOffset.UtcNow);
var report = new { complete = true, servers = count, elapsedSeconds = clock.Elapsed.TotalSeconds, cpuSingleCorePercent = (process.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds * 100, measurements };
AtomicFile.Write(Path.GetFullPath(output), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(report));
sealed class Credentials(string password) : ICredentialStore { public Credential Get(string id) => new(password); }
sealed class KeyCredentials(string key) : ICredentialStore { public Credential Get(string id) => new(PrivateKey: key, Passphrase: "fixture-passphrase"); }
sealed class Pins(string? pin) : IHostKeyStore { public string? Get(string host, int port) => pin; }
