using MessagingRuntime.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessagingRuntime.Tests;

public sealed class AutoReplyAuthorizationTests
{
    [Fact]
    public void OnlyCurrentEnabledExactOutboxCanSend()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Pooling=False");
            db.Open();
            using var sql = db.CreateCommand();
            sql.CommandText = """
                CREATE TABLE reply_rules(id TEXT,provider TEXT,conversation TEXT,enabled INTEGER,revision INTEGER,status TEXT);
                CREATE TABLE reply_jobs(rule_id TEXT,request_id TEXT,provider TEXT,conversation TEXT,text TEXT,state TEXT,expires_at REAL,rule_revision INTEGER);
                INSERT INTO reply_rules VALUES('r','wechat-main','c',1,2,'sending');
                INSERT INTO reply_jobs VALUES('r','req','wechat-main','c','hello','sending',9999999999,2);
                """;
            sql.ExecuteNonQuery();
            var auth = new AutoReplyAuthorization(path);
            var request = new SendRequest("req", "auto-reply", "wechat-main", "c", "hello");
            Assert.Equal("approved", auth.Evaluate(request));
            Assert.Throws<MessagingException>(() => auth.Evaluate(request with { Text = "other" }));
            Assert.Throws<MessagingException>(() => auth.Evaluate(request with { ConversationId = "other" }));
            Assert.Throws<MessagingException>(() => auth.Evaluate(request with { AgentId = "local-agent" }));
            foreach (var change in new[] { "enabled=0", "revision=3", "status='blocked'" })
            {
                sql.CommandText = "UPDATE reply_rules SET enabled=1,revision=2,status='sending'; UPDATE reply_rules SET " + change;
                sql.ExecuteNonQuery();
                Assert.Throws<MessagingException>(() => auth.Evaluate(request));
            }
            sql.CommandText = "UPDATE reply_rules SET enabled=1,revision=2,status='sending'; UPDATE reply_jobs SET expires_at=1";
            sql.ExecuteNonQuery();
            Assert.Throws<MessagingException>(() => auth.Evaluate(request));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingDatabaseFailsClosed()
    {
        var auth = new AutoReplyAuthorization(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"));
        Assert.Throws<MessagingException>(() => auth.Evaluate(new("req", "auto-reply", "wechat-main", "c", "hello")));
    }
}
