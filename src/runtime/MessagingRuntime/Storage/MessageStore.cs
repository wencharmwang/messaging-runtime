using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MessagingRuntime.Storage;

/// <summary>Local, immutable observation snapshots. Cursors survive refresh and process restart.</summary>
public sealed class MessageStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public MessageStore(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS snapshots (
                id TEXT PRIMARY KEY, provider TEXT NOT NULL, conversation TEXT NOT NULL,
                created TEXT NOT NULL, complete INTEGER NOT NULL, coverage TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (
                snapshot TEXT NOT NULL REFERENCES snapshots(id), ordinal INTEGER NOT NULL,
                content TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(snapshot, ordinal));
            CREATE TABLE IF NOT EXISTS heads (
                provider TEXT NOT NULL, conversation TEXT NOT NULL, snapshot TEXT NOT NULL REFERENCES snapshots(id),
                PRIMARY KEY(provider, conversation));
            CREATE TABLE IF NOT EXISTS provider_revisions (provider TEXT PRIMARY KEY, revision TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sends (
                id TEXT PRIMARY KEY, payload TEXT NOT NULL, status TEXT NOT NULL, receipt TEXT);
            CREATE TABLE IF NOT EXISTS audit (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, payload TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public string SaveSnapshot(string provider, string conversation, MessageBatch batch)
    {
        lock (_gate)
        {
            if (batch.Messages.Any(m => m.Provider != provider || m.ConversationId != conversation))
                throw new MessagingException("provider_contract_error", "Provider returned messages from a different conversation.");
            var id = RuntimeJson.Hash(new { provider, conversation, batch.IsComplete, batch.Coverage,
                messages = batch.Messages.Select(m => m with { ObservedAt = null }) });
            using var tx = _db.BeginTransaction();
            var now = DateTimeOffset.UtcNow;
            Execute("INSERT OR IGNORE INTO snapshots VALUES ($id,$p,$c,$at,$complete,$coverage)", tx,
                ("$id", id), ("$p", provider), ("$c", conversation), ("$at", now.ToString("O")),
                ("$complete", batch.IsComplete ? 1 : 0), ("$coverage", batch.Coverage));
            for (var i = 0; i < batch.Messages.Count; i++)
            {
                var message = batch.Messages[i];
                if (string.IsNullOrEmpty(message.Id)) message = message with { Id = $"observation:{id}:{i}" };
                message = message with { ObservedAt = message.ObservedAt ?? now };
                Execute("INSERT OR IGNORE INTO messages VALUES ($s,$n,$c,$j)", tx,
                    ("$s", id), ("$n", i), ("$c", message.Content), ("$j", Json(message)));
            }
            Execute("INSERT INTO heads VALUES ($p,$c,$s) ON CONFLICT(provider,conversation) DO UPDATE SET snapshot=excluded.snapshot", tx,
                ("$p", provider), ("$c", conversation), ("$s", id));
            tx.Commit();
            return id;
        }
    }

    public void EnsureProviderRevision(string provider,string revision)
    {
        lock(_gate)
        {
            if(Scalar("SELECT revision FROM provider_revisions WHERE provider=$p",("$p",provider)) as string == revision) return;
            using var tx=_db.BeginTransaction();
            Execute("DELETE FROM heads WHERE provider=$p",tx,("$p",provider));
            Execute("INSERT INTO provider_revisions VALUES ($p,$r) ON CONFLICT(provider) DO UPDATE SET revision=excluded.revision",
                tx,("$p",provider),("$r",revision));
            tx.Commit();
        }
    }

    public bool HasSnapshot(string provider, string conversation)
    {
        lock (_gate) return Scalar("SELECT snapshot FROM heads WHERE provider=$p AND conversation=$c",
            ("$p", provider), ("$c", conversation)) is string;
    }

    private sealed record Cursor(string Snapshot, int Ordinal);
    private static string Encode(Cursor value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Json(value)));
    private static Cursor Decode(string value)
    {
        try
        {
            if (value.Length > 1024) throw new FormatException();
            return JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value), RuntimeJson.Options)
                ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new MessagingException("invalid_cursor", "Use a cursor returned by get_messages."); }
    }

    public MessagePage GetPage(string provider, string conversation, int limit = 50, string? before = null, string? after = null)
    {
        if (limit is < 1 or > 500 || (before is not null && after is not null))
            throw new MessagingException("invalid_arguments", "limit must be 1–500; before and after are mutually exclusive.");
        lock (_gate)
        {
            var cursor = before is not null ? Decode(before) : after is not null ? Decode(after) : null;
            var id = cursor?.Snapshot ?? Scalar("SELECT snapshot FROM heads WHERE provider=$p AND conversation=$c",
                ("$p", provider), ("$c", conversation)) as string;
            using var meta = Command("SELECT created,complete,coverage FROM snapshots WHERE id=$s AND provider=$p AND conversation=$c",
                null, ("$s", id ?? ""), ("$p", provider), ("$c", conversation));
            using var reader = meta.ExecuteReader();
            if (!reader.Read()) throw new MessagingException(cursor is null ? "not_synced" : "invalid_cursor",
                "No matching snapshot. Sync the conversation first, or use its own cursor.");
            var at = DateTimeOffset.Parse(reader.GetString(0));
            var complete = reader.GetInt32(1) == 1;
            var coverage = reader.GetString(2);
            reader.Close();
            var count = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM messages WHERE snapshot=$s", ("$s", id!)));
            if (cursor is not null && (cursor.Ordinal < 0 || cursor.Ordinal >= count))
                throw new MessagingException("invalid_cursor", "Cursor position is outside this snapshot.");
            var start = after is not null ? cursor!.Ordinal + 1 : Math.Max(0, (cursor?.Ordinal ?? count) - limit);
            var end = before is not null ? cursor!.Ordinal : Math.Min(count, start + limit);
            using var cmd = Command("SELECT payload FROM messages WHERE snapshot=$s AND ordinal >= $start AND ordinal < $end ORDER BY ordinal",
                null, ("$s", id!), ("$start", start), ("$end", end));
            using var rows = cmd.ExecuteReader();
            var result = new List<Message>();
            while (rows.Read()) result.Add(Parse<Message>(rows.GetString(0)));
            return new(result, id!, result.Count > 0 ? Encode(new(id!, start)) : null,
                result.Count > 0 ? Encode(new(id!, end - 1)) : null, start > 0, end < count, complete, coverage, at);
        }
    }

    public IReadOnlyList<Message> Search(string provider, string query, string? conversation, int limit)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT m.payload FROM heads h JOIN messages m ON m.snapshot=h.snapshot
                WHERE h.provider=$p AND ($c IS NULL OR h.conversation=$c)
                AND instr(lower(m.content),lower($q))>0
                ORDER BY h.conversation,m.ordinal LIMIT $limit
                """, null, ("$p", provider), ("$c", conversation), ("$q", query), ("$limit", limit));
            using var reader = cmd.ExecuteReader();
            var result = new List<Message>();
            while (reader.Read()) result.Add(Parse<Message>(reader.GetString(0)));
            return result;
        }
    }

    public SendOutcome RegisterSend(SendRequest request, string initialStatus)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Execute("INSERT OR IGNORE INTO sends(id,payload,status) VALUES($id,$p,$s)", tx,
                ("$id", request.RequestId), ("$p", Json(request)), ("$s", initialStatus));
            using var cmd = Command("SELECT payload,status,receipt FROM sends WHERE id=$id", tx, ("$id", request.RequestId));
            using var reader = cmd.ExecuteReader();
            reader.Read();
            if (Parse<SendRequest>(reader.GetString(0)) != request)
                throw new MessagingException("idempotency_conflict", "request_id is already bound to different content, recipient, or agent.");
            var outcome = new SendOutcome(request.RequestId, reader.GetString(1), reader.IsDBNull(2) ? null : Parse<SendReceipt>(reader.GetString(2)));
            reader.Close();
            tx.Commit();
            return outcome;
        }
    }

    public SendRequest? FindSendRequest(string id)
    {
        lock (_gate)
        {
            var json = Scalar("SELECT payload FROM sends WHERE id=$id", ("$id", id)) as string;
            return json is null ? null : Parse<SendRequest>(json);
        }
    }

    public (SendRequest Request, string Status) ReviewSend(string id)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT payload,status FROM sends WHERE id=$id", null, ("$id", id));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new MessagingException("request_not_found", "Unknown send request.");
            return (Parse<SendRequest>(reader.GetString(0)), reader.GetString(1));
        }
    }

    // Local operator only. There is deliberately no approval MCP tool.
    public void DecideSend(string id, bool approve)
    {
        lock (_gate)
        {
            if (Execute("UPDATE sends SET status=$s WHERE id=$id AND status='pending'", null,
                ("$s", approve ? "approved" : "rejected"), ("$id", id)) != 1)
                throw new MessagingException("invalid_request_state", "Only a pending request can be approved or rejected.");
            var request = ReviewSend(id).Request;
            Audit(new(DateTimeOffset.UtcNow, "local_operator", approve ? "approve_send" : "reject_send",
                request.Provider, request.ConversationId, approve ? "approved" : "rejected", id));
        }
    }

    public bool ClaimSend(string id)
    {
        lock (_gate) return Execute("UPDATE sends SET status='sending' WHERE id=$id AND status='approved'", null, ("$id", id)) == 1;
    }

    // The caller must RegisterSend first to verify the immutable payload. Terminal states never change.
    public void AuthorizeUserRequest(string id)
    {
        lock (_gate)
        {
            if (Execute("UPDATE sends SET status='approved' WHERE id=$id AND status='pending'", null, ("$id", id)) == 1)
            {
                var request = ReviewSend(id).Request;
                Audit(new(DateTimeOffset.UtcNow, request.AgentId, "user_directed_authorization",
                    request.Provider, request.ConversationId, "approved", id));
            }
        }
    }

    public void FinishSend(string id, string status, SendReceipt? receipt = null)
    {
        lock (_gate) Execute("UPDATE sends SET status=$s,receipt=$r WHERE id=$id AND status='sending'", null,
            ("$s", status), ("$r", receipt is null ? null : Json(receipt)), ("$id", id));
    }

    public void Audit(AuditEntry entry)
    {
        lock (_gate) Execute("INSERT INTO audit(payload) VALUES($j)", null, ("$j", Json(entry)));
    }
    public IReadOnlyList<AuditEntry> RecentAudit(int limit = 100)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT payload FROM audit ORDER BY seq DESC LIMIT $n", null, ("$n", limit));
            using var rows = cmd.ExecuteReader();
            var result = new List<AuditEntry>();
            while (rows.Read()) result.Add(Parse<AuditEntry>(rows.GetString(0)));
            return result;
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, RuntimeJson.Options);
    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, RuntimeJson.Options)!;
    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Key, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private int Execute(string sql, SqliteTransaction? tx, params (string Key, object? Value)[] parameters)
    { using var cmd = Command(sql, tx, parameters); return cmd.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string Key, object? Value)[] parameters)
    { using var cmd = Command(sql, null, parameters); return cmd.ExecuteScalar(); }
    public void Dispose() { lock (_gate) _db.Dispose(); }
}
