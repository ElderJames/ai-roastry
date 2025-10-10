using System.Text.Json;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 上下文提取工具：后台生成对话摘要并存储到记忆中。
/// </summary>
public class ContextExtractorTool : IAgentTool
{
    private readonly ContextMemoryStore _memoryStore;
    private readonly IChatClientService _chatClient;

    public string Id { get; }
    public string Name => "context_extractor";
    public string? Description => "Extract and summarize conversation context for future reference";

    public JsonElement? ParametersSchema => JsonSerializer.Deserialize<JsonElement>(@"{
        ""type"": ""object"",
        ""properties"": {
            ""conversation_id"": { ""type"": ""string"", ""description"": ""Unique identifier for the conversation"" },
            ""content"": { ""type"": ""string"", ""description"": ""The conversation content to summarize"" }
        },
        ""required"": [""conversation_id"", ""content""]
    }");

    public ContextExtractorTool(string id, ContextMemoryStore memoryStore, IChatClientService chatClient)
    {
        Id = id;
        _memoryStore = memoryStore;
        _chatClient = chatClient;
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

        if (!je.TryGetProperty("content", out var content) || content.GetString() is not string contentStr || string.IsNullOrWhiteSpace(contentStr))
        {
            return (false, "content is required and must be a non-empty string");
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
        var content = je.GetProperty("content").GetString()!;

        // 后台执行摘要生成，不阻塞主流程
        _ = Task.Run(async () =>
        {
            try
            {
                var summary = await GenerateSummaryAsync(content, ct);
                await _memoryStore.StoreSummaryAsync(conversationId, summary, "auto-generated");
            }
            catch (Exception ex)
            {
                // 记录错误但不抛出
                Console.WriteLine($"ContextExtractorTool background task failed: {ex.Message}");
            }
        }, ct);

        return "Context extraction started in background. Summary will be available for future queries.";
    }

    private async Task<string> GenerateSummaryAsync(string content, CancellationToken ct)
    {
        // 使用LLM生成摘要
        var messages = new List<ChatMessage>
        {
            new ChatMessage
            {
                Role = "system",
                Content = "You are a helpful assistant that summarizes conversations. Provide a concise summary of the key points and context."
            },
            new ChatMessage
            {
                Role = "user",
                Content = $"Please summarize the following conversation:\n\n{content}"
            }
        };

        // 使用默认配置（简化实现）
        var config = new LlmConfig
        {
            Model = "gpt-3.5-turbo", // 或从配置获取
            ApiKey = "dummy", // 实际应从配置获取
            BaseUrl = "https://api.openai.com/v1"
        };

        var response = await _chatClient.SendMessageAsync(config, messages);
        return response.Message ?? "Summary generation failed";
    }
}