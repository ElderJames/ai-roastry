using System.Text.Json.Serialization;

namespace LY.LlmPool.Web.Data;

public enum LlmType
{
    OpenAI,
    DeepSeek,
    Qwen,
    Custom
}

public class LlmConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public LlmType Type { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public Dictionary<string, string> AdditionalHeaders { get; set; } = new();
    
    [JsonIgnore]
    public bool IsBusy { get; set; }
}

public class LlmConfigGroup
{
    public LlmType Type { get; set; }
    public List<LlmConfig> Configs { get; set; } = new();
}

public class LlmPoolOptions
{
    public const string SectionName = "LlmPool";
    public List<LlmConfig> Configs { get; set; } = new();
} 