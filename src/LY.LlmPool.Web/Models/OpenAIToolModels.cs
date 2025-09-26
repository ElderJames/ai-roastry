using System.Text.Json.Serialization;

namespace LY.LlmPool.Web.Models;

public class OpenAITool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAIFunction Function { get; set; } = new();
}

public class OpenAIFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // OpenAI parameters schema (JSON Schema fragment)
    [JsonPropertyName("parameters")]
    public Dictionary<string, object>? Parameters { get; set; }
}
