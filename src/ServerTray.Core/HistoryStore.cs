using Microsoft.Data.Sqlite;

namespace ServerTray.Core;

public sealed record HistoryPoint(long Stamp, double? Cpu, double? Memory, double? Disk, double? Rx, double? Tx, double? Read, double? Write);
public sealed class HistoryStore : IDisposable
{
    private sealed class Accumulator(long minute)
    {
        public long Minute = minute;
        public double[] Sum = new double[7];
        public int[] Count = new int[7];
        public void Add(MetricSample s) { double?[] values = [s.Cpu, s.Memory, s.Disk, s.Rx, s.Tx, s.DiskRead, s.DiskWrite]; for (var i = 0; i < 7; i++) if (values[i] is { } v && double.IsFinite(v)) { Sum[i] += v; Count[i]++; } }
    }
    private readonly string connectionString;
    private readonly Dictionary<string, Accumulator> pending = [];
    private readonly object gate = new();
    private DateTimeOffset lastCleanup = DateTimeOffset.MinValue;
    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString();
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA journal_size_limit=4194304; CREATE TABLE IF NOT EXISTS samples(server TEXT NOT NULL, stamp INTEGER NOT NULL, cpu REAL, memory REAL, disk REAL, rx REAL, tx REAL, rd REAL, wr REAL, PRIMARY KEY(server,stamp)) WITHOUT ROWID; CREATE INDEX IF NOT EXISTS samples_stamp ON samples(stamp);"; command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA cache_size=-2048; PRAGMA mmap_size=0;"; cmd.ExecuteNonQuery(); return c; }
    public void Add(MetricSample sample)
    {
        lock (gate)
        {
            var minute = sample.At.ToUnixTimeSeconds() / 60 * 60;
            if (pending.TryGetValue(sample.ServerId, out var old) && old.Minute != minute) { Write([(sample.ServerId, old)]); pending.Remove(sample.ServerId); }
            if (!pending.TryGetValue(sample.ServerId, out var row)) pending[sample.ServerId] = row = new(minute);
            row.Add(sample);
        }
    }
    private void Write(IEnumerable<(string Id, Accumulator Row)> rows)
    {
        using var c = Open(); using var transaction = c.BeginTransaction();
        foreach (var (id, row) in rows)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "INSERT OR REPLACE INTO samples VALUES($id,$stamp,$c,$m,$d,$r,$t,$rd,$wr)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$stamp", row.Minute);
            string[] keys = ["$c", "$m", "$d", "$r", "$t", "$rd", "$wr"];
            for (var i = 0; i < keys.Length; i++) cmd.Parameters.AddWithValue(keys[i], row.Count[i] > 0 ? row.Sum[i] / row.Count[i] : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void Flush(int retentionDays, DateTimeOffset now)
    {
        lock (gate)
        {
            if (pending.Count > 0) Write(pending.Select(kv => (kv.Key, kv.Value)));
            foreach (var id in pending.Where(kv => kv.Value.Minute < now.AddMinutes(-2).ToUnixTimeSeconds()).Select(kv => kv.Key).ToArray()) pending.Remove(id);
            if (now - lastCleanup < TimeSpan.FromHours(1)) return;
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE stamp < $cutoff; DELETE FROM samples WHERE stamp < (SELECT stamp FROM samples ORDER BY stamp DESC LIMIT 1 OFFSET 999999); PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.Parameters.AddWithValue("$cutoff", (now - TimeSpan.FromDays(Math.Clamp(retentionDays, 1, 30))).ToUnixTimeSeconds()); cmd.ExecuteNonQuery(); lastCleanup = now;
        }
    }
    public HistoryPoint[] Query(string id, int hours, DateTimeOffset now)
    {
        if (hours is not (1 or 6 or 24 or 168)) throw new ArgumentOutOfRangeException(nameof(hours));
        using var c = Open(); using var command = c.CreateCommand(); var bucket = hours <= 6 ? 60 : hours == 24 ? 300 : 600;
        command.CommandText = "SELECT (stamp/$bucket)*$bucket AS t, AVG(cpu), AVG(memory), AVG(disk), AVG(rx), AVG(tx), AVG(rd), AVG(wr) FROM samples WHERE server=$id AND stamp >= $start AND stamp <= $end GROUP BY stamp/$bucket ORDER BY t LIMIT 1200";
        command.Parameters.AddWithValue("$bucket", bucket); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$start", now.AddHours(-hours).ToUnixTimeSeconds()); command.Parameters.AddWithValue("$end", now.ToUnixTimeSeconds());
        using var reader = command.ExecuteReader(); List<HistoryPoint> result = [];
        while (reader.Read()) result.Add(new(reader.GetInt64(0), Number(reader, 1), Number(reader, 2), Number(reader, 3), Number(reader, 4), Number(reader, 5), Number(reader, 6), Number(reader, 7)));
        return result.ToArray();
    }
    private static double? Number(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);
    public void Dispose() { lock (gate) { if (pending.Count > 0) Write(pending.Select(kv => (kv.Key, kv.Value))); pending.Clear(); } }
}
