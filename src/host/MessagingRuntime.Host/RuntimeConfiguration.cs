using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MessagingRuntime.Host;

public sealed record ProviderConfiguration(string Id, string Type, string? AccountId = null, string? DisplayName = null);
public sealed record RuntimeConfiguration
{
    public string AgentId { get; init; } = "local-agent";
    public string? DataDirectory { get; init; }
    public RuntimePolicy Policy { get; init; } = new();
    public string ApprovalUi { get; init; } = "desktop";
    public string WechatWriteMode { get; init; } = "background";
    public string? AutoReplyDatabase { get; init; }
    public string? AutoReplyTokenFile { get; init; }
    public IReadOnlyList<ProviderConfiguration> Providers { get; init; } = [new("wechat-main", "wechat")];

    public static RuntimeConfiguration Load(string? path)
    {
        var options = new JsonSerializerOptions(RuntimeJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var config = path is null ? new RuntimeConfiguration()
            : JsonSerializer.Deserialize<RuntimeConfiguration>(File.ReadAllText(path), options)
                ?? throw new ArgumentException("Empty runtime configuration.");
        if (string.IsNullOrWhiteSpace(config.AgentId) || config.AgentId.Length > 100 || config.Policy is null || config.Providers is null)
            throw new ArgumentException("agent_id, policy and providers must be valid.");
        if (!Enum.IsDefined(config.Policy.SendMode) || config.Policy.MaxTextLength is < 1 or > 10000)
            throw new ArgumentException("Invalid send policy.");
        if (config.ApprovalUi is not ("desktop" or "manual"))
            throw new ArgumentException("approval_ui must be desktop or manual.");
        if (config.WechatWriteMode is not ("background" or "foreground"))
            throw new ArgumentException("wechat_write_mode must be background or foreground.");
        if ((config.AutoReplyDatabase is null) != (config.AutoReplyTokenFile is null))
            throw new ArgumentException("auto_reply_database and auto_reply_token_file must be configured together.");
        if (config.AutoReplyDatabase is not null && path is not null)
            config = config with {
                AutoReplyDatabase = Path.GetFullPath(config.AutoReplyDatabase, Path.GetDirectoryName(Path.GetFullPath(path))!),
                AutoReplyTokenFile = Path.GetFullPath(config.AutoReplyTokenFile!, Path.GetDirectoryName(Path.GetFullPath(path))!) };
        if (config.Providers.Any(p => p is null || !Regex.IsMatch(p.Id ?? "", "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,99}$") ||
            p.Type is not ("wechat" or "whatsapp" or "telegram" or "line" or "kakaotalk")))
            throw new ArgumentException("Provider ID or type is invalid.");
        if (config.Providers.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != config.Providers.Count)
            throw new ArgumentException("Duplicate provider IDs.");
        if (config.Providers.Count(p => p.Type == "wechat") > 1)
            throw new ArgumentException("The current Windows driver supports one active WeChat account per desktop. Use a distinct runtime data directory when switching accounts.");
        if (config.DataDirectory is not null && path is not null)
            config = config with { DataDirectory = Path.GetFullPath(config.DataDirectory, Path.GetDirectoryName(Path.GetFullPath(path))!) };
        return config;
    }
}
