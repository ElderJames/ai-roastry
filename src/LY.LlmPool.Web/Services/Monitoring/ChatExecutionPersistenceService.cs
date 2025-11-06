using System.Text.Json;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services.Monitoring;

/// <summary>
/// 聊天执行监控数据持久化服务
/// </summary>
public class ChatExecutionPersistenceService
{
    private readonly IDbContextFactory<LlmDbContext> _contextFactory;
    private readonly ILogger<ChatExecutionPersistenceService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ChatExecutionPersistenceService(
        IDbContextFactory<LlmDbContext> contextFactory,
        ILogger<ChatExecutionPersistenceService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// 创建执行记录（仅主记录，不包含 Start 节点）
    /// 在 Controller 阶段调用，用于尽早建立执行记录
    /// </summary>
    public async Task<ChatExecutionRecord> CreateExecutionRecordAsync(
        string requestId,
        string? requestModel,
        int messageCount,
        DateTime startTime)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var record = new ChatExecutionRecord
            {
                RequestId = requestId,
                RequestModel = requestModel,  // 存储请求的模型标识
                StartTime = startTime,
                MessageCount = messageCount,
                IsSuccessful = false  // 初始状态为未完成
            };

            context.ChatExecutionRecords.Add(record);
            await context.SaveChangesAsync();

            _logger.LogInformation("[{RequestId}] 创建执行记录: RecordId={RecordId}, RequestModel={RequestModel}",
                requestId, record.Id, requestModel);

            return record;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] 创建执行记录失败", requestId);
            return null!;
        }
    }

    /// <summary>
    /// 为已存在的执行记录添加 Start 节点
    /// 在 MonitoringDelegatingChatClient 中调用，此时具备完整的消息、工具、选项信息
    /// </summary>
    public async Task AddStartNodeAsync(
        string executionRecordId,
        ChatExecutionMonitor monitor,
        IEnumerable<ChatMessage> messages,
        string? modelName,
        List<AITool>? tools,
        ChatOptions? options)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            // 更新主记录的实际模型名称和工具数量
            var record = await context.ChatExecutionRecords
                .FirstOrDefaultAsync(r => r.Id == executionRecordId);

            if (record == null)
            {
                _logger.LogWarning("[{RequestId}] 执行记录不存在: RecordId={RecordId}",
                    monitor.RequestId, executionRecordId);
                return;
            }

            record.ModelName = modelName;  // 更新实际执行的模型名称
            record.ToolCount = tools?.Count ?? 0;

            // 创建 Start 节点
            var startNode = CreateStartNode(executionRecordId, monitor, messages, modelName, tools, options);
            context.ChatExecutionTimelineNodes.Add(startNode);

            await context.SaveChangesAsync();

            _logger.LogInformation("[{RequestId}] 添加 Start 节点: RecordId={RecordId}, ModelName={ModelName}",
                monitor.RequestId, executionRecordId, modelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] 添加 Start 节点失败", monitor.RequestId);
        }
    }

    /// <summary>
    /// 添加文本块节点
    /// </summary>
    public async Task AddTextChunkNodeAsync(
        string executionRecordId,
        string requestId,
        string text,
        double elapsedMs)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var sequence = await GetNextSequenceAsync(context, executionRecordId);

            // 自动计算 delta_ms：查询上一个节点的 elapsed_ms
            var previousElapsedMs = await context.ChatExecutionTimelineNodes
                .Where(n => n.ExecutionRecordId == executionRecordId && n.Sequence == sequence - 1)
                .Select(n => n.ElapsedMs)
                .FirstOrDefaultAsync();

            var deltaMs = elapsedMs - previousElapsedMs;

            var node = new ChatExecutionTimelineNode
            {
                ExecutionRecordId = executionRecordId,
                NodeType = TimelineNodeType.TextChunk,
                Sequence = sequence,
                Timestamp = DateTime.UtcNow,
                ElapsedMs = elapsedMs,
                DeltaMs = deltaMs,
                Data = JsonSerializer.Serialize(new
                {
                    text = text,
                    length = text.Length
                }, _jsonOptions)
            };

            context.ChatExecutionTimelineNodes.Add(node);
            await context.SaveChangesAsync();

            _logger.LogDebug("[{RequestId}] 添加文本块节点: Sequence={Sequence}, Length={Length}, DeltaMs={DeltaMs}",
                requestId, sequence, text.Length, deltaMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] 添加文本块节点失败", requestId);
        }
    }

    /// <summary>
    /// 添加工具调用节点
    /// </summary>
    public async Task AddToolCallNodeAsync(
        string executionRecordId,
        string requestId,
        string toolName,
        string callId,
        string arguments,
        double elapsedMs)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var sequence = await GetNextSequenceAsync(context, executionRecordId);

            // 自动计算 delta_ms：查询上一个节点的 elapsed_ms
            var previousElapsedMs = await context.ChatExecutionTimelineNodes
                .Where(n => n.ExecutionRecordId == executionRecordId && n.Sequence == sequence - 1)
                .Select(n => n.ElapsedMs)
                .FirstOrDefaultAsync();

            var deltaMs = elapsedMs - previousElapsedMs;

            var node = new ChatExecutionTimelineNode
            {
                ExecutionRecordId = executionRecordId,
                NodeType = TimelineNodeType.ToolCall,
                Sequence = sequence,
                Timestamp = DateTime.UtcNow,
                ElapsedMs = elapsedMs,
                DeltaMs = deltaMs,
                Data = JsonSerializer.Serialize(new
                {
                    name = toolName,
                    callId = callId,
                    arguments = JsonSerializer.Deserialize<object>(arguments) // 解析为对象以便更好地存储
                }, _jsonOptions)
            };

            context.ChatExecutionTimelineNodes.Add(node);
            await context.SaveChangesAsync();

            _logger.LogDebug("[{RequestId}] 添加工具调用节点: Sequence={Sequence}, Tool={ToolName}, CallId={CallId}, DeltaMs={DeltaMs}",
                requestId, sequence, toolName, callId, deltaMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] 添加工具调用节点失败", requestId);
        }
    }

    /// <summary>
    /// 添加工具结果节点
    /// </summary>
    public async Task AddToolResultNodeAsync(
        string executionRecordId,
        string requestId,
        string callId,
        bool isSuccess,
        string? result,
        string? error,
        double elapsedMs)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var sequence = await GetNextSequenceAsync(context, executionRecordId);

            // 自动计算 delta_ms：查询上一个节点的 elapsed_ms
            var previousElapsedMs = await context.ChatExecutionTimelineNodes
                .Where(n => n.ExecutionRecordId == executionRecordId && n.Sequence == sequence - 1)
                .Select(n => n.ElapsedMs)
                .FirstOrDefaultAsync();

            var deltaMs = elapsedMs - previousElapsedMs;

            var node = new ChatExecutionTimelineNode
            {
                ExecutionRecordId = executionRecordId,
                NodeType = TimelineNodeType.ToolResult,
                Sequence = sequence,
                Timestamp = DateTime.UtcNow,
                ElapsedMs = elapsedMs,
                DeltaMs = deltaMs,
                Data = JsonSerializer.Serialize(new
                {
                    callId = callId,
                    isSuccess = isSuccess,
                    result = result,
                    error = error,
                    resultLength = result?.Length ?? 0
                }, _jsonOptions)
            };

            context.ChatExecutionTimelineNodes.Add(node);
            await context.SaveChangesAsync();

            _logger.LogDebug("[{RequestId}] 添加工具结果节点: Sequence={Sequence}, CallId={CallId}, Success={Success}, DeltaMs={DeltaMs}",
                requestId, sequence, callId, isSuccess, deltaMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] 添加工具结果节点失败", requestId);
        }
    }

    /// <summary>
    /// 完成执行记录（Complete 节点）
    /// </summary>
    public async Task CompleteExecutionRecordAsync(
        string executionRecordId,
        ChatExecutionMonitor monitor,
        bool isSuccessful = true,
        string? errorMessage = null)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var record = await context.ChatExecutionRecords
                .FirstOrDefaultAsync(r => r.Id == executionRecordId);

            if (record == null)
            {
                _logger.LogWarning("[{RequestId}] 执行记录不存在: RecordId={RecordId}",
                    monitor.RequestId, executionRecordId);
                return;
            }

            // 更新主记录
            record.EndTime = DateTime.UtcNow;
            record.TotalDurationMs = monitor.PhaseEvents.LastOrDefault()?.ElapsedMs ?? 0;
            record.TtfbMs = monitor.PhaseEvents.FirstOrDefault(e => e.Phase == ExecutionPhase.FirstToken)?.ElapsedMs;
            record.ToolCallCount = monitor.ToolCalls.Count;
            record.ChunkCount = monitor.ChunkCount;
            record.TotalCharacters = monitor.TotalCharacters;
            record.IsSuccessful = isSuccessful;
            record.ErrorMessage = errorMessage;
            record.Summary = JsonSerializer.Serialize(new
            {
                summary = monitor.GetSummary(),
                phaseEvents = monitor.PhaseEvents.Select(p => new
                {
                    phase = p.Phase.ToString(),
                    elapsedMs = p.ElapsedMs,
                    deltaMs = p.DeltaMs,
                    metadata = p.Metadata
                }),
                toolCalls = monitor.ToolCalls.Select(t => new
                {
                    name = t.Name,
                    callId = t.CallId,
                    durationMs = t.DurationMs,
                    isSuccess = t.IsSuccess
                })
            }, _jsonOptions);

            // 创建 Complete 节点
            var sequence = await GetNextSequenceAsync(context, executionRecordId);

            // 自动计算 delta_ms：查询上一个节点的 elapsed_ms
            var previousElapsedMs = await context.ChatExecutionTimelineNodes
                .Where(n => n.ExecutionRecordId == executionRecordId && n.Sequence == sequence - 1)
                .Select(n => n.ElapsedMs)
                .FirstOrDefaultAsync();

            var completeElapsedMs = record.TotalDurationMs ?? 0;
            var deltaMs = completeElapsedMs - previousElapsedMs;

            var completeNode = new ChatExecutionTimelineNode
            {
                ExecutionRecordId = executionRecordId,
                NodeType = TimelineNodeType.Complete,
                Sequence = sequence,
                Timestamp = DateTime.UtcNow,
                ElapsedMs = completeElapsedMs,
                DeltaMs = deltaMs,
                Data = record.Summary ?? "{}"
            };

            context.ChatExecutionTimelineNodes.Add(completeNode);
            await context.SaveChangesAsync();

            _logger.LogInformation("[{RequestId}] 完成执行记录: RecordId={RecordId}, TotalDurationMs={Duration}",
                monitor.RequestId, executionRecordId, record.TotalDurationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] 完成执行记录失败", monitor.RequestId);
        }
    }

    /// <summary>
    /// 创建 Start 节点
    /// </summary>
    private ChatExecutionTimelineNode CreateStartNode(
        string executionRecordId,
        ChatExecutionMonitor monitor,
        IEnumerable<ChatMessage> messages,
        string? modelName,
        List<AITool>? tools,
        ChatOptions? options)
    {
        return new ChatExecutionTimelineNode
        {
            ExecutionRecordId = executionRecordId,
            NodeType = TimelineNodeType.Start,
            Sequence = 0,
            Timestamp = DateTime.UtcNow,
            ElapsedMs = 0,
            DeltaMs = 0,
            Data = JsonSerializer.Serialize(new
            {
                messages = messages.Select(m => new
                {
                    role = m.Role.Value,
                    text = m.Text,
                    contentCount = m.Contents?.Count ?? 0
                }),
                model = modelName,
                tools = tools?.Select(t => new
                {
                    name = t.Name ?? "Unknown",
                    description = t.Description
                }),
                options = options != null ? new
                {
                    temperature = options.Temperature,
                    maxTokens = options.MaxOutputTokens,
                    topP = options.TopP,
                    stopSequences = options.StopSequences
                } : null
            }, _jsonOptions)
        };
    }

    /// <summary>
    /// 获取下一个序号
    /// </summary>
    private async Task<int> GetNextSequenceAsync(LlmDbContext context, string executionRecordId)
    {
        var maxSequence = await context.ChatExecutionTimelineNodes
            .Where(n => n.ExecutionRecordId == executionRecordId)
            .MaxAsync(n => (int?)n.Sequence);

        return (maxSequence ?? -1) + 1;
    }

    /// <summary>
    /// 更新执行记录为失败状态
    /// 在 Controller 阶段发生错误时调用
    /// </summary>
    public async Task UpdateExecutionRecordErrorAsync(
        string executionRecordId,
        string errorMessage)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var record = await context.ChatExecutionRecords
                .FirstOrDefaultAsync(r => r.Id == executionRecordId);

            if (record == null)
            {
                _logger.LogWarning("执行记录不存在: RecordId={RecordId}", executionRecordId);
                return;
            }

            record.IsSuccessful = false;
            record.ErrorMessage = errorMessage;
            record.EndTime = DateTime.UtcNow;

            await context.SaveChangesAsync();

            _logger.LogInformation("更新执行记录失败状态: RecordId={RecordId}, Error={Error}",
                executionRecordId, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新执行记录失败状态时出错: RecordId={RecordId}", executionRecordId);
        }
    }

    /// <summary>
    /// 通过 ID 查询执行记录
    /// </summary>
    public async Task<ChatExecutionRecord?> GetExecutionRecordByIdAsync(string executionRecordId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.ChatExecutionRecords
                .Include(r => r.TimelineNodes.OrderBy(n => n.Sequence))
                .FirstOrDefaultAsync(r => r.Id == executionRecordId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询执行记录失败: RecordId={RecordId}", executionRecordId);
            return null;
        }
    }

    /// <summary>
    /// 通过 RequestId 查询执行记录
    /// </summary>
    public async Task<ChatExecutionRecord?> GetExecutionRecordAsync(string requestId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.ChatExecutionRecords
                .Include(r => r.TimelineNodes.OrderBy(n => n.Sequence))
                .FirstOrDefaultAsync(r => r.RequestId == requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] 查询执行记录失败", requestId);
            return null;
        }
    }

    /// <summary>
    /// 查询时间线节点
    /// </summary>
    public async Task<List<ChatExecutionTimelineNode>> GetTimelineNodesAsync(string executionRecordId)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.ChatExecutionTimelineNodes
                .Where(n => n.ExecutionRecordId == executionRecordId)
                .OrderBy(n => n.Sequence)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询时间线节点失败: ExecutionRecordId={ExecutionRecordId}", executionRecordId);
            return new List<ChatExecutionTimelineNode>();
        }
    }

    /// <summary>
    /// 根据 RequestModel 查询执行记录列表
    /// </summary>
    public async Task<List<ChatExecutionRecord>> GetRecordsByRequestModelAsync(string requestModel, int limit = 100)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.ChatExecutionRecords
                .Where(r => r.RequestModel == requestModel)
                .OrderByDescending(r => r.StartTime)
                .Take(limit)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询执行记录失败: RequestModel={RequestModel}", requestModel);
            return new List<ChatExecutionRecord>();
        }
    }

    /// <summary>
    /// 根据 RequestModel 查询执行记录列表（支持分页）
    /// </summary>
    public async Task<List<ChatExecutionRecord>> GetRecordsByRequestModelAsync(string requestModel, int pageSize, int skip)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.ChatExecutionRecords
                .Where(r => r.RequestModel == requestModel)
                .OrderByDescending(r => r.StartTime)
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询执行记录失败: RequestModel={RequestModel}", requestModel);
            return new List<ChatExecutionRecord>();
        }
    }

    /// <summary>
    /// 根据 RequestModel 获取执行记录总数
    /// </summary>
    public async Task<int> GetRecordsCountByRequestModelAsync(string requestModel)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            return await context.ChatExecutionRecords
                .Where(r => r.RequestModel == requestModel)
                .CountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取执行记录总数失败: RequestModel={RequestModel}", requestModel);
            return 0;
        }
    }
}
