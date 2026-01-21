using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Services.PromptCache;

/// <summary>
/// 带有 Prompt 缓存功能的 ChatClient 装饰器
/// 简化版：暂时禁用，等待完整的向量存储实现
/// </summary>
public static class CachedChatClientExtensions
{
    /// <summary>
    /// 为 IChatClient 添加缓存支持（简化版：暂不可用）
    /// </summary>
    public static IChatClient WithPromptCache(
        this IChatClient client,
        PromptCacheService cacheService,
        ILogger logger,
        string? llmConfigId = null,
        string? modelName = null)
    {
        // 简化版：暂时返回原始client，不添加缓存
        // TODO: 实现完整的缓存装饰器
        logger.LogDebug("Prompt cache temporarily disabled - using direct client");
        return client;
    }
}
