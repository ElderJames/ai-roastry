using System.Diagnostics;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// HTTP Handler 用于传播 Activity Context 和 ConversationId 到 HTTP Headers
/// 1. 将当前 Activity 的 TraceId 和 SpanId 通过 traceparent header 传递给下游服务
/// 2. 将 ConversationId 通过 X-Conversation-Id header 传递给下游服务
/// </summary>
public class ActivityPropagationHandler : DelegatingHandler
{
    private readonly ILogger<ActivityPropagationHandler> _logger;

    public ActivityPropagationHandler(ILogger<ActivityPropagationHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, 
        CancellationToken cancellationToken)
    {
        var currentActivity = Activity.Current;
        
        if (currentActivity != null)
        {
            // 🎯 1. 传播 traceparent (分布式追踪)
            // 格式: version-traceid-spanid-flags
            // 例如: 00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01
            var traceparent = $"00-{currentActivity.TraceId}-{currentActivity.SpanId}-01";
            
            // 添加到请求 Headers
            request.Headers.TryAddWithoutValidation("traceparent", traceparent);
            
            _logger.LogDebug("注入 traceparent header: {Traceparent} (TraceId={TraceId}, SpanId={SpanId})", 
                traceparent, currentActivity.TraceId, currentActivity.SpanId);
            
            // 如果有 tracestate，也传播
            if (!string.IsNullOrEmpty(currentActivity.TraceStateString))
            {
                request.Headers.TryAddWithoutValidation("tracestate", currentActivity.TraceStateString);
            }
            
            // 🎯 2. 传播 ConversationId (对话关联)
            // 从当前 Activity 的 Tag 中提取 ConversationId
            var conversationId = currentActivity.GetTagItem(ActivityExtensions.GenAIConversationId)?.ToString();
            if (!string.IsNullOrEmpty(conversationId))
            {
                request.Headers.TryAddWithoutValidation("X-Conversation-Id", conversationId);
                
                _logger.LogDebug("🔗 注入 X-Conversation-Id header: {ConversationId}", conversationId);
            }
            else
            {
                _logger.LogDebug("当前 Activity 没有 ConversationId，跳过注入");
            }
        }
        else
        {
            _logger.LogDebug("当前没有 Activity，跳过 traceparent 和 ConversationId 注入");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
