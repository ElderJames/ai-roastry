using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// Activity 跟踪服务，用于监听和记录 App 调用及嵌套工具调用链路
/// </summary>
public class ActivityTraceService : IDisposable
{
    private readonly ILogger<ActivityTraceService> _logger;
    private readonly ConcurrentDictionary<string, TraceNode> _activeTraces = new();
    private readonly ConcurrentQueue<TraceNode> _completedTraces = new();
    private ActivityListener? _activityListener;
    private const int MaxCompletedTraces = 100; // 保留最近 100 条完成的追踪

    // 🎯 维护 TraceId 到 chat Activity 的映射,供工具调用时查找父级
    private readonly ConcurrentDictionary<string, Activity> _traceToChatActivity = new();

    // 🎯 实时事件通知
    public event EventHandler<TraceNode>? ActivityStarted;
    public event EventHandler<TraceNode>? ActivityStopped;
    public event EventHandler<TraceNode>? ActivityUpdated;
    
    /// <summary>
    /// 根据 TraceId 查找对应的 chat Activity
    /// </summary>
    public Activity? GetChatActivityByTraceId(string traceId)
    {
        _traceToChatActivity.TryGetValue(traceId, out var chatActivity);
        Console.WriteLine($"🔍 GetChatActivityByTraceId({traceId}): {chatActivity?.OperationName ?? "NULL"}");
        return chatActivity;
    }

    /// <summary>
    /// 获取最近的 chat Activity（用于并发工具调用时 AsyncLocal 丢失的场景）
    /// 按 StartTime 倒序,返回最新的 chat Activity
    /// </summary>
    public Activity? GetLatestChatActivity()
    {
        if (_traceToChatActivity.IsEmpty)
        {
            Console.WriteLine("🔍 GetLatestChatActivity: 没有活跃的 chat Activity");
            return null;
        }

        // 按 StartTime 倒序排序,取最新的
        var latestActivity = _traceToChatActivity.Values
            .OrderByDescending(a => a.StartTimeUtc)
            .FirstOrDefault();

        Console.WriteLine($"🔍 GetLatestChatActivity: {latestActivity?.OperationName ?? "NULL"} | TraceId: {latestActivity?.TraceId.ToString() ?? "NULL"}");
        return latestActivity;
    }

    public ActivityTraceService(ILogger<ActivityTraceService> logger)
    {
        _logger = logger;
        InitializeActivityListener();
    }

    /// <summary>
    /// 初始化 Activity 监听器
    /// </summary>
    private void InitializeActivityListener()
    {
        _activityListener = new ActivityListener
        {
            // 监听所有 ActivitySource（用于调试）
            ShouldListenTo = source =>
            {
                var shouldListen = source.Name.StartsWith("Microsoft.Extensions.AI") ||
                                  source.Name.StartsWith("Experimental.Microsoft.Extensions.AI") ||  // 🔑 添加实验性版本
                                  source.Name.StartsWith("Experimental.ModelContextProtocol") ||      // 🔑 MCP SDK ActivitySource
                                  source.Name.Contains("LlmPool") ||
                                  source.Name.StartsWith("OpenAI") ||  // 可能是 OpenAI.* 
                                  source.Name.Contains("ChatClient"); // 可能是其他命名
                
                // 🔍 记录所有 ActivitySource 的名称（用于调试）
                _logger.LogInformation("ActivitySource 检测: {SourceName} - 监听: {ShouldListen}", 
                    source.Name, shouldListen);
                
                return shouldListen;
            },

            // 决定是否采样（记录）此 Activity
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                // 🔍 记录采样决策（用于调试）
                _logger.LogDebug("Activity 采样: {OperationName} from {SourceName}", 
                    options.Name, options.Source.Name);
                
                // 记录所有 Activity
                return ActivitySamplingResult.AllDataAndRecorded;
            },

            // Activity 启动时的回调
            ActivityStarted = activity =>
            {
                if (activity != null)
                {
                    OnActivityStarted(activity);
                }
            },

            // Activity 停止时的回调
            ActivityStopped = activity =>
            {
                if (activity != null)
                {
                    OnActivityStopped(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(_activityListener);
        _logger.LogInformation("Activity 监听器已启动");
    }

    /// <summary>
    /// Activity 启动事件处理
    /// </summary>
    private void OnActivityStarted(Activity activity)
    {
        // 🎯 如果是 chat Activity,保存到映射中供工具调用时查找
        // 关键:只保存每个 trace 中的第一个 chat Activity (即模型调用,非递归工具调用)
        if (activity.OperationName.StartsWith("chat "))
        {
            var traceId = activity.TraceId.ToString();
            
            // 只在映射中不存在时才保存 (保留第一个 chat Activity)
            if (_traceToChatActivity.TryAdd(traceId, activity))
            {
                Console.WriteLine($"📌 保存 chat Activity (第一个): TraceId={traceId}, SpanId={activity.SpanId}, Name={activity.OperationName}");
            }
            else
            {
                Console.WriteLine($"⏭️ 跳过 chat Activity (已存在): TraceId={traceId}, SpanId={activity.SpanId}, Name={activity.OperationName}");
            }
        }
        
        var node = new TraceNode
        {
            ActivityId = activity.Id ?? activity.TraceId.ToString(),
            TraceId = activity.TraceId.ToString(),
            SpanId = activity.SpanId.ToString(),
            ParentSpanId = activity.ParentSpanId.ToString(),
            OperationName = activity.OperationName,
            DisplayName = activity.DisplayName,
            StartTime = activity.StartTimeUtc,
            Kind = activity.Kind.ToString(),
            Tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value?.ToString() ?? string.Empty),
            Status = "Started"
        };

        // 提取关键信息
        ExtractKeyInformation(activity, node);
        
        // 🎯 识别节点类型
        IdentifyNodeType(node);

        _activeTraces.TryAdd(node.ActivityId, node);

        // 🎯 触发事件通知
        ActivityStarted?.Invoke(this, node);

        _logger.LogInformation(
            "🚀 Activity Started: {OperationName} | TraceId: {TraceId} | SpanId: {SpanId} | Parent: {ParentSpanId}",
            node.OperationName,
            node.TraceId,
            node.SpanId,
            node.ParentSpanId != "0000000000000000" ? node.ParentSpanId : "ROOT"
        );

        // 🎯 特别标记 llmpool.server Activity
        if (node.OperationName.StartsWith("llmpool.server", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "  🌐 [SERVER] LlmPool 服务端 Activity | IsRoot: {IsRoot} | Kind: {Kind}",
                node.ParentSpanId == "0000000000000000",
                node.Kind);
        }

        // 记录工具调用
        if (node.IsToolCall)
        {
            _logger.LogInformation(
                "  🔧 Tool Call: {ToolName} | Arguments: {Arguments}",
                node.ToolName,
                node.ToolArguments
            );
        }

        // 记录 App 调用
        if (node.IsAppCall)
        {
            _logger.LogInformation(
                "  📱 App Call: {AppName} | Model: {Model}",
                node.AppName,
                node.ModelId
            );
        }
    }

    /// <summary>
    /// Activity 停止事件处理
    /// </summary>
    private void OnActivityStopped(Activity activity)
    {
        var activityId = activity.Id ?? activity.TraceId.ToString();
        
        if (_activeTraces.TryRemove(activityId, out var node))
        {
            node.EndTime = DateTime.UtcNow;
            node.Duration = activity.Duration;
            node.Status = activity.Status == ActivityStatusCode.Ok ? "Success" : 
                         activity.Status == ActivityStatusCode.Error ? "Error" : "Completed";
            node.StatusDescription = activity.StatusDescription;

            // 🎯 重新提取 Tags（因为 OnActivityStarted 时 Tags 可能为空）
            ExtractKeyInformation(activity, node);
            
            // 更新最终信息
            UpdateFinalInformation(activity, node);
            
            // 提取消息内容（从 Events 中）
            ExtractMessagesFromEvents(activity, node);

            // 添加到完成队列
            _completedTraces.Enqueue(node);
            
            // 限制队列大小
            while (_completedTraces.Count > MaxCompletedTraces)
            {
                _completedTraces.TryDequeue(out _);
            }

            // 🎯 触发事件通知
            ActivityStopped?.Invoke(this, node);

            var durationMs = node.Duration.TotalMilliseconds;
            var statusIcon = node.Status == "Success" ? "✅" : 
                           node.Status == "Error" ? "❌" : "⏹️";

            _logger.LogInformation(
                "{StatusIcon} Activity Stopped: {OperationName} | Duration: {DurationMs}ms | Status: {Status}",
                statusIcon,
                node.OperationName,
                durationMs.ToString("F2"),
                node.Status
            );

            // 记录 Token 使用情况
            if (node.InputTokens > 0 || node.OutputTokens > 0)
            {
                _logger.LogInformation(
                    "  📊 Tokens: Input={InputTokens}, Output={OutputTokens}, Total={TotalTokens}",
                    node.InputTokens,
                    node.OutputTokens,
                    node.InputTokens + node.OutputTokens
                );
            }

            // 记录工具执行结果
            if (node.IsToolCall && !string.IsNullOrEmpty(node.ToolResult))
            {
                _logger.LogInformation(
                    "  ✅ Tool Result: {ToolResult}",
                    node.ToolResult?.Length > 200 ? node.ToolResult.Substring(0, 200) + "..." : node.ToolResult
                );
            }

            // 记录错误信息
            if (node.Status == "Error" && !string.IsNullOrEmpty(node.ErrorType))
            {
                _logger.LogError(
                    "  ❌ Error: {ErrorType} - {ErrorMessage}",
                    node.ErrorType,
                    node.StatusDescription
                );
                
                // 记录完整的堆栈跟踪（如果有）
                if (!string.IsNullOrEmpty(node.ErrorStackTrace))
                {
                    _logger.LogError(
                        "  📋 Stack Trace:\n{StackTrace}",
                        node.ErrorStackTrace
                    );
                }
            }
        }
    }

    /// <summary>
    /// 提取关键信息
    /// </summary>
    private void ExtractKeyInformation(Activity activity, TraceNode node)
    {
        // 从 Tags 中提取信息
        foreach (var tag in activity.Tags)
        {
            switch (tag.Key)
            {
                case "gen_ai.operation.name":
                    node.OperationType = tag.Value;
                    node.IsAppCall = tag.Value == "chat";
                    break;

                case "gen_ai.request.model":
                    node.ModelId = tag.Value;
                    break;

                case "gen_ai.provider.name":
                    node.ProviderName = tag.Value;
                    break;

                case "gen_ai.conversation.id":
                    node.ConversationId = tag.Value;
                    break;

                case "gen_ai.request.temperature":
                    if (float.TryParse(tag.Value, out var temp))
                        node.Temperature = temp;
                    break;

                case "gen_ai.request.max_tokens":
                    if (int.TryParse(tag.Value, out var maxTokens))
                        node.MaxTokens = maxTokens;
                    break;

                case "server.address":
                    node.ServerAddress = tag.Value;
                    break;

                case "server.port":
                    if (int.TryParse(tag.Value, out var port))
                        node.ServerPort = port;
                    break;

                // 工具调用相关 (llmpool 自定义 tags)
                case "tool.name":
                    node.ToolName = tag.Value;
                    node.IsToolCall = true;
                    _logger.LogInformation("  ✅ Extracted ToolName from Tag: {ToolName}", tag.Value);
                    break;

                case "tool.arguments":
                    node.ToolArguments = tag.Value;
                    _logger.LogInformation("  ✅ Extracted ToolArguments from Tag: {Args}", tag.Value?.Substring(0, Math.Min(100, tag.Value?.Length ?? 0)));
                    break;
                
                case "tool.result":
                    node.ToolResult = tag.Value;
                    _logger.LogInformation("  ✅ Extracted ToolResult from Tag: {Result}", tag.Value?.Substring(0, Math.Min(100, tag.Value?.Length ?? 0)));
                    break;

                case "app.name":
                    node.AppName = tag.Value;
                    node.IsAppCall = true;
                    _logger.LogInformation("  ✅ Extracted AppName from Tag: {AppName}", tag.Value);
                    break;
                    
                // Microsoft.Extensions.AI 的工具调用信息也可能在 tags 中
                case "gen_ai.tool.call.count":
                    if (int.TryParse(tag.Value, out var toolCount) && toolCount > 0)
                    {
                        node.IsToolCall = true;
                    }
                    break;
                    
                case "gen_ai.tool.names":
                    // 多个工具名称,逗号分隔
                    if (!string.IsNullOrEmpty(tag.Value))
                    {
                        node.ToolName = tag.Value;
                        node.IsToolCall = true;
                    }
                    break;
            }
        }
        
        // 🎯 新增: 从 Activity Events 中提取工具调用信息
        // Microsoft.Extensions.AI 使用 Events 记录 tool_call 和 tool_result
        _logger.LogInformation(
            "🔍 [ExtractEvents] Activity: {OperationName} | Events: {EventCount}",
            activity.OperationName,
            activity.Events.Count()
        );
        
        foreach (var activityEvent in activity.Events)
        {
            _logger.LogInformation(
                "  📌 Event: {EventName} | Tags: {Tags}",
                activityEvent.Name,
                string.Join(", ", activityEvent.Tags.Select(t => $"{t.Key}={t.Value}"))
            );
            
            // 保存所有 Events 到 node.Events (用于 UI 展示)
            var eventInfo = new ActivityEventInfo
            {
                Name = activityEvent.Name,
                Timestamp = activityEvent.Timestamp,
                Tags = activityEvent.Tags
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? string.Empty)
            };
            node.Events.Add(eventInfo);
            
            if (activityEvent.Name.StartsWith("tool_call:", StringComparison.OrdinalIgnoreCase))
            {
                node.IsToolCall = true;
                
                // 提取工具名称和参数
                foreach (var tag in activityEvent.Tags)
                {
                    switch (tag.Key)
                    {
                        case "gen_ai.tool.call.name":
                            if (string.IsNullOrEmpty(node.ToolName))
                            {
                                node.ToolName = tag.Value?.ToString();
                            }
                            else
                            {
                                node.ToolName += ", " + tag.Value;
                            }
                            _logger.LogInformation("    ✅ Extracted ToolName from Event: {ToolName}", tag.Value);
                            break;
                            
                        case "gen_ai.tool.call.arguments":
                            if (string.IsNullOrEmpty(node.ToolArguments))
                            {
                                node.ToolArguments = tag.Value?.ToString();
                            }
                            else
                            {
                                node.ToolArguments += "\n---\n" + tag.Value;
                            }
                            _logger.LogInformation("    ✅ Extracted ToolArguments from Event: {Args}", tag.Value?.ToString()?.Substring(0, Math.Min(100, tag.Value.ToString()?.Length ?? 0)));
                            break;
                    }
                }
            }
            else if (activityEvent.Name.StartsWith("tool_result:", StringComparison.OrdinalIgnoreCase))
            {
                // 提取工具结果
                foreach (var tag in activityEvent.Tags)
                {
                    if (tag.Key == "gen_ai.tool.call.result")
                    {
                        if (string.IsNullOrEmpty(node.ToolResult))
                        {
                            node.ToolResult = tag.Value?.ToString();
                        }
                        else
                        {
                            node.ToolResult += "\n---\n" + tag.Value;
                        }
                        _logger.LogInformation("    ✅ Extracted ToolResult from Event: {Result}", tag.Value?.ToString()?.Substring(0, Math.Min(100, tag.Value.ToString()?.Length ?? 0)));
                    }
                }
            }
        }

        // 🔍 调试：记录所有 Tags
        _logger.LogInformation(
            "🔍 [ExtractTags] Activity: {OperationName} | Tags: {Tags}",
            node.OperationName,
            string.Join(", ", activity.Tags.Select(t => $"{t.Key}={t.Value}"))
        );

        // 从 DisplayName 推断类型并尝试提取 AppName/ToolName
        // 只有在没有从 Tags 中提取到明确类型时才进行推断
        if (!node.IsAppCall && !node.IsToolCall)
        {
            if (node.OperationName.Contains("chat", StringComparison.OrdinalIgnoreCase))
            {
                node.IsAppCall = true;
                
                // 🎯 从 OperationName 提取模型名称作为 AppName
                // OperationName 格式: "chat {model}" 或 "chat"
                if (string.IsNullOrEmpty(node.AppName))
                {
                    // 尝试从 "chat gpt-4" 提取 "gpt-4"
                    var parts = node.OperationName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1 && !parts[1].StartsWith("/")) // 排除 "/v1/chat/completions" 这种情况
                    {
                        node.AppName = string.Join(" ", parts.Skip(1));
                        _logger.LogInformation("  ✅ Extracted AppName from OperationName: {AppName}", node.AppName);
                    }
                    // 如果没有模型名，尝试从 gen_ai.request.model tag 获取
                    else if (node.Tags.TryGetValue("gen_ai.request.model", out var modelFromTag))
                    {
                        node.AppName = modelFromTag;
                        _logger.LogInformation("  ✅ Extracted AppName from gen_ai.request.model tag: {AppName}", node.AppName);
                    }
                }
            }
            else if (node.OperationName.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                     node.OperationName.Contains("function", StringComparison.OrdinalIgnoreCase))
            {
                node.IsToolCall = true;
                
                // 🎯 从 OperationName 提取工具名称作为 ToolName
                // OperationName 格式: "llmpool.tool {toolName}" 或 "tool {toolName}"
                if (string.IsNullOrEmpty(node.ToolName))
                {
                    // 尝试从 "llmpool.tool calc" 或 "tool calc" 提取 "calc"
                    var parts = node.OperationName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        // 最后一部分就是工具名
                        node.ToolName = parts[^1]; // 等同于 parts[parts.Length - 1]
                        _logger.LogInformation("  ✅ Extracted ToolName from OperationName: {ToolName}", node.ToolName);
                    }
                    else
                    {
                        // 尝试从 "llmpool.tool.calc" 这种格式提取（用点分隔）
                        var dotParts = node.OperationName.Split('.', StringSplitOptions.RemoveEmptyEntries);
                        if (dotParts.Length > 2 && dotParts[^2].Equals("tool", StringComparison.OrdinalIgnoreCase))
                        {
                            node.ToolName = dotParts[^1];
                            _logger.LogInformation("  ✅ Extracted ToolName from dot-separated OperationName: {ToolName}", node.ToolName);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 更新最终信息
    /// </summary>
    private void UpdateFinalInformation(Activity activity, TraceNode node)
    {
        // 🔍 调试：记录 Activity 停止时的所有 Tags
        _logger.LogInformation(
            "🔍 [UpdateFinal] Activity: {OperationName} | Tags: {Tags}",
            activity.OperationName,
            string.Join(", ", activity.Tags.Select(t => $"{t.Key}={t.Value}"))
        );
        
        foreach (var tag in activity.Tags)
        {
            switch (tag.Key)
            {
                case "gen_ai.usage.input_tokens":
                    if (int.TryParse(tag.Value, out var inputTokens))
                        node.InputTokens = inputTokens;
                    break;

                case "gen_ai.usage.output_tokens":
                    if (int.TryParse(tag.Value, out var outputTokens))
                        node.OutputTokens = outputTokens;
                    break;

                case "gen_ai.response.id":
                    node.ResponseId = tag.Value;
                    break;

                case "gen_ai.response.model":
                    node.ResponseModelId = tag.Value;
                    break;

                case "gen_ai.response.finish_reasons":
                    node.FinishReason = tag.Value;
                    break;

                case "error.type":
                    node.ErrorType = tag.Value;
                    break;
                
                case "error.stack_trace":
                    node.ErrorStackTrace = tag.Value;
                    break;

                case "tool.result":
                    node.ToolResult = tag.Value;
                    break;
                    
                // MCP Server 相关
                case "mcp.session.id":
                    node.IsMcpServer = true;
                    break;
                    
                case "mcp.server.name":
                    node.McpServerName = tag.Value;
                    node.IsMcpServer = true;
                    break;
            }
        }
    }
    
    /// <summary>
    /// 识别节点类型
    /// </summary>
    private void IdentifyNodeType(TraceNode node)
    {
        // 1. 识别 MCP Tool (tools/call)
        if (node.OperationName.Contains("tools/call", StringComparison.OrdinalIgnoreCase))
        {
            node.IsMcpTool = true;
            node.IsToolCall = true;
            
            // 尝试从 Tags 中获取 MCP Server 名称
            if (node.Tags.TryGetValue("mcp.server.name", out var serverName))
            {
                node.McpServerName = serverName;
            }
        }
        // 2. 识别 App Tool (llmpool.tool 或 llmpool.app)
        else if (node.OperationName.StartsWith("llmpool.tool", StringComparison.OrdinalIgnoreCase) ||
                 node.OperationName.StartsWith("llmpool.app", StringComparison.OrdinalIgnoreCase))
        {
            node.IsAppTool = true;
            node.IsToolCall = true;
        }
        // 3. 识别 MCP Server Activity (initialize, tools/list等)
        else if (node.OperationName.Contains("initialize", StringComparison.OrdinalIgnoreCase) ||
                 node.OperationName.Contains("tools/list", StringComparison.OrdinalIgnoreCase) ||
                 node.OperationName.Contains("notifications/", StringComparison.OrdinalIgnoreCase) ||
                 node.Tags.ContainsKey("mcp.session.id"))
        {
            node.IsMcpServer = true;
            
            // 尝试从 Tags 中获取 MCP Server 名称
            if (node.Tags.TryGetValue("mcp.server.name", out var serverName))
            {
                node.McpServerName = serverName;
            }
        }
        // 4. 识别 LlmPool Server (llmpool.server)
        else if (node.OperationName.StartsWith("llmpool.server", StringComparison.OrdinalIgnoreCase))
        {
            node.IsLlmPoolServer = true;
        }
        // 5. 识别 HTTP Request
        else if (node.OperationName.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                 node.Kind == "Client")
        {
            // HTTP 请求类型 - 可以进一步细分
            if (node.ServerAddress != null && node.ServerAddress.Contains("localhost"))
            {
                node.IsLlmPoolServer = true;
            }
        }
    }

    /// <summary>
    /// 从 Activity Events 中提取消息内容
    /// </summary>
    private void ExtractMessagesFromEvents(Activity activity, TraceNode node)
    {
        try
        {
            // 🔍 调试：记录 Activity 中的所有 Events 和 Tags
            _logger.LogInformation(
                "🔍 [ExtractMessages] Activity: {ActivityName} | Events: {EventCount} | Tags: {TagCount}",
                activity.OperationName,
                activity.Events.Count(),
                activity.Tags.Count()
            );
            
            foreach (var evt in activity.Events)
            {
                _logger.LogInformation(
                    "  📌 Event: {EventName} | Tags: {Tags}",
                    evt.Name,
                    string.Join(", ", evt.Tags.Select(t => $"{t.Key}={t.Value}"))
                );
                
                // 提取输入消息（gen_ai.choice 事件通常包含消息）
                if (evt.Name == "gen_ai.choice" || evt.Name.Contains("message", StringComparison.OrdinalIgnoreCase))
                {
                    var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value?.ToString() ?? "");
                    
                    // 尝试提取角色和内容
                    string? role = null;
                    string? content = null;
                    
                    if (tags.TryGetValue("gen_ai.message.role", out var roleValue))
                        role = roleValue;
                    if (tags.TryGetValue("gen_ai.message.content", out var contentValue))
                        content = contentValue;
                    
                    // 如果是 system.user 或 assistant 消息
                    if (tags.TryGetValue("role", out var r))
                        role = r;
                    if (tags.TryGetValue("content", out var c))
                        content = c;
                    
                    if (!string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(content))
                    {
                        var message = new TraceChatMessage
                        {
                            Role = role,
                            Content = content
                        };
                        
                        _logger.LogInformation("  ✅ Extracted message: role={Role}, content={Content}", role, content.Substring(0, Math.Min(50, content.Length)));
                        
                        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                        {
                            // 助手的输出
                            node.OutputContent = content;
                        }
                        else
                        {
                            // 用户或系统消息作为输入
                            node.InputMessages.Add(message);
                        }
                    }
                }
            }
            
            // 如果从 Events 中没有提取到，尝试从 Tags 中提取
            if (node.InputMessages.Count == 0 && string.IsNullOrEmpty(node.OutputContent))
            {
                _logger.LogInformation("  ⚠️ No messages from Events, trying to extract from Tags");
                
                // 🎯 首先尝试从 gen_ai.prompt 和 gen_ai.completion Tags 提取 (我们自己添加的)
                if (node.Tags.TryGetValue("gen_ai.prompt", out var promptJson))
                {
                    _logger.LogInformation("  📄 Found gen_ai.prompt tag: {Value}", promptJson.Substring(0, Math.Min(100, promptJson.Length)));
                    
                    try
                    {
                        var messages = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(promptJson);
                        if (messages != null)
                        {
                            foreach (var msg in messages)
                            {
                                if (msg.TryGetValue("role", out var role) && msg.TryGetValue("content", out var content))
                                {
                                    node.InputMessages.Add(new TraceChatMessage { Role = role, Content = content });
                                    _logger.LogInformation("  ✅ Extracted from gen_ai.prompt: role={Role}, content={Content}", role, content.Substring(0, Math.Min(50, content.Length)));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("  ❌ Failed to parse gen_ai.prompt JSON: {Error}", ex.Message);
                    }
                }
                
                if (node.Tags.TryGetValue("gen_ai.completion", out var completionText))
                {
                    node.OutputContent = completionText;
                    _logger.LogInformation("  ✅ Extracted from gen_ai.completion: {Content}", completionText.Substring(0, Math.Min(50, completionText.Length)));
                }
                
                // 兼容：也尝试旧的 input.messages 格式
                if (node.InputMessages.Count == 0 && node.Tags.TryGetValue("input.messages", out var inputMsg))
                {
                    _logger.LogInformation("  📄 Found input.messages tag: {Value}", inputMsg.Substring(0, Math.Min(100, inputMsg.Length)));
                    
                    // 尝试解析 JSON
                    try
                    {
                        var messages = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(inputMsg);
                        if (messages != null)
                        {
                            foreach (var msg in messages)
                            {
                                if (msg.TryGetValue("role", out var role) && msg.TryGetValue("content", out var content))
                                {
                                    node.InputMessages.Add(new TraceChatMessage { Role = role, Content = content });
                                    _logger.LogInformation("  ✅ Extracted from Tag: role={Role}, content={Content}", role, content.Substring(0, Math.Min(50, content.Length)));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("  ❌ Failed to parse input.messages JSON: {Error}", ex.Message);
                    }
                }
                
                // 兼容：也尝试旧的 output.content 格式
                if (string.IsNullOrEmpty(node.OutputContent) && node.Tags.TryGetValue("output.content", out var outputContent))
                {
                    node.OutputContent = outputContent;
                    _logger.LogInformation("  ✅ Extracted output.content from Tag: {Content}", outputContent.Substring(0, Math.Min(50, outputContent.Length)));
                }
            }
            
            _logger.LogInformation(
                "  📊 Final: InputMessages={InputCount}, OutputContent={HasOutput}",
                node.InputMessages.Count,
                !string.IsNullOrEmpty(node.OutputContent)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract messages from Activity events");
        }
    }

    /// <summary>
    /// 获取活跃的追踪
    /// </summary>
    public IEnumerable<TraceNode> GetActiveTraces()
    {
        return _activeTraces.Values.OrderByDescending(t => t.StartTime);
    }

    /// <summary>
    /// 获取已完成的追踪
    /// </summary>
    public IEnumerable<TraceNode> GetCompletedTraces(int count = 50)
    {
        return _completedTraces.Reverse().Take(count);
    }

    /// <summary>
    /// 获取追踪树（按层级组织）
    /// </summary>
    public List<TraceNode> GetTraceTree(string traceId)
    {
        var allNodes = _completedTraces
            .Where(t => t.TraceId == traceId)
            .ToList();

        var activeNodes = _activeTraces.Values
            .Where(t => t.TraceId == traceId)
            .ToList();

        allNodes.AddRange(activeNodes);

        // 构建树形结构
        var nodeDict = allNodes.ToDictionary(n => n.SpanId);
        var rootNodes = new List<TraceNode>();

        foreach (var node in allNodes)
        {
            if (node.ParentSpanId == "0000000000000000" || !nodeDict.ContainsKey(node.ParentSpanId))
            {
                rootNodes.Add(node);
            }
            else
            {
                var parent = nodeDict[node.ParentSpanId];
                parent.Children.Add(node);
            }
        }

        return rootNodes;
    }

    /// <summary>
    /// 获取追踪统计信息
    /// </summary>
    public TraceStatistics GetStatistics(DateTime? since = null)
    {
        var traces = since.HasValue
            ? _completedTraces.Where(t => t.StartTime >= since.Value)
            : _completedTraces;

        var stats = new TraceStatistics
        {
            TotalTraces = traces.Count(),
            SuccessCount = traces.Count(t => t.Status == "Success"),
            ErrorCount = traces.Count(t => t.Status == "Error"),
            TotalDurationMs = traces.Sum(t => t.Duration.TotalMilliseconds),
            AverageDurationMs = traces.Any() ? traces.Average(t => t.Duration.TotalMilliseconds) : 0,
            TotalInputTokens = traces.Sum(t => t.InputTokens),
            TotalOutputTokens = traces.Sum(t => t.OutputTokens),
            AppCallCount = traces.Count(t => t.IsAppCall),
            ToolCallCount = traces.Count(t => t.IsToolCall),
            UniqueModels = traces.Where(t => !string.IsNullOrEmpty(t.ModelId))
                                .Select(t => t.ModelId!)
                                .Distinct()
                                .ToList(),
            UniqueTools = traces.Where(t => !string.IsNullOrEmpty(t.ToolName))
                               .Select(t => t.ToolName!)
                               .Distinct()
                               .ToList()
        };

        return stats;
    }

    /// <summary>
    /// 获取所有 Conversation 列表（包括活跃和已完成的）
    /// </summary>
    public List<ConversationInfo> GetConversationList()
    {
        var allTraces = _completedTraces.Concat(_activeTraces.Values).ToList();
        
        // 为没有 ConversationId 的 traces 分配默认 ID（使用 TraceId）
        var tracesWithConvId = allTraces.Select(t => new
        {
            Trace = t,
            ConvId = string.IsNullOrEmpty(t.ConversationId) 
                ? $"trace-{t.TraceId.Substring(0, 8)}" 
                : t.ConversationId
        }).ToList();
        
        var conversations = tracesWithConvId
            .GroupBy(t => t.ConvId)
            .Select(g => new ConversationInfo
            {
                ConversationId = g.Key,
                RequestCount = g.Count(),
                FirstRequestTime = g.Min(t => t.Trace.StartTime),
                LastRequestTime = g.Max(t => t.Trace.StartTime),
                TotalInputTokens = g.Sum(t => t.Trace.InputTokens),
                TotalOutputTokens = g.Sum(t => t.Trace.OutputTokens),
                IsActive = g.Any(t => !t.Trace.EndTime.HasValue),
                SuccessCount = g.Count(t => t.Trace.Status == "Success"),
                ErrorCount = g.Count(t => t.Trace.Status == "Error"),
                Models = g.Where(t => !string.IsNullOrEmpty(t.Trace.ModelId))
                         .Select(t => t.Trace.ModelId!)
                         .Distinct()
                         .ToList()
            })
            .OrderByDescending(c => c.LastRequestTime)
            .ToList();

        return conversations;
    }

    /// <summary>
    /// 根据 ConversationId 获取该 Conversation 的所有追踪（树形结构）
    /// </summary>
    public List<TraceNode> GetTracesByConversation(string conversationId)
    {
        var allTraces = _completedTraces
            .Concat(_activeTraces.Values)
            .ToList();

        // 如果 conversationId 是临时生成的（以 trace- 开头），则按 TraceId 匹配
        List<TraceNode> filteredTraces;
        if (conversationId.StartsWith("trace-"))
        {
            var traceIdPrefix = conversationId.Substring(6); // 去掉 "trace-" 前缀
            filteredTraces = allTraces
                .Where(t => t.TraceId.StartsWith(traceIdPrefix))
                .ToList();
        }
        else
        {
            filteredTraces = allTraces
                .Where(t => t.ConversationId == conversationId)
                .ToList();
        }

        // 按 TraceId 分组，构建多个调用树
        var traceGroups = filteredTraces.GroupBy(t => t.TraceId);
        var allRootNodes = new List<TraceNode>();

        foreach (var group in traceGroups)
        {
            var nodeDict = group.ToDictionary(n => n.SpanId);
            
            foreach (var node in group)
            {
                if (node.ParentSpanId == "0000000000000000" || !nodeDict.ContainsKey(node.ParentSpanId))
                {
                    allRootNodes.Add(node);
                }
                else
                {
                    var parent = nodeDict[node.ParentSpanId];
                    if (!parent.Children.Contains(node))
                    {
                        parent.Children.Add(node);
                    }
                }
            }
        }

        return allRootNodes.OrderByDescending(n => n.StartTime).ToList();
    }

    /// <summary>
    /// 清除历史追踪
    /// </summary>
    public void ClearHistory()
    {
        while (_completedTraces.TryDequeue(out _)) { }
        _logger.LogInformation("已清除历史追踪记录");
    }

    /// <summary>
    /// 添加来自外部进程的 Activity (通过 OTLP 接收)
    /// 用于跨进程追踪 (如 MCP Server 的 Activity)
    /// </summary>
    public void AddExternalActivity(ExternalActivityDto activityDto)
    {
        try
        {
            var node = new TraceNode
            {
                ActivityId = $"{activityDto.TraceId}:{activityDto.SpanId}",
                TraceId = activityDto.TraceId,
                SpanId = activityDto.SpanId,
                ParentSpanId = activityDto.ParentSpanId ?? "0000000000000000",
                OperationName = activityDto.OperationName,
                DisplayName = activityDto.OperationName,
                StartTime = activityDto.StartTimeUtc,
                EndTime = activityDto.StartTimeUtc + activityDto.Duration,
                Duration = activityDto.Duration,
                Kind = "External", // 标记为外部 Activity
                Tags = activityDto.Tags?.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty) 
                    ?? new Dictionary<string, string>(),
                Status = activityDto.Status switch
                {
                    ActivityStatusCode.Ok => "Success",
                    ActivityStatusCode.Error => "Error",
                    _ => "Completed"
                },
                StatusDescription = activityDto.StatusDescription
            };

            // 从 Tags 中提取信息
            if (activityDto.Tags != null)
            {
                if (activityDto.Tags.TryGetValue("service.name", out var serviceName))
                {
                    node.Tags["ServiceName"] = serviceName?.ToString() ?? string.Empty;
                }
                
                if (activityDto.Tags.TryGetValue("scope.name", out var scopeName))
                {
                    node.Tags["ScopeName"] = scopeName?.ToString() ?? string.Empty;
                }
                
                // 标记为 MCP Server Activity
                if (activityDto.Source?.Contains("ModelContextProtocol") == true)
                {
                    node.Tags["IsMcpServer"] = "true";
                    node.Tags["Source"] = activityDto.Source;
                }
            }

            // 直接添加到完成队列 (外部 Activity 已经完成)
            _completedTraces.Enqueue(node);

            // 限制队列大小
            while (_completedTraces.Count > MaxCompletedTraces)
            {
                _completedTraces.TryDequeue(out _);
            }

            _logger.LogInformation(
                "📥 External Activity Added: {OperationName} | TraceId: {TraceId} | SpanId: {SpanId} | Source: {Source}",
                node.OperationName,
                node.TraceId,
                node.SpanId,
                activityDto.Source ?? "external"
            );

            // 🎯 触发事件通知
            ActivityStopped?.Invoke(this, node);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ 添加外部 Activity 失败: {OperationName}", activityDto.OperationName);
        }
    }

    public void Dispose()
    {
        _activityListener?.Dispose();
        _activeTraces.Clear();
        while (_completedTraces.TryDequeue(out _)) { }
        _logger.LogInformation("Activity 监听器已停止");
    }
}

/// <summary>
/// 追踪节点
/// </summary>
public class TraceNode
{
    public string ActivityId { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public string SpanId { get; set; } = string.Empty;
    public string ParentSpanId { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? StatusDescription { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
    
    // AI 相关属性
    public string? OperationType { get; set; }
    public string? ModelId { get; set; }
    public string? ResponseModelId { get; set; }
    public string? ProviderName { get; set; }
    public string? ConversationId { get; set; }
    public string? ResponseId { get; set; }
    public string? FinishReason { get; set; }
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string? ServerAddress { get; set; }
    public int? ServerPort { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorStackTrace { get; set; }
    
    // App 相关属性
    public bool IsAppCall { get; set; }
    public string? AppName { get; set; }
    
    // Tool 相关属性
    public bool IsToolCall { get; set; }
    public string? ToolName { get; set; }
    public string? ToolArguments { get; set; }
    public string? ToolResult { get; set; }
    
    // 工具类型区分
    public bool IsAppTool { get; set; }  // llmpool.tool (App Tool)
    public bool IsMcpTool { get; set; }   // tools/call (MCP Tool)
    
    // Server 类型区分
    public bool IsLlmPoolServer { get; set; }  // llmpool.server
    public bool IsMcpServer { get; set; }      // MCP Server (initialize, tools/list等)
    public string? McpServerName { get; set; } // MCP Server 的配置名称
    
    // Chat 消息内容（从 Events 中提取）
    public List<TraceChatMessage> InputMessages { get; set; } = new();
    public string? OutputContent { get; set; }
    
    // Activity Events (用于展示工具调用、结果等事件)
    public List<ActivityEventInfo> Events { get; set; } = new();
    
    // 树形结构
    public List<TraceNode> Children { get; set; } = new();
}

/// <summary>
/// Activity Event 信息 (用于 UI 展示)
/// </summary>
public class ActivityEventInfo
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Chat 消息 (从 Activity Events 中提取)
/// </summary>
public class TraceChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
}

/// <summary>
/// Conversation 信息
/// </summary>
public class ConversationInfo
{
    public string ConversationId { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public DateTime FirstRequestTime { get; set; }
    public DateTime LastRequestTime { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public bool IsActive { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Models { get; set; } = new();
}

/// <summary>
/// 追踪统计信息
/// </summary>
public class TraceStatistics
{
    public int TotalTraces { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public double TotalDurationMs { get; set; }
    public double AverageDurationMs { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int AppCallCount { get; set; }
    public int ToolCallCount { get; set; }
    public List<string> UniqueModels { get; set; } = new();
    public List<string> UniqueTools { get; set; } = new();
}
