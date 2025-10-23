using System.Text.Json;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Repositories;

/// <summary>
/// Activity 追踪数据仓储实现
/// </summary>
public class ActivityTraceRepository : IActivityTraceRepository
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly ILogger<ActivityTraceRepository> _logger;

    public ActivityTraceRepository(
        IDbContextFactory<LlmDbContext> dbContextFactory,
        ILogger<ActivityTraceRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// 批量保存 Activity 追踪记录
    /// </summary>
    public async Task SaveBatchAsync(IEnumerable<TraceNode> nodes, CancellationToken cancellationToken = default)
    {
        if (nodes == null || !nodes.Any())
            return;

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entities = nodes.Select(MapToEntity).ToList();

            // 批量添加（EF Core 会自动优化为批量插入）
            await db.ActivityTraces.AddRangeAsync(entities, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("✅ 批量保存 {Count} 条 Activity 追踪记录成功", entities.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ 批量保存 Activity 追踪记录失败，批次大小: {Count}", nodes.Count());
            throw;
        }
    }
    /// <summary>
    /// 删除指定时间之前的记录（预留用于数据清理）
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var count = await db.ActivityTraces
                .Where(t => t.StartTime < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            _logger.LogInformation("✅ 删除 {Count} 条过期的 Activity 追踪记录（早于 {Cutoff}）", count, cutoff);

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ 删除过期的 Activity 追踪记录失败，Cutoff: {Cutoff}", cutoff);
            throw;
        }
    }

    public async Task<(IReadOnlyList<ConversationInfo> Items, int TotalCount)> GetConversationsAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var baseQuery = db.ActivityTraces
            .AsNoTracking()
            .Where(t =>
                t.ConversationId != null &&
                t.ConversationId != string.Empty &&
                !t.ConversationId.StartsWith("conv-auto-"));

        var totalCount = await baseQuery
            .Select(t => t.ConversationId!)
            .Distinct()
            .CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return (Array.Empty<ConversationInfo>(), 0);
        }

        var conversationRows = await baseQuery
            .GroupBy(t => t.ConversationId!)
            .OrderByDescending(g => g.Max(t => t.StartTime))
            .Skip(skip)
            .Take(take)
            .Select(g => new
            {
                ConversationId = g.Key,
                RequestCount = g.Count(),
                FirstRequestTime = g.Min(t => t.StartTime),
                LastRequestTime = g.Max(t => t.StartTime),
                TotalInputTokens = g.Sum(t => t.InputTokens),
                TotalOutputTokens = g.Sum(t => t.OutputTokens),
                IsActive = g.Any(t => t.EndTime == null),
                SuccessCount = g.Count(t => t.Status == "Success"),
                ErrorCount = g.Count(t => t.Status == "Error"),
                Models = g
                    .Where(t => t.ModelId != null && t.ModelId != string.Empty)
                    .Select(t => t.ModelId!)
            })
            .ToListAsync(cancellationToken);

        var conversations = conversationRows
            .Select(row => new ConversationInfo
            {
                ConversationId = row.ConversationId,
                RequestCount = row.RequestCount,
                FirstRequestTime = row.FirstRequestTime,
                LastRequestTime = row.LastRequestTime,
                TotalInputTokens = row.TotalInputTokens,
                TotalOutputTokens = row.TotalOutputTokens,
                IsActive = row.IsActive,
                SuccessCount = row.SuccessCount,
                ErrorCount = row.ErrorCount,
                Models = row.Models.Distinct().ToList()
            })
            .ToList();

        return (conversations, totalCount);
    }

    public async Task<List<TraceNode>> GetTracesByConversationIdsAsync(
        IReadOnlyCollection<string> conversationIds,
        CancellationToken cancellationToken = default)
    {
        if (conversationIds == null || conversationIds.Count == 0)
        {
            return new List<TraceNode>();
        }

        var normalizedConversationIds = conversationIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();

        if (normalizedConversationIds.Length == 0)
        {
            return new List<TraceNode>();
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var relatedTraceIds = await db.ActivityTraces
            .AsNoTracking()
            .Where(t =>
                t.ConversationId != null &&
                normalizedConversationIds.Contains(t.ConversationId))
            .Select(t => t.TraceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (relatedTraceIds.Count == 0)
        {
            return new List<TraceNode>();
        }

        var traceEntities = await db.ActivityTraces
            .AsNoTracking()
            .Where(t => relatedTraceIds.Contains(t.TraceId))
            .ToListAsync(cancellationToken);

        if (traceEntities.Count == 0)
        {
            return new List<TraceNode>();
        }

        var nodes = traceEntities
            .Select(MapToNode)
            .ToList();

        return BuildTreesGroupedByTraceId(nodes);
    }

    /// <summary>
    /// 统计“已完成”的根调用数据（按根节点聚合）。
    /// </summary>
    public async Task<TraceStatistics> GetCompletedRootStatisticsAsync(
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var rootsQuery = db.ActivityTraces.AsNoTracking()
            .Where(t => t.EndTime != null)
            .Where(t =>
                string.IsNullOrEmpty(t.ParentSpanId) ||
                t.ParentSpanId == "0000000000000000" ||
                t.ParentSpanId == "00000000000000000" ||
                t.ParentSpanId == "000000000000000000");

        if (!string.IsNullOrEmpty(conversationId))
        {
            var filteredTraceIds = await db.ActivityTraces.AsNoTracking()
                .Where(t => t.ConversationId == conversationId)
                .Select(t => t.TraceId)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (filteredTraceIds.Count == 0)
            {
                return new TraceStatistics();
            }

            rootsQuery = rootsQuery.Where(t => filteredTraceIds.Contains(t.TraceId));
        }

        var stats = new TraceStatistics();

        // 根调用数量
        stats.TotalTraces = await rootsQuery.CountAsync(cancellationToken);

        // 耗时（毫秒）
        var durations = await rootsQuery.Select(t => (double)t.DurationMs).ToListAsync(cancellationToken);
        stats.TotalDurationMs = durations.Sum();
        stats.AverageDurationMs = durations.Count > 0 ? durations.Average() : 0d;

        // Tokens（根节点级别）
        stats.TotalInputTokens = await rootsQuery.SumAsync(t => (int?)t.InputTokens, cancellationToken) ?? 0;
        stats.TotalOutputTokens = await rootsQuery.SumAsync(t => (int?)t.OutputTokens, cancellationToken) ?? 0;

        // 成功/错误计数（根节点）
        stats.SuccessCount = await rootsQuery.CountAsync(t => t.Status == "Success", cancellationToken);
        stats.ErrorCount = await rootsQuery.CountAsync(t => t.Status == "Error", cancellationToken);

        // 其他维度（根节点）
        stats.AppCallCount = await rootsQuery.CountAsync(t => t.IsAppCall, cancellationToken);
        stats.ToolCallCount = await rootsQuery.CountAsync(t => t.IsToolCall, cancellationToken);
        stats.UniqueModels = await rootsQuery
            .Where(t => t.ModelId != null && t.ModelId != "")
            .Select(t => t.ModelId!)
            .Distinct()
            .ToListAsync(cancellationToken);
        stats.UniqueTools = await rootsQuery
            .Where(t => t.IsToolCall && t.Name != null && t.Name != "")
            .Select(t => t.Name!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return stats;
    }

    private sealed class ConversationSummaryRow
    {
        public required string ConversationId { get; init; }
        public int RequestCount { get; init; }
        public DateTime FirstRequestTime { get; init; }
        public DateTime LastRequestTime { get; init; }
        public int TotalInputTokens { get; init; }
        public int TotalOutputTokens { get; init; }
        public int SuccessCount { get; init; }
        public int ErrorCount { get; init; }
        public List<string> Models { get; init; } = new();
    }

    // ==================== 私有辅助方法 ====================

    /// <summary>
    /// 将 TraceNode 映射到 ActivityTrace 实体
    /// </summary>
    private ActivityTrace MapToEntity(TraceNode node)
    {
        return new ActivityTrace
        {
            Id = Guid.NewGuid(),
            ActivityId = node.ActivityId,
            TraceId = node.TraceId,
            SpanId = node.SpanId,
            ParentSpanId = node.ParentSpanId,
            ConversationId = node.ConversationId,
            OperationName = node.OperationName,
            DisplayName = node.DisplayName,
            StartTime = node.StartTime,
            EndTime = node.EndTime,
            DurationMs = (long)node.Duration.TotalMilliseconds,
            Kind = node.Kind,
            Status = node.Status,
            StatusDescription = node.StatusDescription,
            ErrorType = node.ErrorType,
            ErrorStackTrace = node.ErrorStackTrace,

            // AI 相关
            OperationType = node.OperationType,
            ModelId = node.ModelId,
            ResponseModelId = node.ResponseModelId,
            ProviderName = node.ProviderName,
            ResponseId = node.ResponseId,
            FinishReason = node.FinishReason,
            Temperature = node.Temperature,
            MaxTokens = node.MaxTokens,
            InputTokens = node.InputTokens,
            OutputTokens = node.OutputTokens,
            ServerAddress = node.ServerAddress,
            ServerPort = node.ServerPort,

            // App 相关
            IsAppCall = node.IsAppCall,

            // Tool 相关
            IsToolCall = node.IsToolCall,
            Name = node.Name,  // 统一的名称字段（AppName 或 ToolName）
            ToolType = node.ToolType,
            ToolDataJson = (node.ToolArguments != null || node.ToolResult != null)
                ? JsonSerializer.Serialize(new ActivityToolData
                {
                    Arguments = node.ToolArguments,
                    Result = node.ToolResult
                })
                : null,

            // Server 相关
            ServerType = node.ServerType,
            McpServerName = node.McpServerName,

            // JSON 序列化
            InputMessagesJson = node.InputMessages.Any()
                ? JsonSerializer.Serialize(node.InputMessages)
                : null,
            OutputContent = node.OutputContent,
            TagsJson = node.Tags.Any()
                ? JsonSerializer.Serialize(node.Tags)
                : null,
            EventsJson = node.Events.Any()
                ? JsonSerializer.Serialize(node.Events)
                : null,

            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 将 ActivityTrace 实体映射到 TraceNode
    /// </summary>
    private TraceNode MapToNode(ActivityTrace entity)
    {
        return new TraceNode
        {
            ActivityId = entity.ActivityId,
            TraceId = entity.TraceId,
            SpanId = entity.SpanId,
            ParentSpanId = entity.ParentSpanId,
            OperationName = entity.OperationName,
            DisplayName = entity.DisplayName,
            StartTime = entity.StartTime,
            EndTime = entity.EndTime,
            Duration = TimeSpan.FromMilliseconds(entity.DurationMs),
            Kind = entity.Kind,
            Status = entity.Status,
            StatusDescription = entity.StatusDescription,
            Tags = !string.IsNullOrEmpty(entity.TagsJson)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(entity.TagsJson) ?? new()
                : new(),

            // AI 相关
            OperationType = entity.OperationType,
            ModelId = entity.ModelId,
            ResponseModelId = entity.ResponseModelId,
            ProviderName = entity.ProviderName,
            ConversationId = entity.ConversationId,
            ResponseId = entity.ResponseId,
            FinishReason = entity.FinishReason,
            Temperature = entity.Temperature,
            MaxTokens = entity.MaxTokens,
            InputTokens = entity.InputTokens,
            OutputTokens = entity.OutputTokens,
            ServerAddress = entity.ServerAddress,
            ServerPort = entity.ServerPort,
            ErrorType = entity.ErrorType,
            ErrorStackTrace = entity.ErrorStackTrace,

            // App 相关
            IsAppCall = entity.IsAppCall,

            // Tool 相关
            IsToolCall = entity.IsToolCall,
            Name = entity.Name,  // 统一的名称字段（AppName 或 ToolName）
            ToolType = entity.ToolType,
            ToolArguments = !string.IsNullOrEmpty(entity.ToolDataJson)
                ? JsonSerializer.Deserialize<ActivityToolData>(entity.ToolDataJson)?.Arguments
                : null,
            ToolResult = !string.IsNullOrEmpty(entity.ToolDataJson)
                ? JsonSerializer.Deserialize<ActivityToolData>(entity.ToolDataJson)?.Result
                : null,

            // Server 相关
            ServerType = entity.ServerType,
            McpServerName = entity.McpServerName,

            // Chat 消息内容
            InputMessages = !string.IsNullOrEmpty(entity.InputMessagesJson)
                ? JsonSerializer.Deserialize<List<TraceChatMessage>>(entity.InputMessagesJson) ?? new()
                : new(),
            OutputContent = entity.OutputContent,

            // Activity Events
            Events = !string.IsNullOrEmpty(entity.EventsJson)
                ? JsonSerializer.Deserialize<List<ActivityEventInfo>>(entity.EventsJson) ?? new()
                : new(),

            // 树形结构（Children 需要在外部构建）
            Children = new()
        };
    }

    /// <summary>
    /// 将同一 TraceId 下的节点构建为树，并返回所有根节点集合。
    /// </summary>
    private static List<TraceNode> BuildTreesGroupedByTraceId(List<TraceNode> nodes)
    {
        var roots = new List<TraceNode>();
        if (nodes.Count == 0) return roots;

        foreach (var group in nodes.GroupBy(n => n.TraceId))
        {
            var list = group.ToList();
            var dict = list.ToDictionary(n => n.SpanId);

            // 清空 Children，避免重复追加
            foreach (var n in list)
            {
                n.Children.Clear();
            }

            // 组装父子关系
            foreach (var n in list)
            {
                if (!string.IsNullOrEmpty(n.ParentSpanId) && n.ParentSpanId != "00000000000000000" && dict.TryGetValue(n.ParentSpanId, out var parent))
                {
                    if (!parent.Children.Contains(n)) parent.Children.Add(n);
                }
            }

            // 收集根节点（无父或父不在集合内或 ParentSpanId 为全零）
            foreach (var n in list)
            {
                if (string.IsNullOrEmpty(n.ParentSpanId) || n.ParentSpanId == "00000000000000000" || !dict.ContainsKey(n.ParentSpanId))
                {
                    roots.Add(n);
                }
            }

            // 子节点按开始时间排序（递归）
            foreach (var root in list.Where(r => roots.Contains(r)))
            {
                SortChildrenByStartTime(root);
            }
        }

        return roots;
    }

    private static void SortChildrenByStartTime(TraceNode node)
    {
        if (node.Children == null || node.Children.Count == 0) return;
        node.Children = node.Children.OrderBy(c => c.StartTime).ToList();
        foreach (var child in node.Children)
        {
            SortChildrenByStartTime(child);
        }
    }
}
