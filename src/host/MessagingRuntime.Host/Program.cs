using System.Text;
using System.Text.Json;
using MessagingRuntime.Mcp;
using MessagingRuntime.Providers;
using MessagingRuntime.Providers.WeChat;
using MessagingRuntime.Providers.WeChat.Windows;
using MessagingRuntime.Storage;
using WeChatDriver.Automation;
using WeChatDriver.Windows;
using Microsoft.Extensions.Hosting;

namespace MessagingRuntime.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var dpi = new DpiAwarenessScope();
        try { return Run(args); }
        catch (MessagingException ex) { Console.Error.WriteLine($"{ex.Code}: {ex.Message}"); return 1; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException)
        { Console.Error.WriteLine("Configuration or local storage error. Check --config, --data-dir and command arguments."); return 1; }
        catch { Console.Error.WriteLine("Runtime startup failed; check local .NET/Windows configuration."); return 1; }
    }

    private static int Run(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("""
                Messaging Runtime 0.2
                  --stdio (default) | --http [--port 8766]
                  --config <runtime.json> [--data-dir <directory>]
                  --review-request <UUID> | --approve-request <UUID> | --reject-request <UUID>
                  --audit
                HTTP requires MESSAGING_RUNTIME_TOKEN (32+ characters). Approval commands are local operator commands,
                never MCP tools. Review the exact request, approve it, then retry the original MCP send request.
                """);
            return 0;
        }
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--stdio" or "--http" or "--audit")
            { if (!flags.Add(arg)) throw new ArgumentException("Duplicate option."); }
            else if (arg is "--config" or "--data-dir" or "--port" or "--review-request" or "--approve-request" or "--reject-request")
            {
                if (++i >= args.Length || args[i].StartsWith("--") || !values.TryAdd(arg, args[i]))
                    throw new ArgumentException("Missing or duplicate option.");
            }
            else throw new ArgumentException("Unknown option.");
        }
        if (flags.Contains("--stdio") && flags.Contains("--http")) throw new ArgumentException("Choose one transport.");
        var operations = new[] { "--review-request", "--approve-request", "--reject-request" }.Where(values.ContainsKey).ToArray();
        if (operations.Length + (flags.Contains("--audit") ? 1 : 0) > 1) throw new ArgumentException("Choose one local operation.");
        var config = RuntimeConfiguration.Load(values.GetValueOrDefault("--config"));
        var data = values.GetValueOrDefault("--data-dir") ?? config.DataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MessagingRuntime");
        using var store = new MessageStore(Path.Combine(data, "messages.db"));
        if (flags.Contains("--audit"))
        {
            Console.WriteLine(JsonSerializer.Serialize(store.RecentAudit(), RuntimeJson.Options));
            return 0;
        }
        if (operations.Length == 1)
        {
            var operation = operations[0];
            if (!Guid.TryParseExact(values[operation], "D", out var parsed)) throw new ArgumentException("Expected UUID.");
            var id = parsed.ToString("D");
            var review = store.ReviewSend(id);
            Console.WriteLine(JsonSerializer.Serialize(new { request = review.Request, status = review.Status }, RuntimeJson.Options));
            if (operation != "--review-request")
            {
                store.DecideSend(id, operation == "--approve-request");
                Console.WriteLine(operation == "--approve-request" ? "Approved. Retry the identical MCP request to execute." : "Rejected.");
            }
            return 0;
        }

        var port = values.TryGetValue("--port", out var portString) ? int.Parse(portString) : 0;
        if (port is < 0 or > 65535) throw new ArgumentException("Invalid port.");
        // One Runtime owns this desktop; approval/cache inspection commands above do not acquire it.
        using var desktop = new Mutex(false, "Local\\MessagingRuntime.WeChatDesktop");
        var hasWeChat = config.Providers.Any(p => p.Type == "wechat");
        var ownsDesktop = false;
        if (hasWeChat)
        {
            try { ownsDesktop = desktop.WaitOne(0); }
            catch (AbandonedMutexException) { ownsDesktop = true; }
            if (!ownsDesktop) throw new MessagingException("desktop_busy", "Another Messaging Runtime owns this WeChat desktop.");
        }
        try
        {
            using var providers = new ProviderManager();
            foreach (var provider in config.Providers)
                providers.Register(provider.Type == "wechat"
                    ? new WeChatProvider(new WindowsWeChatDriver(() => new OcrInputBackend(backgroundWriting: config.WechatWriteMode == "background")), provider.Id, provider.AccountId, provider.DisplayName ?? "WeChat")
                    : new UnconfiguredProvider(provider.Id, provider.Type));
            foreach(var info in providers.List()) store.EnsureProviderRevision(info.Id,providers.Get(info.Id).SnapshotRevision);
            var notifier = config.ApprovalUi == "desktop" ? new DesktopApprovalNotifier(store) : null;
            var operationGate = new object();
            var service = new MessagingService(providers, store, config.Policy, config.AgentId, notifier, operationGate: operationGate);
            var server = new MessagingMcpServer(service);
            // Keep the protocol stream independent of any legacy driver Console.WriteLine calls.
            using var protocolOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            Console.SetOut(Console.Error);
            if (flags.Contains("--http"))
            {
                var token = Environment.GetEnvironmentVariable("MESSAGING_RUNTIME_TOKEN") ?? "";
                MessagingMcpServer? worker = null;
                string? workerToken = null;
                if (config.AutoReplyDatabase is not null)
                {
                    workerToken = File.ReadAllText(config.AutoReplyTokenFile!).Trim();
                    var authorization = new AutoReplyAuthorization(config.AutoReplyDatabase);
                    // backgroundReads: this is the only caller that is a background agent. Its reads
                    // yield while the person is working in WeChat; every other MCP client on this
                    // host is answering a person who just asked for something, and must not be
                    // refused because of their own activity.
                    // 用**配置里的策略**，不是硬编码的 ReadOnly。
                    //
                    // worker 通道上来有两类请求：
                    //  - 自动回复（agent 主动）：走下面的 sendAuthorization，要求与某条已启用规则
                    //    的任务完全一致 —— 一点没放松；
                    //  - 用户自己在界面上点的那一下（origin = UserRequest）：授权函数按自动回复的
                    //    规矩拒掉后，MessagingService 会**只在 UserRequest 时**回头问策略，
                    //    这时问的就是这里传进去的策略（配置是 user_directed → 放行）。
                    // 以前这里写死 ReadOnly，于是那次回退等于又撞一次墙，用户点什么都被拒。
                    worker = new MessagingMcpServer(new MessagingService(providers, store, config.Policy,
                        "auto-reply", sendAuthorization: authorization.Evaluate, operationGate: operationGate,
                        backgroundReads: true));
                }
                var app = MessagingTransports.CreateHttp(server, token, port, worker, workerToken);
                try
                {
                    app.StartAsync().GetAwaiter().GetResult();
                    Console.Error.WriteLine($"Messaging MCP listening at {app.Urls.Single()}/mcp (bearer authentication enabled)");
                    app.WaitForShutdownAsync().GetAwaiter().GetResult();
                }
                finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            else
            {
                using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                MessagingTransports.RunStdio(server, input, protocolOutput);
            }
        }
        finally { if (ownsDesktop) desktop.ReleaseMutex(); }
        return 0;
    }
}
