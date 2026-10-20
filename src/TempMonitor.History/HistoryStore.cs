using Microsoft.Data.Sqlite;

namespace TempMonitor.History;

/// <summary>One chart point: a time bucket's average, minimum and maximum.</summary>
public readonly record struct HistoryPoint(DateTime Time, float Avg, float Min, float Max);

/// <summary>A logged event: an overheat alert, a stalled fan, "back to normal".</summary>
public sealed record HistoryEvent(DateTime Time, string Kind, string Key, string Level, string Message);

/// <summary>A recorded series, e.g. "cpu:0/temp" labelled "CPU temperature · Intel Core i9-9980HK".</summary>
public sealed record HistoryMetric(string Name, string Label);

/// <summary>
/// Sensor history in SQLite. Two tiers keep it small while still answering "what happened at
/// 14:03" and "how hot did it get this month":
/// <list type="bullet">
/// <item><c>raw</c>: every sample (1 s), kept for <see cref="RawRetention"/>;</item>
/// <item><c>minute</c>: average / min / max per minute, kept for the retention setting (30 days).</item>
/// </list>
/// Samples are buffered and written every few seconds in one transaction (light on SSDs).
/// WAL mode lets the app read while the background monitor writes.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public static readonly TimeSpan RawRetention = TimeSpan.FromHours(2);

    private readonly SqliteConnection _db;
    private readonly Dictionary<string, long> _metricIds = new();
    private readonly List<(long Id, long Ts, float V)> _pendingRaw = new();
    private readonly List<(long Id, long Ts, float Avg, float Min, float Max, int N)> _pendingMinute = new();
    private readonly Dictionary<long, (double Sum, float Min, float Max, int N)> _acc = new();
    private long _accMinute = -1;

    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS metric (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, label TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS raw (metric INTEGER NOT NULL, ts INTEGER NOT NULL, v REAL NOT NULL,
                PRIMARY KEY (metric, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS minute (metric INTEGER NOT NULL, ts INTEGER NOT NULL,
                avg REAL NOT NULL, min REAL NOT NULL, max REAL NOT NULL, n INTEGER NOT NULL,
                PRIMARY KEY (metric, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS event (ts INTEGER NOT NULL, kind TEXT NOT NULL, key TEXT NOT NULL,
                level TEXT NOT NULL, message TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS event_ts ON event (ts);
            """);
    }

    private static long Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();
    private static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    private void Exec(string sql, params (string, object)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private long MetricId(string name, string label)
    {
        if (_metricIds.TryGetValue(name, out var id)) return id;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metric (name, label) VALUES ($n, $l) ON CONFLICT (name) DO UPDATE SET label = excluded.label;
            SELECT id FROM metric WHERE name = $n;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$l", label);
        id = (long)cmd.ExecuteScalar()!;
        return _metricIds[name] = id;
    }

    /// <summary>Adds one sample per metric at <paramref name="time"/> (UTC). Call <see cref="Flush"/> to write.</summary>
    public void Append(DateTime time, IEnumerable<(string Metric, string Label, float Value)> samples)
    {
        var ts = Unix(time);
        var minute = ts / 60 * 60;
        if (minute != _accMinute)
        {
            CloseMinute();
            _accMinute = minute;
        }
        foreach (var (metric, label, value) in samples)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) continue;
            var id = MetricId(metric, label);
            _pendingRaw.Add((id, ts, value));
            _acc[id] = _acc.TryGetValue(id, out var a)
                ? (a.Sum + value, Math.Min(a.Min, value), Math.Max(a.Max, value), a.N + 1)
                : (value, value, value, 1);
        }
    }

    /// <summary>Turns the finished minute's samples into one average/min/max row per metric.</summary>
    private void CloseMinute()
    {
        if (_accMinute < 0) return;
        foreach (var (id, a) in _acc)
            _pendingMinute.Add((id, _accMinute, (float)(a.Sum / a.N), a.Min, a.Max, a.N));
        _acc.Clear();
    }

    /// <summary>
    /// Writes buffered samples. The current minute's summary is written too (and rewritten as the
    /// minute fills up), so charts of long ranges include the last minute.
    /// </summary>
    public void Flush()
    {
        using var tx = _db.BeginTransaction();
        using (var raw = _db.CreateCommand())
        {
            raw.Transaction = tx;
            raw.CommandText = "INSERT OR REPLACE INTO raw (metric, ts, v) VALUES ($m, $t, $v)";
            var (m, t, v) = (raw.Parameters.Add("$m", SqliteType.Integer), raw.Parameters.Add("$t", SqliteType.Integer), raw.Parameters.Add("$v", SqliteType.Real));
            foreach (var s in _pendingRaw)
            {
                (m.Value, t.Value, v.Value) = (s.Id, s.Ts, s.V);
                raw.ExecuteNonQuery();
            }
        }
        using (var min = _db.CreateCommand())
        {
            min.Transaction = tx;
            min.CommandText = "INSERT OR REPLACE INTO minute (metric, ts, avg, min, max, n) VALUES ($m, $t, $a, $lo, $hi, $n)";
            var p = new[] { "$m", "$t", "$a", "$lo", "$hi", "$n" }.Select(n => min.Parameters.Add(n, SqliteType.Real)).ToArray();
            var current = _acc.Select(kv => (kv.Key, _accMinute, (float)(kv.Value.Sum / kv.Value.N), kv.Value.Min, kv.Value.Max, kv.Value.N));
            foreach (var r in _pendingMinute.Concat(_accMinute >= 0 ? current : Enumerable.Empty<(long, long, float, float, float, int)>()))
            {
                (p[0].Value, p[1].Value, p[2].Value, p[3].Value, p[4].Value, p[5].Value) = (r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6);
                min.ExecuteNonQuery();
            }
        }
        tx.Commit();
        _pendingRaw.Clear();
        _pendingMinute.Clear();
    }

    /// <summary>Drops 1-second samples older than 2 hours and minute summaries older than the retention.</summary>
    public void Prune(DateTime now, int retentionDays)
    {
        Exec("DELETE FROM raw WHERE ts < $t", ("$t", Unix(now - RawRetention)));
        Exec("DELETE FROM minute WHERE ts < $t", ("$t", Unix(now.AddDays(-retentionDays))));
        Exec("DELETE FROM event WHERE ts < $t", ("$t", Unix(now.AddDays(-retentionDays))));
    }

    public void AddEvent(DateTime time, string kind, string key, string level, string message) =>
        Exec("INSERT INTO event (ts, kind, key, level, message) VALUES ($t, $k, $key, $l, $m)",
            ("$t", Unix(time)), ("$k", kind), ("$key", key), ("$l", level), ("$m", message));

    /// <summary>Every recorded series with its label.</summary>
    public List<HistoryMetric> Metrics()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT name, label FROM metric ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryMetric>();
        while (r.Read()) list.Add(new HistoryMetric(r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>
    /// A metric between two times as at most <paramref name="maxPoints"/> buckets. Ranges within
    /// the last 2 hours come from 1-second samples, longer ones from the minute summaries.
    /// </summary>
    public List<HistoryPoint> Query(string metric, DateTime from, DateTime to, int maxPoints = 300, DateTime? now = null)
    {
        long f = Unix(from), t = Unix(to);
        var useRaw = UsesRaw(from, to, now);
        var width = (long)BucketWidth(from, to, maxPoints, now).TotalSeconds;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = useRaw
            ? """
              SELECT (r.ts / $w) * $w AS b, avg(r.v), min(r.v), max(r.v) FROM raw r JOIN metric m ON m.id = r.metric
              WHERE m.name = $n AND r.ts BETWEEN $f AND $t GROUP BY b ORDER BY b
              """
            : """
              SELECT (x.ts / $w) * $w AS b, sum(x.avg * x.n) / sum(x.n), min(x.min), max(x.max) FROM minute x JOIN metric m ON m.id = x.metric
              WHERE m.name = $n AND x.ts BETWEEN $f AND $t GROUP BY b ORDER BY b
              """;
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$n", metric);
        cmd.Parameters.AddWithValue("$f", f);
        cmd.Parameters.AddWithValue("$t", t);
        using var r = cmd.ExecuteReader();
        var points = new List<HistoryPoint>();
        while (r.Read())
            points.Add(new HistoryPoint(FromUnix(r.GetInt64(0)), (float)r.GetDouble(1), (float)r.GetDouble(2), (float)r.GetDouble(3)));
        return points;
    }

    /// <summary>
    /// How long a metric was at or above <paramref name="threshold"/> in the range, from the finest
    /// data kept (seconds within the last 2 hours, minute averages beyond), so a short hot spell
    /// isn't averaged away on a 30-day chart.
    /// </summary>
    public TimeSpan TimeAtOrAbove(string metric, DateTime from, DateTime to, float threshold, DateTime? now = null)
    {
        var raw = UsesRaw(from, to, now);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = raw
            ? "SELECT count(*) FROM raw r JOIN metric m ON m.id = r.metric WHERE m.name = $n AND r.ts BETWEEN $f AND $t AND r.v >= $h"
            : "SELECT count(*) FROM minute x JOIN metric m ON m.id = x.metric WHERE m.name = $n AND x.ts BETWEEN $f AND $t AND x.avg >= $h";
        cmd.Parameters.AddWithValue("$n", metric);
        cmd.Parameters.AddWithValue("$f", Unix(from));
        cmd.Parameters.AddWithValue("$t", Unix(to));
        cmd.Parameters.AddWithValue("$h", threshold);
        var count = (long)cmd.ExecuteScalar()!;
        return raw ? TimeSpan.FromSeconds(count) : TimeSpan.FromMinutes(count);
    }

    private static bool UsesRaw(DateTime from, DateTime to, DateTime? now) =>
        from >= (now ?? DateTime.UtcNow) - RawRetention && to - from <= RawRetention;

    /// <summary>How much time each point of <see cref="Query"/> covers for this range.</summary>
    public static TimeSpan BucketWidth(DateTime from, DateTime to, int maxPoints = 300, DateTime? now = null)
    {
        var seconds = (long)Math.Ceiling((to - from).TotalSeconds / Math.Max(1, maxPoints));
        return TimeSpan.FromSeconds(Math.Max(UsesRaw(from, to, now) ? 1 : 60, seconds));
    }

    public List<HistoryEvent> Events(DateTime from, DateTime to)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT ts, kind, key, level, message FROM event WHERE ts BETWEEN $f AND $t ORDER BY ts";
        cmd.Parameters.AddWithValue("$f", Unix(from));
        cmd.Parameters.AddWithValue("$t", Unix(to));
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryEvent>();
        while (r.Read())
            list.Add(new HistoryEvent(FromUnix(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>Deletes all history and events ("Clear history").</summary>
    public void Clear()
    {
        _pendingRaw.Clear();
        _pendingMinute.Clear();
        _acc.Clear();
        Exec("DELETE FROM raw; DELETE FROM minute; DELETE FROM event;");
        // Give the space back; skipped if the background monitor is writing at this moment
        try { Exec("VACUUM"); }
        catch (SqliteException) { }
    }

    public void Dispose()
    {
        try { Flush(); } catch (SqliteException) { }
        _db.Dispose();
    }
}
