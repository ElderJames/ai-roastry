using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.SemanticKernel;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 编排策略接口:定义如何编排多个 Agent 的交互。
/// </summary>
public interface IOrchestrationStrategy
{
    Task<string> ExecuteAsync(
        LlmApp app,
        IEnumerable<AIChatMessage> userMessages,
        ToolProviderService toolProviderService,
        Func<LlmConfig, List<AIChatMessage>, IEnumerable<KernelFunction>?, Task<ChatResponse>> sendMessage,
        Func<LlmConfig, List<AIChatMessage>, IEnumerable<KernelFunction>?, IAsyncEnumerable<string>> sendStreamingMessage,
        Func<string, string?, int, string, bool, Task>? onProgress = null,
        CancellationToken ct = default);
}
