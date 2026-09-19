using Microsoft.Data.Sqlite;

namespace MessagingRuntime.Storage;

/// <summary>The private worker can submit only a current, explicitly enabled rule's exact outbox payload.</summary>
public sealed class AutoReplyAuthorization(string database)
{
    public string Evaluate(SendRequest request)
    {
        if (request.AgentId != "auto-reply" || request.ReplyTo is not null || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 1000)
            throw Denied();
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM reply_jobs j JOIN reply_rules r ON r.id=j.rule_id
                WHERE j.request_id=$request AND j.provider=$provider
                  AND (j.conversation=$conversation OR r.title=$conversation)
                  AND j.text=$text AND j.state='sending' AND j.expires_at > $now
                  AND r.enabled=1 AND r.revision=j.rule_revision
                  AND r.provider=j.provider AND r.conversation=j.conversation
                  AND r.status NOT IN ('blocked','needs_user')
                """;
            command.Parameters.AddWithValue("$request", request.RequestId);
            command.Parameters.AddWithValue("$provider", request.Provider);
            command.Parameters.AddWithValue("$conversation", request.ConversationId);
            command.Parameters.AddWithValue("$text", request.Text);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (Convert.ToInt64(command.ExecuteScalar()) == 1) return "approved";
        }
        catch (SqliteException) { throw Denied(); }
        throw Denied();
    }

    private static MessagingException Denied() => new("policy_denied", "Automatic reply requires an enabled rule and its exact unexpired outbox request.");
}
