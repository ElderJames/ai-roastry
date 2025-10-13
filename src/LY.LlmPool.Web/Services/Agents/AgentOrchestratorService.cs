using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services.Tools;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// Agent 编排服务：根据 App 的 OrchestrationMode 选择合适的策略执行多 Agent 交互。
/// </summary>
public class AgentOrchestratorService
{
    private readonly IChatClientService _chatClientService;
    private readonly ToolProviderService _toolProviderService;
    private readonly ILogger<AgentOrchestratorService> _logger;

    public AgentOrchestratorService(
        IChatClientService chatClientService, 
        ToolProviderService toolProviderService,
        ILogger<AgentOrchestratorService> logger)
    {
        _chatClientService = chatClientService;
        _toolProviderService = toolProviderService;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        LlmApp app,
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> userMessages,
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
            _toolProviderService,
            (cfg, msgs, tools) => _chatClientService.SendMessageAsync(cfg, msgs, tools),
            (cfg, msgs, tools) => _chatClientService.SendStreamingMessageAsync(cfg, msgs, tools),
            onProgress,
            ct);
    }
}

