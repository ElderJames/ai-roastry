using System.Diagnostics;
using System.Text;
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
    private readonly ActivityTraceService _activityTraceService;

    // 使用 AsyncLocal 存储当前请求的根 Activity
    private static readonly AsyncLocal<Activity?> _requestActivity = new();

    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ActivityContextMiddleware(
        RequestDelegate next, 
        ILogger<ActivityContextMiddleware> logger,
        ActivityTraceService activityTraceService)
    {
        _next = next;
        _logger = logger;
        _activityTraceService = activityTraceService;
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

        // 📝 读取请求体 (用于记录和提取 conversation_id)
        string? requestBody = null;
        if (context.Request.Method == "POST" && 
            context.Request.ContentType?.Contains("application/json") == true)
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            requestBody = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
        }

        // 🎯 提取 conversation_id 并存储到 HttpContext.Items（供 ChatClient 使用）
        var conversationId = ExtractConversationId(context, requestBody);
        
        // 🎯 如果没有提取到 ConversationId，尝试从已有的追踪数据推断
        if (string.IsNullOrEmpty(conversationId))
        {
            var currentActivity = Activity.Current;
            if (currentActivity != null)
            {
                conversationId = InferConversationIdFromTraceId(currentActivity.TraceId.ToString());
                if (!string.IsNullOrEmpty(conversationId))
                {
                    _logger.LogDebug("🔍 从 TraceId 推断出 ConversationId: {ConversationId}", conversationId);
                }
            }
        }
        
        if (!string.IsNullOrEmpty(conversationId))
        {
            context.Items["ConversationId"] = conversationId;
            
            // 🎯 【关键修复】立即设置到当前 Activity (HttpRequestIn)
            var currentActivity = Activity.Current;
            if (currentActivity != null)
            {
                currentActivity.AddTag(ActivityExtensions.GenAIConversationId, conversationId);
                _logger.LogInformation(
                    "🔗 [ConversationId 设置] Middleware 设置到根 Activity: {ConversationId} | Activity: {Name} | SpanId: {SpanId}",
                    conversationId,
                    currentActivity.OperationName,
                    currentActivity.SpanId);
            }
            
            _logger.LogDebug("📥 提取到 ConversationId 并存储到 Items: {ConversationId}", conversationId);
        }

        try
        {
            // 🎯 捕获当前请求的 Activity (可能是从 traceparent 创建的)
            var currentActivity = Activity.Current;
            
            if (currentActivity != null)
            {
                _requestActivity.Value = currentActivity;
                
                // 🎯 记录请求信息到 Activity（不再在这里设置 ConversationId）
                await RecordRequestInfoAsync(currentActivity, context, requestBody);
                
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

            // 🎯 检查是否需要拦截响应内容
            // 对于流式响应(SSE)，不能使用 MemoryStream 缓冲，否则会破坏流式输出
            var shouldInterceptResponse = !IsStreamingRequest(context);

            if (shouldInterceptResponse)
            {
                // 📝 拦截响应 (用于记录响应内容)
                var originalBodyStream = context.Response.Body;
                using var responseBodyStream = new MemoryStream();
                context.Response.Body = responseBodyStream;

                try
                {
                    await _next(context);

                    // 🎯 记录响应信息到 Activity
                    await RecordResponseInfoAsync(currentActivity, context, responseBodyStream);

                    // 复制响应到原始流
                    responseBodyStream.Position = 0;
                    await responseBodyStream.CopyToAsync(originalBodyStream);
                }
                finally
                {
                    context.Response.Body = originalBodyStream;
                }
            }
            else
            {
                // 🚀 流式响应：直接传递，不拦截
                _logger.LogDebug("🚀 检测到流式请求，跳过响应拦截");
                await _next(context);
            }
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
    /// 从请求中提取 ConversationId（只提取，不生成）
    /// 优先级: 
    /// 1. HTTP Header "X-Conversation-Id"  
    /// 2. 请求 Body 中的 "conversation_id"
    /// </summary>
    private string? ExtractConversationId(HttpContext context, string? requestBody = null)
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

        // 2. 尝试从请求 Body 提取 (如果已经读取了)
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(requestBody);
                // Only try to extract conversation_id if root element is an object (not array)
                if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object &&
                    jsonDoc.RootElement.TryGetProperty("conversation_id", out var convIdElement))
                {
                    var conversationId = convIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(conversationId))
                    {
                        _logger.LogDebug("📥 ConversationId from Body: {ConversationId}", conversationId);
                        return conversationId;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract conversation_id from request body");
            }
        }

        // 不在这里自动生成，让 ChatClient 生成
        return null;
    }

    /// <summary>
    /// 从 TraceId 推断 ConversationId
    /// 查找同一 TraceId 下已有的 ConversationId（排除自动生成的）
    /// </summary>
    private string? InferConversationIdFromTraceId(string traceId)
    {
        if (string.IsNullOrEmpty(traceId))
            return null;

        try
        {
            // 🔍 从活跃和已完成的追踪中查找同一 TraceId 的节点
            var allTraces = _activityTraceService.GetActiveTraces()
                .Concat(_activityTraceService.GetCompletedTraces())
                .ToList();

            // 🎯 查找同一 TraceId 下有真实 ConversationId 的节点（排除自动生成的）
            var nodeWithConvId = allTraces
                .Where(n => n.TraceId == traceId && 
                           !string.IsNullOrEmpty(n.ConversationId) &&
                           !n.ConversationId.StartsWith("conv-auto-"))
                .OrderBy(n => n.StartTime) // 取最早的节点的 ConversationId
                .FirstOrDefault();

            if (nodeWithConvId != null)
            {
                _logger.LogDebug(
                    "🔍 从 TraceId {TraceId} 推断出 ConversationId: {ConversationId} (来自 SpanId: {SpanId})",
                    traceId,
                    nodeWithConvId.ConversationId,
                    nodeWithConvId.SpanId
                );
                return nodeWithConvId.ConversationId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "推断 ConversationId 时出错 (TraceId: {TraceId})", traceId);
        }

        return null;
    }

    /// <summary>
    /// 检查是否为流式请求
    /// </summary>
    private bool IsStreamingRequest(HttpContext context)
    {
        // 检查请求体中是否包含 "stream": true
        if (context.Request.Method == "POST" &&
            context.Request.ContentType?.Contains("application/json") == true)
        {
            try
            {
                context.Request.Body.Position = 0;
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = reader.ReadToEnd();
                context.Request.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(body))
                {
                    var jsonDoc = JsonDocument.Parse(body);
                    if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object &&
                        jsonDoc.RootElement.TryGetProperty("stream", out var streamElement) &&
                        streamElement.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check streaming request");
            }
        }

        return false;
    }

    /// <summary>
    /// 记录请求信息到 Activity
    /// </summary>
    private async Task RecordRequestInfoAsync(Activity? activity, HttpContext context, string? requestBody)
    {
        if (activity == null || string.IsNullOrWhiteSpace(requestBody))
            return;

        try
        {
            var jsonDoc = JsonDocument.Parse(requestBody);
            if (jsonDoc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            // 记录消息数量
            if (jsonDoc.RootElement.TryGetProperty("messages", out var messagesElement) &&
                messagesElement.ValueKind == JsonValueKind.Array)
            {
                var messageCount = messagesElement.GetArrayLength();
                activity.SetTag("http.request.body.messages.count", messageCount);

                // 记录消息摘要 (截取前200字符)
                var messagesSummary = new List<object>();
                foreach (var msg in messagesElement.EnumerateArray())
                {
                    if (msg.ValueKind != JsonValueKind.Object)
                        continue;

                    var role = msg.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : "unknown";
                    var content = msg.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : "";
                    
                    if (!string.IsNullOrEmpty(content) && content.Length > 200)
                    {
                        content = content.Substring(0, 200) + "...";
                    }

                    messagesSummary.Add(new { role, content });
                }

                var messagesSummaryJson = JsonSerializer.Serialize(messagesSummary, _jsonSerializerOptions);
                activity.SetTag("http.request.body.messages", messagesSummaryJson);

                _logger.LogDebug("� 记录请求信息到 Activity: {Count} messages", messageCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record request info to Activity");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 记录响应信息到 Activity
    /// </summary>
    private async Task RecordResponseInfoAsync(Activity? activity, HttpContext context, MemoryStream responseBodyStream)
    {
        if (activity == null || responseBodyStream.Length == 0)
            return;

        try
        {
            // 只处理成功的 JSON 响应
            if (context.Response.StatusCode != 200 || 
                !context.Response.ContentType?.Contains("application/json") == true)
                return;

            responseBodyStream.Position = 0;
            using var reader = new StreamReader(responseBodyStream, leaveOpen: true);
            var responseBody = await reader.ReadToEndAsync();
            responseBodyStream.Position = 0;

            if (string.IsNullOrWhiteSpace(responseBody))
                return;

            var jsonDoc = JsonDocument.Parse(responseBody);
            if (jsonDoc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            // 提取 choices[0].message.content
            if (jsonDoc.RootElement.TryGetProperty("choices", out var choicesElement) &&
                choicesElement.ValueKind == JsonValueKind.Array &&
                choicesElement.GetArrayLength() > 0)
            {
                var firstChoice = choicesElement[0];
                if (firstChoice.TryGetProperty("message", out var messageElement) &&
                    messageElement.TryGetProperty("content", out var contentElement))
                {
                    var content = contentElement.GetString() ?? "";
                    
                    // 记录响应内容摘要 (截取前500字符)
                    var responseSummary = content.Length > 500 
                        ? content.Substring(0, 500) + "..." 
                        : content;
                    
                    activity.SetTag("http.response.body.content", responseSummary);
                    activity.SetTag("http.response.body.length", content.Length);
                    
                    _logger.LogDebug("📝 记录响应内容到 Activity: {Length} chars", content.Length);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record response info to Activity");
        }
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
