using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerTray.Core;

public enum AuthenticationKind { Password, PrivateKey }
public enum ThemeKind { System, Light, Dark }
public sealed record AlertRules
{
    public bool Offline { get; init; } = true;
    public bool Resources { get; init; } = true;
    public bool Cpu { get; init; } = true;
    public bool Memory { get; init; } = true;
    public bool Disk { get; init; } = true;
    public bool Expiry { get; init; } = true;
    public double CpuHigh { get; init; } = 85;
    public double MemoryHigh { get; init; } = 90;
    public double DiskHigh { get; init; } = 90;
    public double Hysteresis { get; init; } = 5;
    public int HighSeconds { get; init; } = 120;
    public int RecoverySeconds { get; init; } = 60;
}
public sealed record MonitorSettings
{
    public int IntervalSeconds { get; init; } = 15;
    public int TimeoutSeconds { get; init; } = 10;
    public int MaxConcurrency { get; init; } = 4;
    public int RetentionDays { get; init; } = 7;
    public string[] ExcludeInterfaces { get; init; } = ["lo", "docker*", "veth*", "br-*", "virbr*", "tun*", "wg*"];
    public string[] IncludeInterfaces { get; init; } = [];
}
public sealed record ServerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "New server";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "";
    public AuthenticationKind Authentication { get; init; }
    public string Group { get; init; } = "";
    public string[] Tags { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public int Order { get; init; }
    public string? JumpServerId { get; init; }
    public int? IntervalSeconds { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string[]? IncludeInterfaces { get; init; }
    public string[]? ExcludeInterfaces { get; init; }
    public AlertRules? Alerts { get; init; }
    public DateOnly? ExpiresOn { get; init; }
}
public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public MonitorSettings Monitoring { get; init; } = new();
    public AlertRules Alerts { get; init; } = new();
    public ThemeKind Theme { get; init; }
    public string TimeZoneId { get; init; } = "Local";
    public bool AutoStart { get; init; }
    public string? DefaultServerId { get; init; }
    public string TraySummary { get; init; } = "Fleet";
    public string[] VisibleMetrics { get; init; } = ["cpu", "memory", "disk", "network", "io", "swap", "load"];
    public List<ServerProfile> Servers { get; init; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1) throw new ConfigurationException("Unsupported schemaVersion; supported version: 1.");
        if (Monitoring is null || Alerts is null || Servers is null || VisibleMetrics is null) throw new ConfigurationException("Configuration sections cannot be null.");
        Range(Monitoring.IntervalSeconds, 5, 300, "intervalSeconds"); Range(Monitoring.TimeoutSeconds, 1, 30, "timeoutSeconds");
        Range(Monitoring.MaxConcurrency, 1, 8, "maxConcurrency"); Range(Monitoring.RetentionDays, 1, 30, "retentionDays");
        if (!Enum.IsDefined(Theme) || TraySummary is not ("Fleet" or "Selected")) throw new ConfigurationException("Invalid interface option.");
        if (TimeZoneId != "Local") { try { TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { throw new ConfigurationException("Invalid time zone."); } }
        if (Servers.Count > 200) throw new ConfigurationException("A maximum of 200 profiles is allowed; performance target: 20 active servers.");
        if (Servers.Any(s => s is null)) throw new ConfigurationException("Invalid server profile.");
        if (Servers.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Servers.Count) throw new ConfigurationException("Duplicate server IDs.");
        ValidateRules(Alerts); ValidatePatterns(Monitoring.IncludeInterfaces); ValidatePatterns(Monitoring.ExcludeInterfaces);
        if (VisibleMetrics.Any(x => x is not ("cpu" or "memory" or "disk" or "network" or "io" or "swap" or "load"))) throw new ConfigurationException("Invalid metric.");
        foreach (var s in Servers)
        {
            Text(s.Id, 64, "id"); Text(s.Name, 120, "name"); Text(s.Host, 253, "host"); Text(s.Username, 128, "username");
            if (s.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ConfigurationException("Server IDs allow only ASCII letters, numbers, hyphen and underscore.");
            if (s.Host.Any(char.IsWhiteSpace) || s.Host.IndexOfAny(['/', '\\', '@', '\'', '"']) >= 0) throw new ConfigurationException("Invalid SSH host.");
            if (!Enum.IsDefined(s.Authentication)) throw new ConfigurationException("Invalid authentication method.");
            Range(s.Port, 1, 65535, "port"); if (s.IntervalSeconds is { } i) Range(i, 5, 300, "server intervalSeconds");
            if (s.TimeoutSeconds is { } t) Range(t, 1, 30, "server timeoutSeconds");
            if (s.Group is null || s.Group.Length > 80 || s.Tags is null || s.Tags.Length > 20 || s.Tags.Any(t => t is null || t.Length > 60)) throw new ConfigurationException("Invalid group/tags.");
            if (s.Alerts is not null) ValidateRules(s.Alerts);
            if (s.IncludeInterfaces is not null) ValidatePatterns(s.IncludeInterfaces); if (s.ExcludeInterfaces is not null) ValidatePatterns(s.ExcludeInterfaces);
            if (s.JumpServerId is not null)
            {
                var jump = Servers.SingleOrDefault(x => x.Id == s.JumpServerId);
                if (jump is null || jump.Id == s.Id || jump.JumpServerId is not null) throw new ConfigurationException("Jump host must exist and cannot use another jump host.");
            }
        }
        if (DefaultServerId is not null && Servers.All(s => s.Id != DefaultServerId)) throw new ConfigurationException("Unknown default server.");
    }
    private static void Text(string? x, int max, string name) { if (string.IsNullOrWhiteSpace(x) || x.Length > max || x.Any(char.IsControl)) throw new ConfigurationException("Invalid " + name); }
    private static void Range(double x, double min, double max, string name) { if (!double.IsFinite(x) || x < min || x > max) throw new ConfigurationException($"{name}: expected {min}–{max}."); }
    private static void ValidateRules(AlertRules r) { Range(r.CpuHigh, 5, 100, "cpuHigh"); Range(r.MemoryHigh, 5, 100, "memoryHigh"); Range(r.DiskHigh, 5, 100, "diskHigh"); Range(r.Hysteresis, 1, Math.Min(r.CpuHigh, Math.Min(r.MemoryHigh, r.DiskHigh)), "hysteresis"); Range(r.HighSeconds, 0, 3600, "highSeconds"); Range(r.RecoverySeconds, 0, 3600, "recoverySeconds"); }
    private static void ValidatePatterns(string[] x) { if (x is null || x.Length > 32 || x.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 64 || p.Any(c => !char.IsAsciiLetterOrDigit(c) && "*?_.:@-".IndexOf(c) < 0))) throw new ConfigurationException("Invalid interface patterns."); }
}
public sealed class ConfigurationException(string message) : Exception(message);
public static class ConfigFile
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };
    public static AppConfiguration Parse(string json) { if (json.Length > 1_048_576) throw new ConfigurationException("Configuration is too large."); var c = JsonSerializer.Deserialize<AppConfiguration>(json, Json) ?? throw new ConfigurationException("Empty configuration."); c.Validate(); return c; }
    public static AppConfiguration Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1_048_576) throw new ConfigurationException("Configuration is too large.");
        return Parse(File.ReadAllText(path));
    }
    public static void Save(string path, AppConfiguration config) { config.Validate(); AtomicFile.Write(path, JsonSerializer.Serialize(config, Json), true); }
}
public static class AtomicFile
{
    public static void Write(string path, string content, bool backup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(content); writer.Flush(); stream.Flush(true); } if (File.Exists(path)) File.Replace(temp, path, backup ? path + ".bak" : null); else File.Move(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
