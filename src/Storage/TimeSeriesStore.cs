using System.Data;
using IndustrialProtocolAssistant.Core;
using Microsoft.Data.Sqlite;

namespace IndustrialProtocolAssistant.Storage;

/// <summary>
/// 轻量时序数据存储（SQLite）。
/// 将采集到的 TagValue 按 (tagId, ts, value, quality) 落库，供历史曲线查询。
/// 注意：ts 一律以 UTC 的 "O" 格式存储，查询参数也必须转 UTC 后再比较——
/// SQLite 对 TEXT 时间戳做的是字符串比较，混用时区偏移（如本地 +08:00）会查不到数据。
/// </summary>
public sealed class TimeSeriesStore : IDisposable
{
    private readonly SqliteConnection _conn;
    // 保护连接：Save 由后台消费线程调用，查询可能来自 UI 线程，SQLite 单连接不允许并发访问
    private readonly object _gate = new();

    public TimeSeriesStore(string dbPath = "industrial.db")
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tag_samples (
                tag_id TEXT NOT NULL,
                ts TEXT NOT NULL,
                value_real REAL,
                value_text TEXT,
                quality INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tag_ts ON tag_samples(tag_id, ts);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Save(TagValue tv)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO tag_samples(tag_id, ts, value_real, value_text, quality) VALUES($tag,$ts,$vr,$vt,$q)";
            cmd.Parameters.AddWithValue("$tag", tv.TagId);
            cmd.Parameters.AddWithValue("$ts", tv.Timestamp.ToUniversalTime().ToString("O"));
            if (tv.Value is double or float or int or long or short or ushort or uint)
                cmd.Parameters.AddWithValue("$vr", Convert.ToDouble(tv.Value));
            else cmd.Parameters.AddWithValue("$vr", DBNull.Value);
            // null 值必须转 DBNull.Value，否则 SqliteParameter.Bind 抛 "Value must be set."
            cmd.Parameters.AddWithValue("$vt", (object?)tv.Value?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$q", (int)tv.Quality);
            cmd.ExecuteNonQuery();
        }
    }

    public IEnumerable<(DateTimeOffset Ts, double Value)> QueryRecent(string tagId, int limit = 200)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT ts, value_real FROM tag_samples WHERE tag_id=$tag AND value_real IS NOT NULL ORDER BY ts DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$tag", tagId);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<(DateTimeOffset, double)>();
            while (reader.Read())
                list.Add((DateTimeOffset.Parse(reader.GetString(0)), reader.GetDouble(1)));
            return list;
        }
    }

    /// <summary>按时间区间查询历史数值（升序），供历史曲线界面使用。</summary>
    public IEnumerable<(DateTimeOffset Ts, double Value)> QueryRange(string tagId, DateTimeOffset from, DateTimeOffset to, int limit = 5000)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT ts, value_real FROM tag_samples
                WHERE tag_id=$tag AND ts >= $from AND ts <= $to AND value_real IS NOT NULL
                ORDER BY ts ASC LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$tag", tagId);
            // 与存储统一为 UTC 后再做字符串比较，避免本地时区偏移导致区间错位查不到数据
            cmd.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$to", to.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            var list = new List<(DateTimeOffset, double)>();
            while (reader.Read())
                list.Add((DateTimeOffset.Parse(reader.GetString(0)), reader.GetDouble(1)));
            return list;
        }
    }

    public void Dispose() => _conn.Dispose();
}
