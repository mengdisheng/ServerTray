using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using ServerTray.Core;

namespace ServerTray.App;

public static class Text
{
    private static readonly ResourceManager Resources = new("ServerTray.App.Resources.Strings", typeof(Text).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string Add => Get("Add"); public static string Settings => Get("Settings"); public static string Import => Get("Import"); public static string Export => Get("Export");
    public static string Refresh => Get("Refresh"); public static string Overview => Get("Overview"); public static string Alerts => Get("Alerts"); public static string Edit => Get("Edit");
    public static string Cpu => Get("Cpu"); public static string Memory => Get("Memory"); public static string Disk => Get("Disk");
    public static string Search => Get("Search"); public static string SearchHint => Get("SearchHint"); public static string DemoBanner => Get("DemoBanner");
    public static string VerifyHost => Get("VerifyHost"); public static string MoveUp => Get("MoveUp"); public static string MoveDown => Get("MoveDown"); public static string Tagline => Get("Tagline");
    public static string Status(ConnectionStatus status) => Get("Status" + status);
}
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class ActionCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
public static class Display
{
    public static string Percent(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
    public static string Bytes(double? value)
    {
        if (value is not { } n || !double.IsFinite(n)) return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"]; var i = 0;
        while (Math.Abs(n) >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return n.ToString(n >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
    }
    public static string Rate(double? n) => n is null ? "—" : Bytes(n) + "/s";
    public static string Duration(double? seconds) => seconds is { } s ? $"{(int)(s / 86400)}d {(int)(s % 86400 / 3600)}h {(int)(s % 3600 / 60)}m" : "—";
}
public sealed class CredentialVault : ICredentialStore
{
    private readonly string path; private readonly object gate = new(); private Dictionary<string, string> entries;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ServerTray.credentials.v1");
    public CredentialVault(string root) { path = Path.Combine(root, "credentials.json"); entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(BoundedFiles.Read(path, 16_777_216)) ?? [] : []; }
    public Credential? Get(string id)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var value)) return null;
            byte[]? clear = null;
            try { clear = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser); return JsonSerializer.Deserialize<Credential>(clear); }
            catch (Exception e) when (e is CryptographicException or FormatException or JsonException) { return null; }
            finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
        }
    }
    public void Put(string id, Credential credential)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(credential);
        try { lock (gate) { entries[id] = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); AtomicFile.Write(path, JsonSerializer.Serialize(entries)); } }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Remove(string id) { lock (gate) { entries.Remove(id); AtomicFile.Write(path, JsonSerializer.Serialize(entries)); } }
}
public sealed class KnownHosts : IHostKeyStore
{
    private readonly string path; private readonly object gate = new(); private Dictionary<string, string> entries;
    public KnownHosts(string root) { path = Path.Combine(root, "known-hosts.json"); entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(BoundedFiles.Read(path, 1_048_576)) ?? [] : []; }
    private static string Key(string host, int port) => host.ToLowerInvariant() + ":" + port;
    public string? Get(string host, int port) { lock (gate) return entries.GetValueOrDefault(Key(host, port)); }
    public void Trust(HostKeyChallenge challenge) { lock (gate) { entries[Key(challenge.Host, challenge.Port)] = challenge.Fingerprint; AtomicFile.Write(path, JsonSerializer.Serialize(entries)); } }
}
public sealed class RollingLog(string root)
{
    private readonly object gate = new();
    public void Write(string code, string? serverId = null)
    {
        lock (gate)
        {
            try
            {
                var p = Path.Combine(root, "servertray.log");
                if (File.Exists(p) && new FileInfo(p).Length >= 1_048_576) File.Move(p, p + ".1", true);
                File.AppendAllText(p, $"{DateTimeOffset.UtcNow:O} {code} {serverId}\n");
            }
            catch (IOException) { }
        }
    }
}
internal static class BoundedFiles
{
    public static string Read(string path, int maxBytes)
    {
        if (new FileInfo(path).Length > maxBytes) throw new IOException("Local data file exceeds its size limit.");
        return File.ReadAllText(path);
    }
}
