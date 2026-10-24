using System.Text.Json;

namespace ServerTray.Core;

public static class ConfigurationDiff
{
    // Configuration contains no credentials. Bound text so imported names and
    // tags cannot turn the review dialog into an unbounded allocation.
    public static string[] Describe(AppConfiguration previous, AppConfiguration next, int limit = 80)
    {
        var lines = new List<string>(); var omitted = 0; limit = Math.Clamp(limit, 1, 200);
        var before = JsonSerializer.SerializeToElement(previous, ConfigFile.Json);
        var after = JsonSerializer.SerializeToElement(next, ConfigFile.Json);
        foreach (var item in after.EnumerateObject().Where(p => p.Name != "servers"))
            Compare(item.Name, before.GetProperty(item.Name), item.Value);
        foreach (var old in previous.Servers.Where(p => next.Servers.All(n => n.Id != p.Id))) Add("− " + old.Name);
        foreach (var server in next.Servers)
        {
            var old = previous.Servers.FirstOrDefault(p => p.Id == server.Id);
            if (old is null) { Add("+ " + server.Name + " (" + server.Host + ")"); continue; }
            var oldJson = JsonSerializer.SerializeToElement(old, ConfigFile.Json);
            foreach (var item in JsonSerializer.SerializeToElement(server, ConfigFile.Json).EnumerateObject())
                Compare(server.Name + "." + item.Name, oldJson.GetProperty(item.Name), item.Value);
        }
        if (omitted > 0) lines.Add("… +" + omitted);
        return lines.ToArray();
        void Compare(string name, JsonElement a, JsonElement b)
        {
            if (a.GetRawText() == b.GetRawText()) return;
            if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in b.EnumerateObject()) Compare(name + "." + child.Name, a.GetProperty(child.Name), child.Value);
                return;
            }
            Add(name + ": " + Short(a) + " → " + Short(b));
        }
        static string Short(JsonElement e) { var s = e.ToString().ReplaceLineEndings(" "); return s.Length <= 140 ? s : s[..140] + "…"; }
        void Add(string s) { if (lines.Count < limit) lines.Add(s); else omitted++; }
    }
}
