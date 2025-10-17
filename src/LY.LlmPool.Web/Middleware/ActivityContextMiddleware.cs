using System.Diagnostics;
using System.Text.Json;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Middleware;

/// <summary>
/// Activity 上下文中间件
/// 职责:
/// 1. 自动从 HTTP Headers 提取 traceparent
/// 2. 捕获进入 API 的所有请求的 Activity
/// 3. 提取并设置 ConversationId 到 Activity
/// 4. 将 Activity 存储到 AsyncLocal 供后续使用
/// 5. 自动清理 AsyncLocal
/// </summary>
public class ActivityContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ActivityContextMiddleware> _logger;

    // 使用 AsyncLocal 存储当前请求的根 Activity
    private static readonly AsyncLocal<Activity?> _requestActivity = new();

    public ActivityContextMiddleware(RequestDelegate next, ILogger<ActivityContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public static Activity? GetRequestActivity() => _requestActivity.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        
        // 只处理 API 请求 (避免干扰静态资源等)
        if (!path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        try
        {
            // 🎯 捕获当前请求的 Activity (可能是从 traceparent 创建的)
            var currentActivity = Activity.Current;
            
            if (currentActivity != null)
            {
                _requestActivity.Value = currentActivity;
                
                // 🎯 提取或生成 ConversationId (遵循 OpenTelemetry Semantic Conventions)
                // 总是会返回一个有效的 ConversationId (自动生成或客户端提供)
                var conversationId = await ExtractConversationIdAsync(context);
                currentActivity.AddTag(ActivityExtensions.GenAIConversationId, conversationId);
                
                _logger.LogDebug(
                    "🔗 ActivityContextMiddleware: ConversationId={ConversationId} for {Path}",
                    conversationId,
                    path
                );
                
                _logger.LogDebug(
                    "🔗 ActivityContextMiddleware: 捕获 Activity for {Path} | TraceId={TraceId}, SpanId={SpanId}, Name={Name}",
                    path,
                    currentActivity.TraceId,
                    currentActivity.SpanId,
                    currentActivity.OperationName
                );
            }
            else
            {
                _logger.LogWarning(
                    "⚠️ ActivityContextMiddleware: Activity.Current is NULL for {Path}",
                    path
                );
            }

            await _next(context);
        }
        finally
        {
            // 🧹 请求完成后清理 AsyncLocal
            _requestActivity.Value = null;
            
            _logger.LogDebug(
                "🧹 ActivityContextMiddleware: 清理 AsyncLocal for {Path}",
                path
            );
        }
    }

    /// <summary>
    /// 提取或生成 ConversationId
    /// 优先级: 
    /// 1. HTTP Header "X-Conversation-Id"  
    /// 2. 请求 Body 中的 "conversation_id"
    /// 3. 自动生成 (格式: conv-{timestamp}-{guid})
    /// </summary>
    private async Task<string> ExtractConversationIdAsync(HttpContext context)
    {
        // 1. 尝试从 HTTP Header 提取 (推荐方式,符合 OpenTelemetry 惯例)
        if (context.Request.Headers.TryGetValue("X-Conversation-Id", out var headerValue))
        {
            var conversationId = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(conversationId))
            {
                _logger.LogDebug("📥 ConversationId from Header: {ConversationId}", conversationId);
                return conversationId;
            }
        }

        // 2. 尝试从请求 Body 提取 (仅用于 POST 请求)
        if (context.Request.Method == "POST" && 
            context.Request.ContentType?.Contains("application/json") == true)
        {
            try
            {
                // Enable buffering to allow reading the body multiple times
                context.Request.EnableBuffering();
                
                using var reader = new StreamReader(
                    context.Request.Body,
                    leaveOpen: true);
                
                var body = await reader.ReadToEndAsync();
                
                // Reset the stream position for subsequent reads
                context.Request.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(body))
                {
                    var jsonDoc = JsonDocument.Parse(body);
                    if (jsonDoc.RootElement.TryGetProperty("conversation_id", out var convIdElement))
                    {
                        var conversationId = convIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(conversationId))
                        {
                            _logger.LogDebug("📥 ConversationId from Body: {ConversationId}", conversationId);
                            return conversationId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract conversation_id from request body");
            }
        }

        // 3. 自动生成 ConversationId (使用 TraceId 的一部分 + 短 GUID)
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8);
        var autoGeneratedId = $"conv-auto-{traceId.Substring(0, 8)}-{shortGuid}";
        
        _logger.LogDebug(
            "🔧 Auto-generated ConversationId: {ConversationId} (no client-provided ID)",
            autoGeneratedId
        );
        
        return autoGeneratedId;
    }
}

/// <summary>
/// 中间件扩展方法
/// </summary>
public static class ActivityContextMiddlewareExtensions
{
    public static IApplicationBuilder UseActivityContext(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ActivityContextMiddleware>();
    }
}
