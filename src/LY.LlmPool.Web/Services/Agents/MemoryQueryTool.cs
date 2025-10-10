using System.Text.Json;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 记忆查询工具：从存储的记忆中查询相关上下文。
/// </summary>
public class MemoryQueryTool : IAgentTool
{
    private readonly ContextMemoryStore _memoryStore;

    public string Id { get; }
    public string Name => "memory_query";
    public string? Description => "Query stored conversation summaries and context";

    public JsonElement? ParametersSchema => JsonSerializer.Deserialize<JsonElement>(@"{
        ""type"": ""object"",
        ""properties"": {
            ""conversation_id"": { ""type"": ""string"", ""description"": ""Unique identifier for the conversation"" },
            ""query"": { ""type"": ""string"", ""description"": ""Optional query to filter results"" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Maximum number of results to return"", ""default"": 5 }
        },
        ""required"": [""conversation_id""]
    }");

    public MemoryQueryTool(string id, ContextMemoryStore memoryStore)
    {
        Id = id;
        _memoryStore = memoryStore;
    }

    public (bool ok, string? error) Validate(JsonElement? args)
    {
        if (args is not JsonElement je || je.ValueKind != JsonValueKind.Object)
        {
            return (false, "Arguments must be a JSON object");
        }

        if (!je.TryGetProperty("conversation_id", out var convId) || convId.GetString() is not string convIdStr || string.IsNullOrWhiteSpace(convIdStr))
        {
            return (false, "conversation_id is required and must be a non-empty string");
        }

        if (je.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
        {
            if (limit.TryGetInt32(out var limitVal) && (limitVal < 1 || limitVal > 50))
            {
                return (false, "limit must be between 1 and 50");
            }
        }

        return (true, null);
    }

    public async Task<string> ExecuteAsync(JsonElement? args, CancellationToken ct = default)
    {
        var (ok, error) = Validate(args);
        if (!ok)
        {
            throw new ArgumentException($"Validation failed: {error}");
        }

        var je = args!.Value;
        var conversationId = je.GetProperty("conversation_id").GetString()!;
        var limit = 5;

        if (je.TryGetProperty("limit", out var limitProp) && limitProp.TryGetInt32(out var limitVal))
        {
            limit = Math.Max(1, Math.Min(50, limitVal));
        }

        var memories = await _memoryStore.QueryMemoriesAsync(conversationId, limit);

        if (memories.Count == 0)
        {
            return "No stored memories found for this conversation.";
        }

        var result = new System.Text.StringBuilder();
        result.AppendLine($"Found {memories.Count} memory entries:");
        result.AppendLine();

        foreach (var memory in memories.OrderByDescending(m => m.CreatedAt))
        {
            result.AppendLine($"[{memory.CreatedAt:yyyy-MM-dd HH:mm:ss}] {memory.Summary}");
            if (!string.IsNullOrWhiteSpace(memory.Metadata))
            {
                result.AppendLine($"  Metadata: {memory.Metadata}");
            }
            result.AppendLine();
        }

        return result.ToString().Trim();
    }
}