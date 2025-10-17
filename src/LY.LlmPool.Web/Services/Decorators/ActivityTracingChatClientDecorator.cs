using System.Diagnostics;
using Microsoft.Extensions.AI;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Services.Decorators;

/// <summary>
/// ChatClient 装饰器 - 自动注入 Activity 追踪
/// 
/// 职责:
/// 1. 自动在流式调用前后管理 Activity 上下文
/// 2. 在 yield return 后自动恢复 Activity.Current (解决 .NET Issue #47802)
/// 3. 消除业务代码中手动操作 Activity 的需要
/// 
/// 设计模式: Decorator Pattern
/// </summary>
public class ActivityTracingChatClientDecorator : DelegatingChatClient
{
    private readonly ILogger<ActivityTracingChatClientDecorator> _logger;
    private readonly ActivityTraceService? _activityTraceService;

    public ActivityTracingChatClientDecorator(
        IChatClient innerClient,
        ILogger<ActivityTracingChatClientDecorator> logger,
        ActivityTraceService? activityTraceService = null)
        : base(innerClient)
    {
        _logger = logger;
        _activityTraceService = activityTraceService;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 🎯 捕获调用开始时的 Activity
        var startActivity = Activity.Current;
        
        if (startActivity != null)
        {
            _logger.LogDebug(
                "🎬 [ActivityTracing] 流式调用开始 | Activity: {ActivityName} | TraceId: {TraceId}",
                startActivity.OperationName,
                startActivity.TraceId
            );
            
            // 保存到 ChatActivityContext 供工具调用使用
            ChatActivityContext.SetChatActivity(startActivity);
        }

        var yieldCount = 0;

        await foreach (var update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            yieldCount++;
            
            // 🔑 关键修复: 在每次 yield return 后恢复 Activity.Current
            // 这是 Microsoft.Extensions.AI 内部使用的模式 (见 OpenTelemetryChatClient.cs:202)
            if (startActivity != null && Activity.Current != startActivity)
            {
                _logger.LogTrace(
                    "🔄 [ActivityTracing] 恢复 Activity.Current (yield #{Count}) | Before: {Before} → After: {After}",
                    yieldCount,
                    Activity.Current?.OperationName ?? "NULL",
                    startActivity.OperationName
                );
                
                Activity.Current = startActivity;
            }

            yield return update;
        }

        _logger.LogDebug(
            "🎬 [ActivityTracing] 流式调用完成 | Total yields: {Count}",
            yieldCount
        );
    }

    public override async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // 🎯 捕获调用开始时的 Activity
        var startActivity = Activity.Current;
        
        if (startActivity != null)
        {
            _logger.LogDebug(
                "🎬 [ActivityTracing] 非流式调用开始 | Activity: {ActivityName} | TraceId: {TraceId}",
                startActivity.OperationName,
                startActivity.TraceId
            );
            
            ChatActivityContext.SetChatActivity(startActivity);
        }

        var result = await base.GetResponseAsync(chatMessages, options, cancellationToken);

        _logger.LogDebug(
            "🎬 [ActivityTracing] 非流式调用完成"
        );

        return result ?? throw new InvalidOperationException("ChatResponse is null");
    }
}

/// <summary>
/// 装饰器扩展方法
/// </summary>
public static class ActivityTracingChatClientExtensions
{
    /// <summary>
    /// 为 ChatClient 添加自动 Activity 追踪
    /// 使用装饰器模式,无需修改原有业务逻辑
    /// </summary>
    public static IChatClient UseActivityTracing(
        this IChatClient client,
        IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<ActivityTracingChatClientDecorator>>();
        var activityTraceService = serviceProvider.GetService<ActivityTraceService>();
        
        return new ActivityTracingChatClientDecorator(client, logger, activityTraceService);
    }
}
