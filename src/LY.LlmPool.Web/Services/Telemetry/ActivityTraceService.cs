using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.AI;
using LY.LlmPool.Web.Repositories;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// Activity 跟踪服务，用于监听和记录 App 调用及嵌套工具调用链路
/// 🎯 使用 HybridCache 持久化追踪数据
/// </summary>
public class ActivityTraceService : IDisposable
{
    private readonly ActivityTracePersistenceService _persistenceService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActivityTraceService> _logger;
    private readonly HybridCache _cache;
    private readonly ConcurrentDictionary<string, TraceNode> _activeTraces = new();
    private readonly ConcurrentQueue<TraceNode> _completedTraces = new();
    private readonly ConcurrentDictionary<string, Activity> _activeActivities = new();  // 🎯 缓存真实的 Activity 对象
    private ActivityListener? _activityListener;

    // 📉 限制在内存中保留的完成追踪数量，避免堆积大请求体/响应体导致的内存暴涨
    private const int MaxCompletedTraces = 2000;

    // 📉 限制存储到 TraceNode.Tags 的单个值长度，防止把完整请求/响应放进内存
    private const int MaxTagValueLength = 2048;
    private const int LargePayloadTagLength = 1024;
    private static readonly HashSet<string> LargePayloadTagKeys = new(
        new[]
        {
            "request.body",
            "response.content",
            "app.input.messages",
            "app.output.interaction_sequence",
            "app.input.parameters",
            "gen_ai.prompt",
            "gen_ai.completion",
            "response.error"
        },
        StringComparer.OrdinalIgnoreCase);
    
    // 🎯 HybridCache 键名常量
    private const string CacheKeyCompletedTraces = "ActivityTrace:CompletedTraces";
    private const string CacheKeyConversations = "ActivityTrace:Conversations";
    private const int CacheExpirationDays = 7; // 缓存保留7天

    // 🎯 实时事件通知
    public event EventHandler<TraceNode>? ActivityStarted;
    public event EventHandler<TraceNode>? ActivityStopped;
    public event EventHandler<TraceNode>? ActivityUpdated;
    
    /// <summary>
    /// 根据 TraceId 查找对应的 chat Activity
    /// 🎯 直接从 _activeTraces 和 _completedTraces 中查询,不使用单独的缓存
    /// </summary>
    public Activity? GetChatActivityByTraceId(string traceId)
    {
        try
        {
            // 🎯 从活跃追踪中查找第一个 chat Activity
            var chatNode = _activeTraces.Values
                .FirstOrDefault(n => n.TraceId == traceId && n.OperationName.StartsWith("chat "));
            
            if (chatNode != null)
            {
                Console.WriteLine($"🔍 GetChatActivityByTraceId({traceId}): 找到活跃的 chat Activity: {chatNode.OperationName}");
                // 注意: 无法返回真实的 Activity 对象,返回 null
                return null;
            }
            
            // 🎯 从已完成的追踪中查找第一个 chat Activity
            chatNode = _completedTraces
                .FirstOrDefault(n => n.TraceId == traceId && n.OperationName.StartsWith("chat "));
            
            if (chatNode != null)
            {
                Console.WriteLine($"🔍 GetChatActivityByTraceId({traceId}): 找到已完成的 chat Activity: {chatNode.OperationName}");
                // 注意: 无法返回真实的 Activity 对象,返回 null
                return null;
            }

            Console.WriteLine($"🔍 GetChatActivityByTraceId({traceId}): 未找到 chat Activity");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查找 chat Activity 失败: {TraceId}", traceId);
            return null;
        }
    }

    /// <summary>
    /// 获取最近的 chat TraceNode（用于并发工具调用时 AsyncLocal 丢失的场景）
    /// 🎯 返回 TraceNode 而不是 Activity,因为无法重建 Activity 对象
    /// </summary>
    public TraceNode? GetLatestChatTraceNode()
    {
        try
        {
            // 🎯 从 _activeTraces 中查找最新的 chat Activity
            var latestChatNode = _activeTraces.Values
                .Where(n => n.OperationName.StartsWith("chat "))
                .OrderByDescending(n => n.StartTime)
                .FirstOrDefault();

            if (latestChatNode == null)
            {
                Console.WriteLine("🔍 GetLatestChatTraceNode: 没有活跃的 chat Activity");
                return null;
            }

            Console.WriteLine($"🔍 GetLatestChatTraceNode: {latestChatNode.OperationName} | TraceId: {latestChatNode.TraceId} | SpanId: {latestChatNode.SpanId}");
            
            return latestChatNode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取最新 chat TraceNode 失败");
            return null;
        }
    }

    /// <summary>
    /// 获取最近的 chat Activity（用于并发工具调用时 AsyncLocal 丢失的场景）
    /// 🎯 从缓存的 Activity 对象中返回
    /// </summary>
    public Activity? GetLatestChatActivity()
    {
        try
        {
            // 🎯 从 _activeActivities 中查找最新的 chat Activity
            var latestChatActivity = _activeActivities.Values
                .Where(a => a.OperationName.StartsWith("chat "))
                .OrderByDescending(a => a.StartTimeUtc)
                .FirstOrDefault();

            if (latestChatActivity == null)
            {
                Console.WriteLine("🔍 GetLatestChatActivity: 没有活跃的 chat Activity");
                return null;
            }

            Console.WriteLine($"🔍 GetLatestChatActivity: {latestChatActivity.OperationName} | TraceId: {latestChatActivity.TraceId} | SpanId: {latestChatActivity.SpanId}");
            
            return latestChatActivity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取最新 chat Activity 失败");
            return null;
        }
    }

    public ActivityTraceService(
    ILogger<ActivityTraceService> logger,
    HybridCache cache,
    ActivityTracePersistenceService persistenceService,
    IServiceProvider serviceProvider)
    {
        _logger = logger;
        _persistenceService = persistenceService;
        _serviceProvider = serviceProvider;
        _cache = cache;
        
        // 🎯 从缓存中恢复已完成的追踪数据
        _ = LoadCompletedTracesFromCacheAsync();
        
        InitializeActivityListener();
    }
    
    /// <summary>
    /// 从 HybridCache 加载已完成的追踪数据
    /// </summary>
    private async Task LoadCompletedTracesFromCacheAsync()
    {
        try
        {
            var cachedTraces = await _cache.GetOrCreateAsync<List<TraceNode>>(
                CacheKeyCompletedTraces,
                async _ => await ValueTask.FromResult(new List<TraceNode>()),
                new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromDays(CacheExpirationDays),
                    LocalCacheExpiration = TimeSpan.FromHours(1)
                });

            if (cachedTraces != null && cachedTraces.Any())
            {
                foreach (var trace in cachedTraces.Take(MaxCompletedTraces))
                {
                    _completedTraces.Enqueue(trace);
                }
                _logger.LogInformation("📥 从缓存中恢复了 {Count} 条已完成的追踪记录", cachedTraces.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从缓存加载追踪数据失败");
        }
    }
    
    /// <summary>
    /// 保存已完成的追踪数据到 HybridCache
    /// </summary>
    private async Task SaveCompletedTracesToCacheAsync()
    {
        try
        {
            var tracesList = _completedTraces.ToList();
            
            await _cache.SetAsync(
                CacheKeyCompletedTraces,
                tracesList,
                new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromDays(CacheExpirationDays),
                    LocalCacheExpiration = TimeSpan.FromDays(2)
                });
            
            _logger.LogDebug("💾 已保存 {Count} 条追踪记录到缓存", tracesList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存追踪数据到缓存失败");
        }
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
                                  source.Name.Contains("ChatClient") || // 可能是其他命名
                                  source.Name == "System.Net.Http" || // 🔑 HttpClient Activity
                                  source.Name == "OpenTelemetry.Instrumentation.Http.HttpClient" || // 🔑 HttpClient Instrumentation
                                  source.Name == "Microsoft.AspNetCore" || // 🔑 ASP.NET Core Activity (HttpRequestIn)
                                  source.Name == "OpenTelemetry.Instrumentation.AspNetCore"; // 🔑 ASP.NET Core Instrumentation
                
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
        // 🎯 缓存真实的 Activity 对象
        _activeActivities.TryAdd(activity.SpanId.ToString(), activity);
        
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
        
        // 🎯 诊断日志：检查 ConversationId 是否从 Activity Tags 中提取到
        _logger.LogInformation(
            "🔍 [ConversationId 诊断] Activity: {OperationName} | SpanId: {SpanId} | Parent: {ParentSpanId}",
            node.OperationName,
            node.SpanId,
            node.ParentSpanId);
        
        _logger.LogInformation(
            "  📋 Activity Tags 中的 gen_ai.conversation.id: {ConvId}",
            activity.Tags.FirstOrDefault(t => t.Key == "gen_ai.conversation.id").Value ?? "NULL");
        
        _logger.LogInformation(
            "  📋 提取后 node.ConversationId: {ConvId}",
            node.ConversationId ?? "NULL");
        
        // 🎯 识别节点类型
        IdentifyNodeType(node);
        
        // 🎯 关键修复：从父 Activity 继承 ConversationId（如果当前没有）
        if (string.IsNullOrEmpty(node.ConversationId) && node.ParentSpanId != "0000000000000000")
        {
            // 尝试从已记录的父节点中获取 ConversationId
            var parentNode = _activeTraces.Values.FirstOrDefault(n => n.SpanId == node.ParentSpanId);
            if (parentNode != null)
            {
                _logger.LogInformation(
                    "  🔍 找到父节点: {ParentOp} | 父节点 ConversationId: {ParentConvId}",
                    parentNode.OperationName,
                    parentNode.ConversationId ?? "NULL");
                
                if (!string.IsNullOrEmpty(parentNode.ConversationId))
                {
                    node.ConversationId = parentNode.ConversationId;
                    _logger.LogInformation(
                        "  ✅ 继承父节点的 ConversationId: {ConversationId} (从 {ParentOp} 到 {CurrentOp})",
                        node.ConversationId,
                        parentNode.OperationName,
                        node.OperationName);
                }
                else
                {
                    _logger.LogWarning(
                        "  ⚠️ 父节点 {ParentOp} 也没有 ConversationId",
                        parentNode.OperationName);
                }
            }
            else
            {
                _logger.LogWarning(
                    "  ⚠️ 未找到父节点 (ParentSpanId: {ParentSpanId})",
                    node.ParentSpanId);
            }
        }
        else if (!string.IsNullOrEmpty(node.ConversationId))
        {
            _logger.LogInformation(
                "  ✅ Activity 已有 ConversationId: {ConversationId}",
                node.ConversationId);
        }

        // 📉 截断可能过大的 Tag 值，避免在内存中持有完整请求/响应
        TrimLargeTags(node);

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
        // 🎯 从缓存中移除 Activity 对象
        _activeActivities.TryRemove(activity.SpanId.ToString(), out _);
        
        var activityId = activity.Id ?? activity.TraceId.ToString();
        
        if (_activeTraces.TryRemove(activityId, out var node))
        {
            node.EndTime = DateTime.UtcNow;
            node.Duration = activity.Duration;
            node.Status = activity.Status == ActivityStatusCode.Ok ? "Success" : 
                         activity.Status == ActivityStatusCode.Error ? "Error" : "Completed";
            node.StatusDescription = activity.StatusDescription;

            // 🎯 重新更新完整的 Tags 字典（Activity 执行过程中可能添加了新的 Tags）
            node.Tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value?.ToString() ?? string.Empty);

            // 🎯 重新提取 Tags（因为 OnActivityStarted 时 Tags 可能为空）
            ExtractKeyInformation(activity, node);

            // 📉 截断可能过大的 Tag 值，避免在内存中长期保存大 payload
            TrimLargeTags(node);
            
            // 🎯 【CRITICAL FIX】在 OnActivityStopped 时再次尝试获取 ConversationId
            // 原因：Middleware 可能在 OnActivityStarted 之后才设置 gen_ai.conversation.id Tag
            var conversationIdFromTags = activity.Tags.FirstOrDefault(t => t.Key == ActivityExtensions.GenAIConversationId).Value;
            if (!string.IsNullOrEmpty(conversationIdFromTags))
            {
                _logger.LogInformation(
                    "  🔄 [ConversationId 更新] OnActivityStopped 时发现 ConversationId: {ConvId} | Activity: {Name}",
                    conversationIdFromTags, node.OperationName);
                node.ConversationId = conversationIdFromTags;
                
                // 🎯 【递归传播】传播到所有后代节点（不仅是直接子节点）
                var totalUpdated = PropagateConversationIdRecursively(conversationIdFromTags, node.SpanId, 1);
                
                if (totalUpdated > 0)
                {
                    _logger.LogInformation(
                        "  ✅ 已递归传播 ConversationId 到 {Count} 个后代节点",
                        totalUpdated);
                }
            }
            // 🎯 【FIX 2】如果当前节点没有 ConversationId，尝试从父节点获取
            // 原因：子节点启动时父节点可能还没有 ConversationId，现在父节点已经停止并设置了
            else if (string.IsNullOrEmpty(node.ConversationId) && !string.IsNullOrEmpty(node.ParentSpanId))
            {
                if (_activeTraces.TryGetValue(node.ParentSpanId, out var parentNode) && !string.IsNullOrEmpty(parentNode.ConversationId))
                {
                    node.ConversationId = parentNode.ConversationId;
                    _logger.LogInformation(
                        "  🔄 [ConversationId 继承] 从活跃父节点 {ParentName} 继承 ConversationId: {ConvId}",
                        parentNode.OperationName, node.ConversationId);
                }
                // 如果父节点已经完成，从完成队列中查找
                else if (_completedTraces.Any(n => n.SpanId == node.ParentSpanId))
                {
                    var completedParent = _completedTraces.FirstOrDefault(n => n.SpanId == node.ParentSpanId);
                    if (completedParent != null && !string.IsNullOrEmpty(completedParent.ConversationId))
                    {
                        node.ConversationId = completedParent.ConversationId;
                        _logger.LogInformation(
                            "  🔄 [ConversationId 继承] 从已完成的父节点 {ParentName} 继承 ConversationId: {ConvId}",
                            completedParent.OperationName, node.ConversationId);
                    }
                }
            }
            
            // 更新最终信息
            UpdateFinalInformation(activity, node);
            
            // 提取消息内容（从 Events 中）
            ExtractMessagesFromEvents(activity, node);

            // 🎯 异步持久化到数据库（通过后台服务）
            _ = _persistenceService.EnqueueAsync(node);
            // 添加到完成队列
            _completedTraces.Enqueue(node);
            
            // 限制队列大小
            while (_completedTraces.Count > MaxCompletedTraces)
            {
                _completedTraces.TryDequeue(out _);
            }
            
            // 🎯 异步保存到 HybridCache
            _ = SaveCompletedTracesToCacheAsync();

            // 🎯 触发事件通知
            ActivityStopped?.Invoke(this, node);

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
    /// 递归传播 ConversationId 到所有后代节点
    /// </summary>
    /// <param name="conversationId">要传播的 ConversationId</param>
    /// <param name="parentSpanId">父节点的 SpanId</param>
    /// <param name="depth">当前递归深度（用于日志缩进）</param>
    /// <returns>传播的节点数量</returns>
    private int PropagateConversationIdRecursively(string conversationId, string parentSpanId, int depth)
    {
        var indent = new string(' ', depth * 2);
        var count = 0;
        
        // 查找所有直接子节点（活跃 + 已完成）
        var activeChildren = _activeTraces.Values
            .Where(n => n.ParentSpanId == parentSpanId && string.IsNullOrEmpty(n.ConversationId))
            .ToList();
        
        var completedChildren = _completedTraces
            .Where(n => n.ParentSpanId == parentSpanId && string.IsNullOrEmpty(n.ConversationId))
            .ToList();
        
        var allChildren = activeChildren.Concat(completedChildren).ToList();
        
        foreach (var child in allChildren)
        {
            // 设置 ConversationId
            child.ConversationId = conversationId;
            count++;
            
            _logger.LogInformation(
                "  {Indent}⬇️ [递归传播 L{Depth}] {ChildName} (SpanId: {ChildSpanId})",
                indent, depth, child.OperationName, child.SpanId);
            
            // 递归传播到子节点的子节点
            var childCount = PropagateConversationIdRecursively(conversationId, child.SpanId, depth + 1);
            count += childCount;
        }
        
        return count;
    }

    /// <summary>
    /// 截断过大的 Tag，防止在内存中保留完整请求/响应正文
    /// </summary>
    private void TrimLargeTags(TraceNode node)
    {
        if (node.Tags == null || node.Tags.Count == 0)
        {
            return;
        }

        foreach (var key in node.Tags.Keys.ToList())
        {
            var value = node.Tags[key];
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var threshold = LargePayloadTagKeys.Contains(key) ? LargePayloadTagLength : MaxTagValueLength;
            var shouldTrim = value.Length > threshold;

            if (shouldTrim)
            {
                var truncated = value.Substring(0, threshold);
                node.Tags[key] = $"{truncated}... [truncated {value.Length} chars]";
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
                    node.Name = tag.Value;
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
                    node.Name = tag.Value;
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
                        node.Name = tag.Value;
                        node.IsToolCall = true;
                    }
                    break;
            }
        }
        
        // 🎯 新增: 从 Activity Events 中提取工具调用信息
        // Microsoft.Extensions.AI 使用 Events 记录 tool_call 和 tool_result
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
                            if (string.IsNullOrEmpty(node.Name))
                            {
                                node.Name = tag.Value?.ToString();
                            }
                            else
                            {
                                node.Name += ", " + tag.Value;
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

        // 从 DisplayName 推断类型并尝试提取 Name (AppName/ToolName)
        // 只有在没有从 Tags 中提取到明确类型时才进行推断
        if (!node.IsAppCall && !node.IsToolCall)
        {
            if (node.OperationName.Contains("chat", StringComparison.OrdinalIgnoreCase))
            {
                node.IsAppCall = true;

                // 🎯 从 OperationName 提取模型名称作为 Name (AppName)
                // OperationName 格式: "chat {model}" 或 "chat"
                if (string.IsNullOrEmpty(node.Name))
                {
                    // 尝试从 "chat gpt-4" 提取 "gpt-4"
                    var parts = node.OperationName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1 && !parts[1].StartsWith("/")) // 排除 "/v1/chat/completions" 这种情况
                    {
                        node.Name = string.Join(" ", parts.Skip(1));
                        _logger.LogInformation("  ✅ Extracted AppName from OperationName: {AppName}", node.Name);
                    }
                    // 如果没有模型名，尝试从 gen_ai.request.model tag 获取
                    else if (node.Tags.TryGetValue("gen_ai.request.model", out var modelFromTag))
                    {
                        node.Name = modelFromTag;
                        _logger.LogInformation("  ✅ Extracted AppName from gen_ai.request.model tag: {AppName}", node.Name);
                    }
                }
            }
            else if (node.OperationName.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                     node.OperationName.Contains("function", StringComparison.OrdinalIgnoreCase))
            {
                node.IsToolCall = true;

                // 🎯 从 OperationName 提取工具名称作为 Name (ToolName)
                // OperationName 格式: "llmpool.tool {toolName}" 或 "tool {toolName}"
                if (string.IsNullOrEmpty(node.Name))
                {
                    // 尝试从 "llmpool.tool calc" 或 "tool calc" 提取 "calc"
                    var parts = node.OperationName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        // 最后一部分就是工具名
                        node.Name = parts[^1]; // 等同于 parts[parts.Length - 1]
                        _logger.LogInformation("  ✅ Extracted ToolName from OperationName: {ToolName}", node.Name);
                    }
                    else
                    {
                        // 尝试从 "llmpool.tool.calc" 这种格式提取（用点分隔）
                        var dotParts = node.OperationName.Split('.', StringSplitOptions.RemoveEmptyEntries);
                        if (dotParts.Length > 2 && dotParts[^2].Equals("tool", StringComparison.OrdinalIgnoreCase))
                        {
                            node.Name = dotParts[^1];
                            _logger.LogInformation("  ✅ Extracted ToolName from dot-separated OperationName: {ToolName}", node.Name);
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
                    node.ServerType = ActivityServerType.McpServer;
                    break;

                case "mcp.server.name":
                    node.McpServerName = tag.Value;
                    node.ServerType = ActivityServerType.McpServer;
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
            node.ToolType = ActivityToolType.McpTool;
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
            node.ToolType = ActivityToolType.AppTool;
            node.IsToolCall = true;
        }
        // 3. 识别 MCP Server Activity (initialize, tools/list等)
        else if (node.OperationName.Contains("initialize", StringComparison.OrdinalIgnoreCase) ||
                 node.OperationName.Contains("tools/list", StringComparison.OrdinalIgnoreCase) ||
                 node.OperationName.Contains("notifications/", StringComparison.OrdinalIgnoreCase) ||
                 node.Tags.ContainsKey("mcp.session.id"))
        {
            node.ServerType = ActivityServerType.McpServer;

            // 尝试从 Tags 中获取 MCP Server 名称
            if (node.Tags.TryGetValue("mcp.server.name", out var serverName))
            {
                node.McpServerName = serverName;
            }
        }
        // 4. 识别 LlmPool Server (llmpool.server)
        else if (node.OperationName.StartsWith("llmpool.server", StringComparison.OrdinalIgnoreCase))
        {
            node.ServerType = ActivityServerType.LlmPoolServer;
        }
        // 5. 识别 HTTP Request
        else if (node.OperationName.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                 node.Kind == "Client")
        {
            // HTTP 请求类型 - 可以进一步细分
            if (node.ServerAddress != null && node.ServerAddress.Contains("localhost"))
            {
                node.ServerType = ActivityServerType.LlmPoolServer;
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
            foreach (var evt in activity.Events)
            {    
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
                // 🎯 首先尝试从 gen_ai.prompt 和 gen_ai.completion Tags 提取 (我们自己添加的)
                if (node.Tags.TryGetValue("gen_ai.prompt", out var promptJson))
                {
                    // 如果已被截断，则不要尝试解析，避免 JSON 格式错误
                    if (!promptJson.Contains("[truncated", StringComparison.OrdinalIgnoreCase))
                    {
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
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("  ❌ Failed to parse gen_ai.prompt JSON: {Error}", ex.Message);
                        }
                    }
                }
                
                if (node.Tags.TryGetValue("gen_ai.completion", out var completionText))
                {
                    node.OutputContent = completionText;
                }
                
                // 兼容：也尝试旧的 input.messages 格式
                if (node.InputMessages.Count == 0 && node.Tags.TryGetValue("input.messages", out var inputMsg))
                {   
                    if (!inputMsg.Contains("[truncated", StringComparison.OrdinalIgnoreCase))
                    {
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
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("  ❌ Failed to parse input.messages JSON: {Error}", ex.Message);
                        }
                    }
                }
                
                // 兼容：也尝试旧的 output.content 格式
                if (string.IsNullOrEmpty(node.OutputContent) && node.Tags.TryGetValue("output.content", out var outputContent))
                {
                    node.OutputContent = outputContent;
                }
            }
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
    /// 获取所有 Conversation 列表（只返回有真实 ConversationId 的会话）
    /// 排除自动生成的 conv-auto-* 条目
    /// </summary>
    public List<ConversationInfo> GetConversationList()
    {
        var allTraces = _completedTraces.Concat(_activeTraces.Values).ToList();
        
        // 🎯 只筛选出有真实 ConversationId 的 traces（排除 null、空字符串、conv-auto-*）
        var tracesWithRealConvId = allTraces
            .Where(t => !string.IsNullOrEmpty(t.ConversationId) &&
                       !t.ConversationId.StartsWith("conv-auto-"))
            .ToList();
        
        // 如果没有任何真实的 ConversationId，返回空列表
        if (!tracesWithRealConvId.Any())
        {
            return new List<ConversationInfo>();
        }
        
        var conversations = tracesWithRealConvId
            .GroupBy(t => t.ConversationId!)
            .Select(g => new ConversationInfo
            {
                ConversationId = g.Key,
                RequestCount = g.Count(),
                FirstRequestTime = g.Min(t => t.StartTime),
                LastRequestTime = g.Max(t => t.StartTime),
                TotalInputTokens = g.Sum(t => t.InputTokens),
                TotalOutputTokens = g.Sum(t => t.OutputTokens),
                IsActive = g.Any(t => !t.EndTime.HasValue),
                SuccessCount = g.Count(t => t.Status == "Success"),
                ErrorCount = g.Count(t => t.Status == "Error"),
                Models = g.Where(t => !string.IsNullOrEmpty(t.ModelId))
                         .Select(t => t.ModelId!)
                         .Distinct()
                         .ToList()
            })
            .OrderByDescending(c => c.LastRequestTime)
            .ToList();

        return conversations;
    }

    /// <summary>
    /// 获取最新的N条会话列表（用于实时模式限制数据量）
    /// </summary>
    /// <param name="maxCount">最大返回数量，默认100</param>
    /// <returns>按最后请求时间倒序排列的会话列表</returns>
    public List<ConversationInfo> GetRecentConversationList(int maxCount = 100)
    {
        return GetConversationList()
            .Take(maxCount)
            .ToList();
    }

    public async Task<(IReadOnlyList<ConversationInfo> Items, int TotalCount)> GetHistoricalConversationsAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedPageSize = Math.Max(pageSize, 1);
        var normalizedPageIndex = Math.Max(pageIndex, 1);
        var skip = (normalizedPageIndex - 1) * normalizedPageSize;

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IActivityTraceRepository>();

        return await repository.GetConversationsAsync(skip, normalizedPageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<TraceNode>> GetHistoricalTracesByConversationsAsync(
        IReadOnlyCollection<string> conversationIds,
        CancellationToken cancellationToken = default)
    {
        if (conversationIds == null || conversationIds.Count == 0)
        {
            return new List<TraceNode>();
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IActivityTraceRepository>();

        return await repository.GetTracesByConversationIdsAsync(conversationIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 根据 ConversationId 获取该 Conversation 的所有追踪（树形结构）
    /// </summary>
    public List<TraceNode> GetTracesByConversation(string conversationId)
    {
        var allTraces = _completedTraces
            .Concat(_activeTraces.Values)
            .ToList();

        // 🎯 第一步: 找到所有包含该 ConversationId 的 Activity
        var activitiesWithConversationId = allTraces
            .Where(t => t.ConversationId == conversationId)
            .ToList();

        if (!activitiesWithConversationId.Any())
        {
            _logger.LogInformation("未找到 ConversationId={ConversationId} 的 Activity", conversationId);
            return new List<TraceNode>();
        }

        // 🎯 第二步: 提取所有相关的 TraceId
        var relatedTraceIds = activitiesWithConversationId
            .Select(t => t.TraceId)
            .Distinct()
            .ToHashSet();

        // 🎯 第三步: 查询所有这些 TraceId 下的 Activity (包括没有 ConversationId 的工具调用)
        var allRelatedTraces = allTraces
            .Where(t => relatedTraceIds.Contains(t.TraceId))
            .ToList();

        // 🎯 第四步: 按 TraceId 分组，构建多个调用树
        var traceGroups = allRelatedTraces.GroupBy(t => t.TraceId);
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
    /// 批量查询多个会话的调用树（性能优化版本）
    /// </summary>
    /// <param name="conversationIds">会话ID列表</param>
    /// <returns>所有会话的调用树根节点列表</returns>
    public List<TraceNode> GetTracesByConversations(List<string> conversationIds)
    {
        if (conversationIds == null || !conversationIds.Any())
        {
            return new List<TraceNode>();
        }

        var allTraces = _completedTraces
            .Concat(_activeTraces.Values)
            .ToList();

        // 🎯 第一步: 找到所有包含这些 ConversationId 的 Activity
        var conversationIdSet = conversationIds.ToHashSet();
        var activitiesWithConversationIds = allTraces
            .Where(t => !string.IsNullOrEmpty(t.ConversationId) && conversationIdSet.Contains(t.ConversationId))
            .ToList();

        if (!activitiesWithConversationIds.Any())
        {
            _logger.LogInformation("未找到任何指定的 ConversationId 的 Activity");
            return new List<TraceNode>();
        }

        // 🎯 第二步: 提取所有相关的 TraceId
        var relatedTraceIds = activitiesWithConversationIds
            .Select(t => t.TraceId)
            .Distinct()
            .ToHashSet();

        // 🎯 第三步: 查询所有这些 TraceId 下的 Activity (包括没有 ConversationId 的工具调用)
        var allRelatedTraces = allTraces
            .Where(t => relatedTraceIds.Contains(t.TraceId))
            .ToList();

        // 🎯 第四步: 按 TraceId 分组，构建多个调用树
        var traceGroups = allRelatedTraces.GroupBy(t => t.TraceId);
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

            // 🎯 异步持久化到数据库（通过后台服务）
            _ = _persistenceService.EnqueueAsync(node);
            // 直接添加到完成队列 (外部 Activity 已经完成)
            _completedTraces.Enqueue(node);

            // 限制队列大小
            while (_completedTraces.Count > MaxCompletedTraces)
            {
                _completedTraces.TryDequeue(out _);
            }

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
