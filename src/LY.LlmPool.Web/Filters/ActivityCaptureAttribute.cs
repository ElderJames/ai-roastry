using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Filters;

/// <summary>
/// Activity 自动捕获过滤器
/// 在 Controller Action 执行前后自动管理 Activity
/// 
/// 职责:
/// 1. 在 Action 执行前,将当前 Activity 保存到 ActivityTraceService
/// 2. 在检测到 FunctionCall 时,自动保存 Activity 上下文
/// 3. 替代 Controller 中手动调用 ChatActivityContext.SetChatActivity
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class ActivityCaptureAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<ActivityCaptureAttribute>>();
        var activityTraceService = context.HttpContext.RequestServices.GetService<ActivityTraceService>();
        
        var currentActivity = Activity.Current;
        var actionName = $"{context.Controller.GetType().Name}.{context.ActionDescriptor.DisplayName}";
        
        if (currentActivity != null)
        {
            logger.LogDebug(
                "🎯 [ActivityCapture] Action 开始: {Action} | Activity: {ActivityName} | TraceId: {TraceId}",
                actionName,
                currentActivity.OperationName,
                currentActivity.TraceId
            );
            
            // 🔑 自动保存 Activity 到 ChatActivityContext (供工具调用时使用)
            ChatActivityContext.SetChatActivity(currentActivity);
        }
        else
        {
            logger.LogWarning(
                "⚠️ [ActivityCapture] Action 开始但 Activity.Current 为 NULL: {Action}",
                actionName
            );
        }

        // 执行 Action
        var executedContext = await next();

        // Action 执行后清理
        if (executedContext.Exception == null)
        {
            logger.LogDebug(
                "✅ [ActivityCapture] Action 完成: {Action}",
                actionName
            );
        }
        else
        {
            logger.LogError(
                executedContext.Exception,
                "❌ [ActivityCapture] Action 异常: {Action}",
                actionName
            );
        }
    }

    public override void OnActionExecuted(ActionExecutedContext context)
    {
        // 可选: Action 完成后清理 ChatActivityContext
        // ChatActivityContext.ClearChatActivity();
        base.OnActionExecuted(context);
    }
}
