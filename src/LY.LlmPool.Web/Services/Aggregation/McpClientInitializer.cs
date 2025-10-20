using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

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
                    logger?.LogError(ex, "Failed to initialize MCP client '{ServerId}': {ErrorMessage}",
                        serverId, ex.Message);
                    
                    // 🎯 创建一个专门的 Activity 来记录初始化失败的详细错误信息
                    // 这样错误详情可以通过分布式追踪系统传播
                    using var errorActivity = ActivitySource.StartActivity(
                        "mcp.client.initialize.error",
                        ActivityKind.Internal);
                    
                    if (errorActivity != null)
                    {
                        // 收集完整的异常信息
                        var errorDetails = new StringBuilder();
                        errorDetails.AppendLine($"[MCP Client Init Error] Failed to initialize '{serverId}'");
                        errorDetails.AppendLine($"Server Name: {config.Name}");
                        errorDetails.AppendLine($"Server Type: {config.Type}");
                        errorDetails.AppendLine($"Exception Type: {ex.GetType().FullName}");
                        errorDetails.AppendLine($"Message: {ex.Message}");
                        
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            errorDetails.AppendLine($"Stack Trace:");
                            errorDetails.AppendLine(ex.StackTrace);
                        }
                        
                        // 内部异常
                        if (ex.InnerException != null)
                        {
                            errorDetails.AppendLine($"\nInner Exception: {ex.InnerException.GetType().FullName}");
                            errorDetails.AppendLine($"Inner Message: {ex.InnerException.Message}");
                            if (!string.IsNullOrEmpty(ex.InnerException.StackTrace))
                            {
                                errorDetails.AppendLine($"Inner Stack Trace:");
                                errorDetails.AppendLine(ex.InnerException.StackTrace);
                            }
                        }
                        
                        // 添加详细的标签
                        errorActivity.SetStatus(ActivityStatusCode.Error, ex.Message);
                        errorActivity.AddTag("error.type", ex.GetType().FullName);
                        errorActivity.AddTag("error.message", ex.Message);
                        errorActivity.AddTag("error.stack_trace", ex.StackTrace ?? "");
                        errorActivity.AddTag("error.full_details", errorDetails.ToString());
                        
                        errorActivity.AddTag("mcp.server.id", serverId);
                        errorActivity.AddTag("mcp.server.name", config.Name);
                        errorActivity.AddTag("mcp.server.type", config.Type);
                        errorActivity.AddTag("mcp.server.command", config.Command ?? "");
                        
                        if (ex.InnerException != null)
                        {
                            errorActivity.AddTag("error.has_inner_exception", true);
                            errorActivity.AddTag("error.inner_exception_type", ex.InnerException.GetType().FullName);
                            errorActivity.AddTag("error.inner_exception_message", ex.InnerException.Message);
                            errorActivity.AddTag("error.inner_stack_trace", ex.InnerException.StackTrace ?? "");
                        }
                    }
                }
            }
        ).ConfigureAwait(false);

        return clientWrappers.ToList();
    }
}
