using System.Collections.Concurrent;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 上下文记忆存储：存储对话摘要，支持查询。
/// 简单内存实现，后续可扩展为 EF 持久化。
/// </summary>
public class ContextMemoryStore
{
    private readonly ConcurrentDictionary<string, List<MemoryEntry>> _memories = new();

    public class MemoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ConversationId { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? Metadata { get; set; }
    }

    /// <summary>
    /// 存储摘要
    /// </summary>
    public Task StoreSummaryAsync(string conversationId, string summary, string? metadata = null)
    {
        var entry = new MemoryEntry
        {
            ConversationId = conversationId,
            Summary = summary,
            Metadata = metadata
        };

        _memories.AddOrUpdate(
            conversationId,
            new List<MemoryEntry> { entry },
            (key, list) =>
            {
                list.Add(entry);
                return list;
            });

        return Task.CompletedTask;
    }

    /// <summary>
    /// 查询记忆
    /// </summary>
    public Task<List<MemoryEntry>> QueryMemoriesAsync(string conversationId, int limit = 10)
    {
        if (_memories.TryGetValue(conversationId, out var entries))
        {
            return Task.FromResult(entries.OrderByDescending(e => e.CreatedAt).Take(limit).ToList());
        }

        return Task.FromResult(new List<MemoryEntry>());
    }

    /// <summary>
    /// 获取最新摘要
    /// </summary>
    public Task<string?> GetLatestSummaryAsync(string conversationId)
    {
        if (_memories.TryGetValue(conversationId, out var entries))
        {
            var latest = entries.OrderByDescending(e => e.CreatedAt).FirstOrDefault();
            return Task.FromResult(latest?.Summary);
        }

        return Task.FromResult<string?>(null);
    }
}