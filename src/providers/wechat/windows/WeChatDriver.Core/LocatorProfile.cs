using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatDriver.Core;

/// <summary>目标微信版本的定位器 Profile。自检不通过不得进入发送状态。</summary>
public sealed class LocatorProfile
{
    [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "";
    [JsonPropertyName("clientVersion")] public string ClientVersion { get; set; } = "";
    [JsonPropertyName("dpi")] public int Dpi { get; set; } = 100;
    [JsonPropertyName("windowBounds")] public WindowBounds? WindowBounds { get; set; }
    [JsonPropertyName("requiredCapabilities")] public List<string> RequiredCapabilities { get; set; } = new();
    [JsonPropertyName("uiaSelectors")] public Dictionary<string, string> UiaSelectors { get; set; } = new();
    [JsonPropertyName("fallbackRegions")] public FallbackRegions? FallbackRegions { get; set; }
    [JsonPropertyName("sendStrategy")] public string SendStrategy { get; set; } = "unknown";

    public static LocatorProfile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LocatorProfile>(json, JsonOptions);
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record WindowBounds(int Width, int Height);

public sealed record FallbackRegions(LatestMessageArea LatestMessageArea);

public sealed record LatestMessageArea(double X, double Y, double Width, double Height);
