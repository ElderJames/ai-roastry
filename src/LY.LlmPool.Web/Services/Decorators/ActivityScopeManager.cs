using System.Diagnostics;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Services.Decorators;

/// <summary>
/// Activity 作用域管理器
/// 提供统一的 Activity 生命周期管理,替代散落各处的手动 Activity 创建
/// 
/// 设计模式: Factory + Scope Pattern
/// 使用方式: using var scope = ActivityScopeManager.CreateToolScope(...)
/// </summary>
public static class ActivityScopeManager
{
    /// <summary>
    /// 创建 App Tool 执行的 Activity 作用域
    /// 自动处理父 Activity 查找、Activity 创建、异常处理
    /// </summary>
    public static IActivityScope CreateToolScope(
        string toolName,
        string? modelId = null,
        Dictionary<string, object?>? parameters = null,
        IServiceProvider? serviceProvider = null)
    {
        return new AppToolActivityScope(toolName, modelId, parameters, serviceProvider);
    }

    /// <summary>
    /// 创建 App 执行的 Activity 作用域
    /// </summary>
    public static IActivityScope CreateAppScope(
        string appName,
        string appType,
        string? modelId = null)
    {
        return new AppExecutionActivityScope(appName, appType, modelId);
    }

    /// <summary>
    /// 创建 Server Request 的 Activity 作用域
    /// </summary>
    public static IActivityScope CreateServerRequestScope(
        string route,
        string? appName = null,
        ActivityContext parentContext = default)
    {
        return new ServerRequestActivityScope(route, appName, parentContext);
    }
}

/// <summary>
/// Activity 作用域接口
/// 提供统一的 Dispose 模式,自动清理资源
/// </summary>
public interface IActivityScope : IDisposable
{
    Activity? Activity { get; }
    
    /// <summary>
    /// 记录成功响应
    /// </summary>
    void RecordSuccess(string? finishReason = null, Dictionary<string, object?>? tags = null);
    
    /// <summary>
    /// 记录错误
    /// </summary>
    void RecordError(Exception exception);
}

/// <summary>
/// App Tool Activity 作用域实现
/// </summary>
internal class AppToolActivityScope : IActivityScope
{
    private readonly Activity? _activity;
    private readonly ILogger? _logger;

    public Activity? Activity => _activity;

    public AppToolActivityScope(
        string toolName,
        string? modelId,
        Dictionary<string, object?>? parameters,
        IServiceProvider? serviceProvider)
    {
        _logger = serviceProvider?.GetService<ILogger<AppToolActivityScope>>();
        
        // 🎯 自动查找父 Activity
        Activity? parentActivity = null;
        
        // 方法 1: 从 ChatActivityContext 获取
        var savedActivity = ChatActivityContext.GetChatActivity();
        if (savedActivity != null && serviceProvider != null)
        {
            var activityTraceService = serviceProvider.GetService<ActivityTraceService>();
            if (activityTraceService != null)
            {
                var traceId = savedActivity.TraceId.ToString();
                parentActivity = activityTraceService.GetChatActivityByTraceId(traceId);
                
                _logger?.LogDebug(
                    "🔍 [ActivityScope] 从 ChatActivityContext 查找父 Activity: {ParentName}",
                    parentActivity?.OperationName ?? "NULL"
                );
            }
        }
        
        // 方法 2: Fallback 到 Activity.Current
        if (parentActivity == null)
        {
            parentActivity = Activity.Current;
            
            if (parentActivity != null)
            {
                _logger?.LogDebug(
                    "🔍 [ActivityScope] 使用 Activity.Current 作为父: {ParentName}",
                    parentActivity.OperationName
                );
            }
        }
        
        // 🎯 使用 ActivityExtensions 创建 Activity
        _activity = ActivityExtensions.StartAppToolActivity(
            appName: toolName,
            modelId: modelId,
            parameters: parameters,
            parentActivity: parentActivity
        );
        
        // 🎯 传递 ConversationId (从父 Activity 继承)
        if (_activity != null && parentActivity != null)
        {
            var conversationId = parentActivity.GetTagItem(ActivityExtensions.GenAIConversationId)?.ToString();
            if (!string.IsNullOrEmpty(conversationId))
            {
                _activity.AddTag(ActivityExtensions.GenAIConversationId, conversationId);
                
                _logger?.LogDebug(
                    "🔗 [ActivityScope] 传递 ConversationId={ConversationId} 到子 Activity",
                    conversationId
                );
            }
            
            _logger?.LogDebug(
                "✅ [ActivityScope] 创建 Tool Activity: {ActivityName} | Parent: {ParentName}",
                _activity.OperationName,
                _activity.Parent?.OperationName ?? "NULL"
            );
        }
    }

    public void RecordSuccess(string? finishReason = null, Dictionary<string, object?>? tags = null)
    {
        if (_activity == null) return;

        _activity.RecordResponse(
            responseId: null,
            modelId: _activity.GetTagItem("gen_ai.request.model")?.ToString(),
            finishReason: finishReason ?? "stop"
        );
        
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                _activity.AddTag(tag.Key, tag.Value);
            }
        }
        
        _activity.SetStatus(ActivityStatusCode.Ok);
        
        _logger?.LogDebug(
            "✅ [ActivityScope] 记录成功: {ActivityName}",
            _activity.OperationName
        );
    }

    public void RecordError(Exception exception)
    {
        if (_activity == null) return;

        _activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        _activity.AddTag("error.type", exception.GetType().Name);
        _activity.AddTag("error.message", exception.Message);
        
        _logger?.LogError(
            exception,
            "❌ [ActivityScope] 记录错误: {ActivityName}",
            _activity.OperationName
        );
    }

    public void Dispose()
    {
        _activity?.Dispose();
    }
}

/// <summary>
/// App Execution Activity 作用域实现
/// </summary>
internal class AppExecutionActivityScope : IActivityScope
{
    private readonly Activity? _activity;

    public Activity? Activity => _activity;

    public AppExecutionActivityScope(string appName, string appType, string? modelId)
    {
        _activity = ActivityExtensions.StartAppExecutionActivity(appName, appType, modelId);
    }

    public void RecordSuccess(string? finishReason = null, Dictionary<string, object?>? tags = null)
    {
        if (_activity == null) return;
        
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                _activity.AddTag(tag.Key, tag.Value);
            }
        }
        
        _activity.SetStatus(ActivityStatusCode.Ok);
    }

    public void RecordError(Exception exception)
    {
        if (_activity == null) return;
        
        _activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        _activity.AddTag("error.type", exception.GetType().Name);
        _activity.AddTag("error.message", exception.Message);
    }

    public void Dispose()
    {
        _activity?.Dispose();
    }
}

/// <summary>
/// Server Request Activity 作用域实现
/// </summary>
internal class ServerRequestActivityScope : IActivityScope
{
    private readonly Activity? _activity;

    public Activity? Activity => _activity;

    public ServerRequestActivityScope(string route, string? appName, ActivityContext parentContext)
    {
        _activity = ActivityExtensions.StartServerRequestActivity(route, appName, parentContext);
    }

    public void RecordSuccess(string? finishReason = null, Dictionary<string, object?>? tags = null)
    {
        if (_activity == null) return;
        
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                _activity.AddTag(tag.Key, tag.Value);
            }
        }
        
        _activity.SetStatus(ActivityStatusCode.Ok);
    }

    public void RecordError(Exception exception)
    {
        if (_activity == null) return;
        
        _activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        _activity.AddTag("error.type", exception.GetType().Name);
        _activity.AddTag("error.message", exception.Message);
    }

    public void Dispose()
    {
        _activity?.Dispose();
    }
}
