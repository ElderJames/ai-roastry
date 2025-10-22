using System.Diagnostics;
using LY.LlmPool.Web.Services.Aggregation;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web;


public class McpClientStartupService : IHostedService
{
    private static readonly ActivitySource ActivitySource = new("LY.LlmPool.Web");
    
    private readonly McpClientsFactory _mcpClientsFactory;
    private readonly ILogger<McpClientStartupService> _logger;

    public McpClientStartupService(McpClientsFactory mcpClientsFactory, ILogger<McpClientStartupService> logger)
    {
        _mcpClientsFactory = mcpClientsFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 🎯 为启动初始化创建一个专门的 Activity 和 ConversationId
        // 这样即使在后台任务中也能正确追踪整个初始化链路
        using var activity = ActivitySource.StartActivity(
            "mcp.clients.startup.initialize",
            ActivityKind.Internal);
        
        if (activity != null)
        {
            // 为启动初始化生成一个特殊的 ConversationId
            var startupConversationId = $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            activity.SetTag(ActivityExtensions.GenAIConversationId, startupConversationId);
            activity.SetTag("mcp.initialization.phase", "startup");
            activity.SetTag("mcp.initialization.timestamp", DateTime.UtcNow.ToString("O"));
        }
        
        try
        {
            _logger.LogInformation("🚀 Initializing MCP clients on startup... (ConversationId: {ConversationId})", 
                activity?.GetTagItem(ActivityExtensions.GenAIConversationId));
            
            await _mcpClientsFactory.GetOrCreateClientsAsync(cancellationToken);
            
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogInformation("✅ MCP clients initialized successfully on startup.");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // 记录异常详情到 Activity Tags
            activity?.SetTag("exception.type", ex.GetType().FullName);
            activity?.SetTag("exception.message", ex.Message);
            activity?.SetTag("exception.stacktrace", ex.StackTrace);
            
            _logger.LogError(ex, "❌ Failed to initialize MCP clients on startup");
            // You can choose to throw here if client initialization is critical
            // throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping MCP client startup service...");
        return Task.CompletedTask;
    }
}
