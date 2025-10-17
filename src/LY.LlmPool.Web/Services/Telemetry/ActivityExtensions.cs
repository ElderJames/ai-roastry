using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// Activity 类型枚举
/// </summary>
public enum ActivityType
{
    /// <summary>客户端 HTTP 请求到 LlmPool</summary>
    ClientRequest,
    
    /// <summary>LlmPool 服务端处理请求</summary>
    ServerResponse,
    
    /// <summary>调用 App 执行</summary>
    AppExecution,
    
    /// <summary>调用真实外部 LLM 模型</summary>
    ExternalModel,
    
    /// <summary>Tool 调用（可能嵌套 App）</summary>
    ToolCall
}

/// <summary>
/// Activity 扩展工具类，用于创建自定义 Activity
/// </summary>
public static class ActivityExtensions
{
    private const string SourceName = "LY.LlmPool";
    private static readonly ActivitySource _activitySource = new(SourceName, "1.0.0");

    // OpenTelemetry Semantic Conventions for GenAI
    // See: https://opentelemetry.io/docs/specs/semconv/gen-ai/
    public const string GenAIConversationId = "gen_ai.conversation.id";
    public const string GenAIRequestModel = "gen_ai.request.model";
    public const string GenAIOperationName = "gen_ai.operation.name";
    public const string GenAIResponseId = "gen_ai.response.id";
    public const string GenAIResponseModel = "gen_ai.response.model";
    public const string GenAIResponseFinishReasons = "gen_ai.response.finish_reasons";
    public const string GenAIUsageInputTokens = "gen_ai.usage.input_tokens";
    public const string GenAIUsageOutputTokens = "gen_ai.usage.output_tokens";

    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 创建通用 Activity（不自动启动）
    /// </summary>
    public static Activity? CreateActivity(
        string operationName,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parentContext = default)
    {
        if (parentContext == default)
        {
            // 使用当前 Activity 作为父级
            return _activitySource.CreateActivity(operationName, kind);
        }
        else
        {
            // 使用指定的父级 Context
            return _activitySource.CreateActivity(operationName, kind, parentContext);
        }
    }

    /// <summary>
    /// 创建 LlmPool 服务端处理请求的 Activity
    /// 用于追踪 Controller 接收并处理客户端请求
    /// </summary>
    public static Activity? StartServerRequestActivity(
        string route,
        string? appName = null,
        ActivityContext parentContext = default)
    {
        var activity = CreateActivity(
            $"llmpool.server {route}",
            ActivityKind.Server,
            parentContext);

        if (activity != null)
        {
            activity.Start();
            activity.AddTag("activity.type", ActivityType.ServerResponse.ToString());
            activity.AddTag("llmpool.component", "controller");
            activity.AddTag("http.route", route);
            
            if (!string.IsNullOrEmpty(appName))
            {
                activity.AddTag("app.name", appName);
            }
        }

        return activity;
    }

    /// <summary>
    /// 创建 App 执行的 Activity
    /// 用于追踪 App（Prompt + Config）的完整执行过程
    /// </summary>
    public static Activity? StartAppExecutionActivity(
        string appName,
        string appType,
        string? modelId = null)
    {
        var activity = _activitySource.StartActivity(
            $"llmpool.app {appName}",
            ActivityKind.Internal);

        if (activity != null)
        {
            activity
                .AddTag("activity.type", ActivityType.AppExecution.ToString())
                .AddTag("llmpool.component", "app")
                .AddTag("app.name", appName)
                .AddTag("app.type", appType);

            if (!string.IsNullOrEmpty(modelId))
            {
                activity.AddTag("gen_ai.request.model", modelId);
            }
        }

        return activity;
    }

    /// <summary>
    /// 为 App Tool 调用创建 Activity
    /// 用于追踪通过 Tool 方式调用的嵌套 App
    /// </summary>
    public static Activity? StartAppToolActivity(
        string appName,
        string? modelId = null,
        Dictionary<string, object?>? parameters = null,
        Activity? parentActivity = null)  // 🎯 新增参数: 显式指定父 Activity
    {
        // 🔍 诊断日志: 检查当前 Activity 上下文
        var currentActivity = Activity.Current;
        var effectiveParent = parentActivity ?? currentActivity;  // 优先使用传入的父 Activity
        
        Console.WriteLine($"🔍 StartAppToolActivity for '{appName}':");
        Console.WriteLine($"   Activity.Current: {currentActivity?.OperationName ?? "NULL"}");
        Console.WriteLine($"   Activity.Current.Id: {currentActivity?.Id ?? "NULL"}");
        Console.WriteLine($"   Activity.Current.SpanId: {currentActivity?.SpanId.ToString() ?? "NULL"}");
        Console.WriteLine($"   Explicit Parent: {parentActivity?.OperationName ?? "NULL"}");
        Console.WriteLine($"   Explicit Parent.SpanId: {parentActivity?.SpanId.ToString() ?? "NULL"}");
        Console.WriteLine($"   Effective Parent: {effectiveParent?.OperationName ?? "NULL"}");
        Console.WriteLine($"   Effective Parent.SpanId: {effectiveParent?.SpanId.ToString() ?? "NULL"}");
        
        // 🎯 关键修复: 使用显式传入的父 Activity 或 Activity.Current
        // 如果有父 Activity,创建基于父 Activity 的 ActivityContext
        Activity? activity;
        if (effectiveParent != null)
        {
            var parentContext = new ActivityContext(
                effectiveParent.TraceId,
                effectiveParent.SpanId,
                effectiveParent.ActivityTraceFlags,
                effectiveParent.TraceStateString,
                isRemote: false
            );
            
            activity = _activitySource.StartActivity(
                $"llmpool.tool {appName}",
                ActivityKind.Internal,
                parentContext);
        }
        else
        {
            // 没有父 Activity,创建新的根 Activity
            activity = _activitySource.StartActivity(
                $"llmpool.tool {appName}",
                ActivityKind.Internal);
        }
        
        Console.WriteLine($"   Created Activity: {activity?.OperationName ?? "NULL"}");
        Console.WriteLine($"   Created Activity.ParentSpanId: {activity?.ParentSpanId.ToString() ?? "NULL"}");

        if (activity != null)
        {
            activity
                .AddTag("activity.type", ActivityType.ToolCall.ToString())
                .AddTag("llmpool.component", "tool")
                .AddTag("tool.type", "app")
                .AddTag("app.name", appName)
                .AddTag("tool.name", appName) // 🎯 也设置 tool.name 便于统一提取
                .AddTag("gen_ai.operation.name", "execute_tool");  // ← 改为 execute_tool

            if (!string.IsNullOrEmpty(modelId))
            {
                activity.AddTag("gen_ai.request.model", modelId);
            }

            // 🎯 将 parameters 序列化为 JSON 字符串，同时保留单独的 parameter tags
            if (parameters != null)
            {
                // 设置单个 tool.arguments Tag（JSON 格式）
                var argumentsJson = System.Text.Json.JsonSerializer.Serialize(parameters, _jsonSerializerOptions);
                activity.AddTag("tool.arguments", argumentsJson);
                
                // 也保留单独的 parameter tags（用于细粒度追踪）
                foreach (var param in parameters)
                {
                    activity.AddTag($"tool.parameter.{param.Key}", param.Value?.ToString());
                }
            }
        }

        return activity;
    }

    /// <summary>
    /// 记录模型调用的工具信息到 Activity
    /// 作为 Event 或 Tag 记录,类似 Microsoft.Extensions.AI 的做法
    /// </summary>
    public static void RecordToolCalls(this Activity? activity, IEnumerable<Microsoft.Extensions.AI.FunctionCallContent> toolCalls)
    {
        if (activity == null || !toolCalls.Any()) return;

        // 方式1: 使用 Activity Events (推荐,符合 OpenTelemetry 规范)
        foreach (var toolCall in toolCalls)
        {
            var eventTags = new ActivityTagsCollection
            {
                { "gen_ai.tool.call.id", toolCall.CallId },
                { "gen_ai.tool.call.name", toolCall.Name }
            };

            if (toolCall.Arguments != null)
            {
                var argsJson = System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments, _jsonSerializerOptions);
                eventTags.Add("gen_ai.tool.call.arguments", argsJson);
            }

            activity.AddEvent(new ActivityEvent($"tool_call: {toolCall.Name}", tags: eventTags));
        }

        // 方式2: 也在 Tag 中记录工具调用数量(方便查询和统计)
        activity.AddTag("gen_ai.tool.call.count", toolCalls.Count());
        activity.AddTag("gen_ai.tool.names", string.Join(", ", toolCalls.Select(t => t.Name)));
    }

    /// <summary>
    /// 记录工具执行结果到 Activity
    /// </summary>
    public static void RecordToolResults(this Activity? activity, IEnumerable<Microsoft.Extensions.AI.FunctionResultContent> toolResults)
    {
        if (activity == null || !toolResults.Any()) return;

        foreach (var result in toolResults)
        {
            var eventTags = new ActivityTagsCollection
            {
                { "gen_ai.tool.call.id", result.CallId }
            };

            if (result.Result != null)
            {
                var resultStr = result.Result.ToString();
                if (resultStr != null && resultStr.Length <= 1000) // 限制长度避免过大
                {
                    eventTags.Add("gen_ai.tool.call.result", resultStr);
                }
                else
                {
                    eventTags.Add("gen_ai.tool.call.result", $"[Result too large: {resultStr?.Length ?? 0} chars]");
                }
            }

            if (result.Exception != null)
            {
                eventTags.Add("gen_ai.tool.call.error", result.Exception.Message);
                activity.SetStatus(ActivityStatusCode.Error, $"Tool {result.CallId} failed: {result.Exception.Message}");
            }

            activity.AddEvent(new ActivityEvent($"tool_result: {result.CallId}", tags: eventTags));
        }
    }

    /// <summary>
    /// 创建外部 LLM 模型调用的 Activity
    /// 用于追踪到真实外部模型（OpenAI, Azure, etc.）的 HTTP 调用
    /// 注意：gen_ai.choice Activity 由 Microsoft.Extensions.AI 自动创建
    /// 此方法用于在需要时手动创建额外的追踪
    /// </summary>
    public static Activity? StartExternalModelActivity(
        string modelId,
        string provider,
        string endpoint)
    {
        var activity = _activitySource.StartActivity(
            $"llmpool.external_model {modelId}",
            ActivityKind.Client);

        if (activity != null)
        {
            activity
                .AddTag("activity.type", ActivityType.ExternalModel.ToString())
                .AddTag("llmpool.component", "external_client")
                .AddTag("gen_ai.request.model", modelId)
                .AddTag("gen_ai.system", provider)
                .AddTag("server.address", endpoint);
        }

        return activity;
    }

    /// <summary>
    /// 为嵌套工具调用创建 Activity
    /// 用于追踪 MCP Tool 或其他类型的工具调用
    /// </summary>
    public static Activity? StartNestedToolActivity(
        string toolName,
        string toolType = "mcp",
        string? arguments = null,
        Activity? parent = null)
    {
        var activity = _activitySource.StartActivity(
            $"llmpool.tool {toolName}",
            ActivityKind.Internal,
            parent?.Context ?? default);

        if (activity != null)
        {
            activity
                .AddTag("activity.type", ActivityType.ToolCall.ToString())
                .AddTag("llmpool.component", "tool")
                .AddTag("tool.name", toolName)
                .AddTag("tool.type", toolType);

            if (!string.IsNullOrEmpty(arguments))
            {
                activity.AddTag("tool.arguments", arguments);
            }
        }

        return activity;
    }

    /// <summary>
    /// 记录工具执行结果
    /// </summary>
    public static void RecordToolResult(this Activity activity, string result, bool isError = false)
    {
        if (activity != null)
        {
            activity.AddTag("tool.result", result);
            
            if (isError)
            {
                activity.SetStatus(ActivityStatusCode.Error, result);
                activity.AddTag("error.type", "ToolExecutionError");
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }
        }
    }

    /// <summary>
    /// 记录 Token 使用情况
    /// </summary>
    public static void RecordTokenUsage(this Activity activity, int inputTokens, int outputTokens)
    {
        if (activity != null)
        {
            activity
                .AddTag("gen_ai.usage.input_tokens", inputTokens)
                .AddTag("gen_ai.usage.output_tokens", outputTokens);
        }
    }

    /// <summary>
    /// 记录模型响应信息
    /// </summary>
    public static void RecordResponse(
        this Activity activity,
        string? responseId = null,
        string? modelId = null,
        string? finishReason = null)
    {
        if (activity != null)
        {
            if (!string.IsNullOrEmpty(responseId))
            {
                activity.AddTag("gen_ai.response.id", responseId);
            }

            if (!string.IsNullOrEmpty(modelId))
            {
                activity.AddTag("gen_ai.response.model", modelId);
            }

            if (!string.IsNullOrEmpty(finishReason))
            {
                activity.AddTag("gen_ai.response.finish_reasons", $"[\"{finishReason}\"]");
            }
        }
    }

    /// <summary>
    /// 获取 ActivitySource（供外部使用）
    /// </summary>
    public static ActivitySource GetActivitySource() => _activitySource;
}
