using Microsoft.Data.Sqlite;
using WeChatDriver.Core;

namespace WeChatDriver.Store;

/// <summary>本地最小状态存储：SQLite。只保存本地、可清除的最小数据。</summary>
public sealed class SqliteStore : ILocalStore
{
    private readonly string _dbPath;
    private readonly string _connString;

    public SqliteStore(string baseDir)
    {
        Directory.CreateDirectory(baseDir);
        _dbPath = Path.Combine(baseDir, "wechat-driver.db");
        _connString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS conversations (
                stable_hint TEXT PRIMARY KEY,
                display_name TEXT,
                unread INTEGER,
                last_fingerprint TEXT,
                observed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS metrics (
                operation_id TEXT,
                key TEXT,
                value REAL,
                PRIMARY KEY (operation_id, key)
            );
            CREATE TABLE IF NOT EXISTS failures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                code INTEGER,
                message TEXT,
                occurred_at TEXT,
                operation_id TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveConversationState(ConversationState state)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations (stable_hint, display_name, unread, last_fingerprint, observed_at)
            VALUES ($h, $n, $u, $f, $o)
            ON CONFLICT(stable_hint) DO UPDATE SET
                display_name = $n, unread = $u, last_fingerprint = $f, observed_at = $o;
            """;
        cmd.Parameters.AddWithValue("$h", state.Key.StableHint);
        cmd.Parameters.AddWithValue("$n", state.Key.DisplayName);
        cmd.Parameters.AddWithValue("$u", (object?)state.UnreadCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)state.LastMessageFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$o", state.LastObservedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void SaveMetric(string operationId, string key, double? value)
    {
        if (value is null) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metrics (operation_id, key, value) VALUES ($o, $k, $v)
            ON CONFLICT(operation_id, key) DO UPDATE SET value = $v;
            """;
        cmd.Parameters.AddWithValue("$o", operationId);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value.Value);
        cmd.ExecuteNonQuery();
    }

    public void SaveFailure(FailureInfo failure)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO failures (code, message, occurred_at, operation_id)
            VALUES ($c, $m, $o, $op);
            """;
        cmd.Parameters.AddWithValue("$c", (int)failure.Code);
        cmd.Parameters.AddWithValue("$m", failure.Message);
        cmd.Parameters.AddWithValue("$o", failure.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$op", (object?)failure.OperationId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FailureInfo> RecentFailures(int limit = 50)
    {
        var result = new List<FailureInfo>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, message, occurred_at, operation_id FROM failures ORDER BY id DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$l", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FailureInfo(
                (FailureCode)reader.GetInt32(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return result;
    }

    public void ClearAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations; DELETE FROM metrics; DELETE FROM failures;";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }
}
