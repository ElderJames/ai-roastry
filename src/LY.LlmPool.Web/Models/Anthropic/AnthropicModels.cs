using System.Text.Json.Serialization;
using System.Text.Json;

namespace LY.LlmPool.Web.Models.Anthropic;

public class MessagesRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("messages")]
    public List<AnthropicMessage> Messages { get; set; } = new();

    [JsonPropertyName("system")]
    public JsonElement? System { get; set; }

    [JsonPropertyName("stop_sequences")]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; } = false;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; } = 1.0f;

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("tools")]
    public List<AnthropicTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public Dictionary<string, object>? ToolChoice { get; set; }

    [JsonPropertyName("thinking")]
    public ThinkingConfig? Thinking { get; set; }

    // Internal property to store original model name
    [JsonIgnore]
    public string? OriginalModel { get; set; }
}

public class TokenCountRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<AnthropicMessage> Messages { get; set; } = new();

    [JsonPropertyName("system")]
    public JsonElement? System { get; set; }

    [JsonPropertyName("tools")]
    public List<AnthropicTool>? Tools { get; set; }

    [JsonPropertyName("thinking")]
    public ThinkingConfig? Thinking { get; set; }

    [JsonPropertyName("tool_choice")]
    public Dictionary<string, object>? ToolChoice { get; set; }

    // Internal property to store original model name
    [JsonIgnore]
    public string? OriginalModel { get; set; }
}

public class AnthropicMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public object Content { get; set; } = string.Empty;
}

public class AnthropicTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public Dictionary<string, object> InputSchema { get; set; } = new();
}

public class ThinkingConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public class MessagesResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public List<ContentBlock> Content { get; set; } = new();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }

    [JsonPropertyName("usage")]
    public Usage Usage { get; set; } = new();
}

public class TokenCountResponse
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }
}

public class ContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    public Dictionary<string, object>? Input { get; set; }
}

public class Usage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int CacheReadInputTokens { get; set; }
}

public class StreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public class MessageStartEvent : StreamEvent
{
    [JsonPropertyName("message")]
    public MessagesResponse Message { get; set; } = new();

    public MessageStartEvent()
    {
        Type = "message_start";
    }
}

public class MessageDeltaEvent : StreamEvent
{
    [JsonPropertyName("delta")]
    public Dictionary<string, object> Delta { get; set; } = new();

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }

    public MessageDeltaEvent()
    {
        Type = "message_delta";
    }
}

public class ContentBlockStartEvent : StreamEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("content_block")]
    public ContentBlock ContentBlock { get; set; } = new();

    public ContentBlockStartEvent()
    {
        Type = "content_block_start";
    }
}

public class ContentBlockDeltaEvent : StreamEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public Dictionary<string, object> Delta { get; set; } = new();

    public ContentBlockDeltaEvent()
    {
        Type = "content_block_delta";
    }
}

public class ContentBlockStopEvent : StreamEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    public ContentBlockStopEvent()
    {
        Type = "content_block_stop";
    }
}

public class MessageStopEvent : StreamEvent
{
    public MessageStopEvent()
    {
        Type = "message_stop";
    }
}
