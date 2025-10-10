using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 编排策略接口:定义如何编排多个 Agent 的交互。
/// </summary>
public interface IOrchestrationStrategy
{
    Task<string> ExecuteAsync(
        LlmApp app,
        IEnumerable<ChatMessage> userMessages,
        Func<LlmConfig, List<ChatMessage>, Task<ChatResponse>> sendMessage,
        Func<LlmConfig, List<ChatMessage>, IAsyncEnumerable<string>> sendStreamingMessage,
        Func<string, string?, int, string, bool, Task>? onProgress = null,
        CancellationToken ct = default);
}
