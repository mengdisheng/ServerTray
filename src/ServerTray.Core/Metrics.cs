using System.Globalization;
using System.Text.RegularExpressions;

namespace ServerTray.Core;

public enum ConnectionStatus { Pending, Connecting, Online, Stale, Offline, AuthenticationFailed, HostKeyRequired, HostKeyChanged, CollectionFailed, Disabled, Paused, TimedOut }
public sealed record NetworkCounter(ulong Received, ulong Sent);
public sealed record RawMetrics
{
    public DateTimeOffset At { get; init; }
    public string BootId { get; init; } = "";
    public ulong CpuTotal { get; init; }
    public ulong CpuIdle { get; init; }
    public long MemoryTotal { get; init; }
    public long MemoryAvailable { get; init; }
    public long SwapTotal { get; init; }
    public long SwapFree { get; init; }
    public long? DiskTotal { get; init; }
    public long? DiskUsed { get; init; }
    public double Uptime { get; init; }
    public double[] Load { get; init; } = [];
    public ulong DiskRead { get; init; }
    public ulong DiskWrite { get; init; }
    public Dictionary<string, NetworkCounter> Network { get; init; } = new(StringComparer.Ordinal);
}
public sealed record MetricSample
{
    public string ServerId { get; init; } = "";
    public DateTimeOffset At { get; init; }
    public string BootId { get; init; } = "";
    public double? Cpu { get; init; }
    public double Memory { get; init; }
    public double? Disk { get; init; }
    public long MemoryUsed { get; init; }
    public long MemoryTotal { get; init; }
    public long SwapUsed { get; init; }
    public long SwapTotal { get; init; }
    public long? DiskUsed { get; init; }
    public long? DiskTotal { get; init; }
    public double? Rx { get; init; }
    public double? Tx { get; init; }
    public ulong RxTotal { get; init; }
    public ulong TxTotal { get; init; }
    public double? DiskRead { get; init; }
    public double? DiskWrite { get; init; }
    public double Uptime { get; init; }
    public double[] Load { get; init; } = [];
    public double LatencyMs { get; init; }
    public double SchedulingDelayMs { get; init; }
    public string[] Interfaces { get; init; } = [];
}
public sealed record StaticInfo(string OperatingSystem, string Architecture, int Cores);
public sealed record ServerState(ServerProfile Profile, ConnectionStatus Status, MetricSample? Sample = null, StaticInfo? Info = null, string? Error = null, bool IsOffline = false);
public sealed class MetricFormatException(string message) : Exception(message);

public static class LinuxCollector
{
    // Constant script: no user-controlled input is interpolated into remote commands.
    public const string Script = """
export LC_ALL=C
printf 'ST1\nSTAT\n'; head -n 1 /proc/stat
printf 'BOOT\n'; cat /proc/sys/kernel/random/boot_id
printf 'MEM\n'; cat /proc/meminfo
printf 'UP\n'; cat /proc/uptime
printf 'LOAD\n'; cat /proc/loadavg
printf 'NET\n'; cat /proc/net/dev
printf 'IO\n'; cat /proc/diskstats
""";
    public const string DiskScript = "printf 'DISK\\n'; df -Pk / 2>/dev/null | tail -n 1\n";
    public const string StaticScript = "export LC_ALL=C; printf 'OS\\n'; cat /etc/os-release 2>/dev/null | head -c 4096; printf '\\nARCH\\n'; uname -m; printf 'CORES\\n'; getconf _NPROCESSORS_ONLN 2>/dev/null || printf '1\\n'\n";
    public const int MaxOutputBytes = 65536;
    public static RawMetrics Parse(string output, DateTimeOffset at)
    {
        if (output.Length > MaxOutputBytes || !output.StartsWith("ST1\n", StringComparison.Ordinal)) throw new MetricFormatException("Invalid or oversized collector output.");
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal); string? section = null;
        foreach (var line in output.Split('\n'))
        {
            if (line is "STAT" or "BOOT" or "MEM" or "UP" or "LOAD" or "NET" or "IO" or "DISK") { section = line; sections[section] = []; }
            else if (section is not null && !string.IsNullOrWhiteSpace(line)) sections[section].Add(line);
        }
        try
        {
            var cpu = Words(sections["STAT"][0]).Skip(1).Take(8).Select(UL).ToArray();
            if (cpu.Length < 4) throw new FormatException();
            var memory = sections["MEM"].Select(Words).Where(a => a.Length >= 2).ToDictionary(a => a[0].TrimEnd(':'), a => checked((long)UL(a[1]) * 1024));
            var net = new Dictionary<string, NetworkCounter>(StringComparer.Ordinal);
            foreach (var line in sections["NET"])
            {
                var split = line.IndexOf(':'); if (split < 0) continue;
                var fields = Words(line[(split + 1)..]); if (fields.Length < 16) throw new FormatException();
                net.Add(line[..split].Trim(), new(UL(fields[0]), UL(fields[8])));
            }
            ulong read = 0, write = 0;
            foreach (var line in sections["IO"])
            {
                var f = Words(line); if (f.Length < 10) continue;
                if (Regex.IsMatch(f[2], "^(?:[svx]d[a-z]+|xvd[a-z]+|nvme[0-9]+n[0-9]+|mmcblk[0-9]+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) { read = checked(read + UL(f[5]) * 512); write = checked(write + UL(f[9]) * 512); }
            }
            long? total = null, used = null;
            if (sections.TryGetValue("DISK", out var d) && d.Count > 0) { var f = Words(d[^1]); if (f.Length >= 6) { total = checked((long)UL(f[1]) * 1024); used = checked((long)UL(f[2]) * 1024); } }
            var boot = sections["BOOT"][0].Trim(); if (!Guid.TryParse(boot, out _)) throw new FormatException();
            var memTotal = memory["MemTotal"]; var available = memory.GetValueOrDefault("MemAvailable", memory.GetValueOrDefault("MemFree") + memory.GetValueOrDefault("Buffers") + memory.GetValueOrDefault("Cached"));
            if (memTotal <= 0 || net.Count > 128 || net.Count == 0 || available < 0 || available > memTotal) throw new FormatException();
            return new() { At = at, BootId = boot, CpuTotal = cpu.Aggregate(0UL, (a, b) => checked(a + b)), CpuIdle = checked(cpu[3] + (cpu.Length > 4 ? cpu[4] : 0)), MemoryTotal = memTotal, MemoryAvailable = available, SwapTotal = memory.GetValueOrDefault("SwapTotal"), SwapFree = memory.GetValueOrDefault("SwapFree"), DiskTotal = total, DiskUsed = used, Uptime = Double(Words(sections["UP"][0])[0]), Load = Words(sections["LOAD"][0]).Take(3).Select(Double).ToArray(), Network = net, DiskRead = read, DiskWrite = write };
        }
        catch (Exception e) when (e is KeyNotFoundException or IndexOutOfRangeException or FormatException or OverflowException or ArgumentException) { throw new MetricFormatException("Incomplete or invalid Linux metrics."); }
    }
    public static StaticInfo ParseStatic(string raw)
    {
        var lines = raw.Split('\n'); var os = lines.FirstOrDefault(x => x.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?[12..].Trim('"', '\'') ?? "Linux";
        var arch = Array.IndexOf(lines, "ARCH"); var cores = Array.IndexOf(lines, "CORES");
        return new(os.Length > 120 ? os[..120] : os, arch >= 0 && arch + 1 < lines.Length ? lines[arch + 1] : "Linux", cores >= 0 && cores + 1 < lines.Length && int.TryParse(lines[cores + 1], out var c) ? Math.Clamp(c, 1, 65536) : 1);
    }
    private static string[] Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static ulong UL(string s) => ulong.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
    private static double Double(string s) { var n = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); if (!double.IsFinite(n) || n < 0) throw new FormatException(); return n; }
}
public sealed class MetricDeriver
{
    private RawMetrics? previous;
    private long? diskTotal, diskUsed;
    public MetricSample Derive(string id, RawMetrics raw, string[] include, string[] exclude, double latency = 0)
    {
        var prior = previous; previous = raw;
        if (prior is not null && prior.BootId != raw.BootId) diskTotal = diskUsed = null;
        if (raw.DiskTotal is > 0 && raw.DiskUsed is >= 0 && raw.DiskUsed <= raw.DiskTotal) { diskTotal = raw.DiskTotal; diskUsed = raw.DiskUsed; }
        var elapsed = prior is null ? 0 : raw.Uptime - prior.Uptime;
        var valid = prior is not null && prior.BootId == raw.BootId && elapsed > 0;
        double? cpu = valid && raw.CpuTotal > prior!.CpuTotal && raw.CpuIdle >= prior.CpuIdle ? 100 * (1 - Math.Min(1, (double)(raw.CpuIdle - prior.CpuIdle) / (raw.CpuTotal - prior.CpuTotal))) : null;
        var selected = raw.Network.Where(p => (include.Length == 0 || include.Any(x => Glob(x, p.Key))) && !exclude.Any(x => Glob(x, p.Key))).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var names = selected.Select(p => p.Key).ToArray();
        var oldNames = prior?.Network.Keys.Where(k => (include.Length == 0 || include.Any(x => Glob(x, k))) && !exclude.Any(x => Glob(x, k))).Order(StringComparer.Ordinal).ToArray();
        var sameNetwork = valid && names.Length > 0 && oldNames!.SequenceEqual(names);
        double? rx = sameNetwork ? 0 : null, tx = sameNetwork ? 0 : null; ulong rxTotal = 0, txTotal = 0;
        foreach (var (name, n) in selected)
        {
            rxTotal = checked(rxTotal + n.Received); txTotal = checked(txTotal + n.Sent);
            if (sameNetwork) { var old = prior!.Network[name]; if (n.Received < old.Received || n.Sent < old.Sent) { rx = tx = null; } else { if (rx is not null) rx += (n.Received - old.Received) / elapsed; if (tx is not null) tx += (n.Sent - old.Sent) / elapsed; } }
        }
        return new() { ServerId = id, At = raw.At, BootId = raw.BootId, Cpu = cpu, Memory = 100.0 * (raw.MemoryTotal - raw.MemoryAvailable) / raw.MemoryTotal, MemoryTotal = raw.MemoryTotal, MemoryUsed = raw.MemoryTotal - raw.MemoryAvailable, SwapTotal = raw.SwapTotal, SwapUsed = Math.Max(0, raw.SwapTotal - raw.SwapFree), DiskTotal = diskTotal, DiskUsed = diskUsed, Disk = diskTotal is > 0 ? 100.0 * diskUsed / diskTotal : null, Rx = rx, Tx = tx, RxTotal = rxTotal, TxTotal = txTotal, DiskRead = valid && raw.DiskRead >= prior!.DiskRead ? (raw.DiskRead - prior.DiskRead) / elapsed : null, DiskWrite = valid && raw.DiskWrite >= prior!.DiskWrite ? (raw.DiskWrite - prior.DiskWrite) / elapsed : null, Uptime = raw.Uptime, Load = raw.Load, Interfaces = names, LatencyMs = latency };
    }
    public void Reset() { previous = null; diskTotal = diskUsed = null; }
    private static bool Glob(string pattern, string text) => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, text, ignoreCase: false);
}
