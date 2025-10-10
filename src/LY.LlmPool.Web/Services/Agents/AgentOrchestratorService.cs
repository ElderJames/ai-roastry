using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// Agent 编排服务：根据 App 的 OrchestrationMode 选择合适的策略执行多 Agent 交互。
/// </summary>
public class AgentOrchestratorService
{
    private readonly IChatClientService _chatClientService;
    private readonly ILogger<AgentOrchestratorService> _logger;

    public AgentOrchestratorService(IChatClientService chatClientService, ILogger<AgentOrchestratorService> logger)
    {
        _chatClientService = chatClientService;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        LlmApp app,
        IEnumerable<ChatMessage> userMessages,
        Func<string, string?, int, string, bool, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (app.AppType != "AgentGroup")
        {
            throw new ArgumentException("App must be of type AgentGroup", nameof(app));
        }

        IOrchestrationStrategy strategy = app.OrchestrationMode switch
        {
            OrchestrationMode.Sequential => new SequentialStrategy(),
            OrchestrationMode.GroupChat => new GroupChatStrategy(),
            OrchestrationMode.DAG => new DAGStrategy(),
            _ => new SequentialStrategy() // 默认顺序模式
        };

        return await strategy.ExecuteAsync(
            app,
            userMessages,
            (cfg, msgs) => _chatClientService.SendMessageAsync(cfg, msgs),
            (cfg, msgs) => _chatClientService.SendStreamingMessageAsync(cfg, msgs),
            onProgress,
            ct);
    }
}
