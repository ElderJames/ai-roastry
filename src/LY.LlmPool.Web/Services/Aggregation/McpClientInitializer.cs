using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Services.Aggregation;

internal static class McpClientInitializer
{
    private static readonly ActivitySource ActivitySource = new("LY.LlmPool.Web");

    public static async Task<List<McpClientWrapper>> InitializeClientsAsync(
        Dictionary<string, McpServerConfigDto> mcpServers,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        var clientWrappers = new ConcurrentBag<McpClientWrapper>();

        // 优化：预先过滤启用的服务器，减少不必要的并发任务
        var enabledServers = mcpServers
            .Where(kv => kv.Value.Enabled.GetValueOrDefault(true))
            .ToList();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(
            enabledServers,
            parallelOptions,
            async (serverConfig, ct) =>
            {
                var serverId = serverConfig.Key;
                var config = serverConfig.Value;

                try
                {
                    var clientWrapper = new McpClientWrapper(serverId, config, loggerFactory);
                    await clientWrapper.InitializeAsync().ConfigureAwait(false);
                    clientWrappers.Add(clientWrapper);
                }
                catch (Exception ex)
                {
                    var logger = loggerFactory?.CreateLogger(typeof(McpClientInitializer));
                    
                    // 🎯 创建一个专门的 Activity 来记录初始化失败的详细错误信息
                    // 继承父 Activity 的 conversationId (如果存在)
                    using var errorActivity = ActivitySource.StartActivity(
                        "mcp.client.initialize.error",
                        ActivityKind.Internal);
                    
                    if (errorActivity != null)
                    {
                        // 🎯 如果父 Activity 有 conversationId，子 Activity 会自动继承
                        // 但为了确保，我们显式检查并设置
                        var parentConversationId = Activity.Current?.Parent?.GetTagItem(ActivityExtensions.GenAIConversationId) as string;
                        if (!string.IsNullOrEmpty(parentConversationId))
                        {
                            errorActivity.SetTag(ActivityExtensions.GenAIConversationId, parentConversationId);
                        }
                        
                        // 记录错误详情
                        errorActivity.SetStatus(ActivityStatusCode.Error, ex.Message);
                        errorActivity.SetTag("exception.type", ex.GetType().FullName);
                        errorActivity.SetTag("exception.message", ex.Message);
                        errorActivity.SetTag("exception.stacktrace", ex.StackTrace ?? "");
                        
                        errorActivity.SetTag("mcp.server.id", serverId);
                        errorActivity.SetTag("mcp.server.name", config.Name);
                        errorActivity.SetTag("mcp.server.type", config.Type);
                        errorActivity.SetTag("mcp.server.command", config.Command ?? "");
                        
                        if (ex.InnerException != null)
                        {
                            errorActivity.SetTag("exception.inner_type", ex.InnerException.GetType().FullName);
                            errorActivity.SetTag("exception.inner_message", ex.InnerException.Message);
                            errorActivity.SetTag("exception.inner_stacktrace", ex.InnerException.StackTrace ?? "");
                        }
                    }
                    
                    logger?.LogError(ex, 
                        "❌ Failed to initialize MCP client '{ServerId}' (ConversationId: {ConversationId}): {ErrorMessage}",
                        serverId, 
                        errorActivity?.GetTagItem(ActivityExtensions.GenAIConversationId),
                        ex.Message);
                }
            }
        ).ConfigureAwait(false);

        return clientWrappers.ToList();
    }
}
