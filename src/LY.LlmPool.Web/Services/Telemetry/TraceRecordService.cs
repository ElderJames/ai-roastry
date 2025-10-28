using System.Text.Json;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models.Dto;
using LY.LlmPool.Web.Repositories;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// 追踪记录服务实现
/// </summary>
public class TraceRecordService : ITraceRecordService
{
    private readonly IActivityTraceRepository _repository;
    private readonly ILogger<TraceRecordService> _logger;

    public TraceRecordService(
        IActivityTraceRepository repository,
        ILogger<TraceRecordService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// 分页查询追踪记录
    /// </summary>
    public async Task<PagedResult<TraceRecordDto>> GetPagedRecordsAsync(
        TraceRecordQueryDto query,
        CancellationToken cancellationToken = default)
    {
        if (query == null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (string.IsNullOrWhiteSpace(query.Name))
        {
            _logger.LogWarning("⚠️ GetPagedRecordsAsync: Name 参数为空");
            return new PagedResult<TraceRecordDto>
            {
                Items = new List<TraceRecordDto>(),
                TotalCount = 0,
                PageIndex = query.PageIndex,
                PageSize = query.PageSize
            };
        }

        try
        {
            // 调用 Repository 查询数据（传递 UTC 时间）
            var (items, totalCount) = await _repository.GetTraceRecordsByNameAsync(
                name: query.Name,
                startTimeFrom: query.StartTimeFrom?.ToUniversalTime(),
                startTimeTo: query.StartTimeTo?.ToUniversalTime(),
                status: query.Status,
                traceId: query.TraceId,
                pageIndex: query.PageIndex,
                pageSize: query.PageSize,
                cancellationToken: cancellationToken);

            // 转换为 DTO
            var dtoItems = items.Select(MapToDto).ToList();

            _logger.LogInformation(
                "✅ GetPagedRecordsAsync 成功: Name={Name}, 返回 {Count} 条记录",
                query.Name, dtoItems.Count);

            return new PagedResult<TraceRecordDto>
            {
                Items = dtoItems,
                TotalCount = totalCount,
                PageIndex = query.PageIndex,
                PageSize = query.PageSize
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ GetPagedRecordsAsync 失败: Name={Name}", query.Name);
            throw;
        }
    }

    /// <summary>
    /// 将 ActivityTrace 实体映射到 TraceRecordDto
    /// </summary>
    private static TraceRecordDto MapToDto(ActivityTrace entity)
    {
        // 提取 app.input.parameters 从 TagsJson
        string? inputParameters = null;
        if (!string.IsNullOrEmpty(entity.TagsJson))
        {
            try
            {
                var tagsDict = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.TagsJson);
                if (tagsDict != null && tagsDict.TryGetValue("app.input.parameters", out var parameters))
                {
                    inputParameters = parameters;
                }
            }
            catch (JsonException)
            {
                // 如果 JSON 解析失败，保持为 null
            }
        }

        return new TraceRecordDto
        {
            Name = entity.Name ?? string.Empty,
            TraceId = entity.TraceId,
            ConversationId = entity.ConversationId ?? string.Empty,
            StartTime = entity.StartTime.ToLocalTime(), // 转换为本地时间用于显示
            EndTime = entity.EndTime?.ToLocalTime() ?? entity.StartTime.ToLocalTime(),
            DurationMs = entity.DurationMs,
            Status = entity.Status,
            OutputContent = entity.OutputContent,
            InputMessagesJson = entity.InputMessagesJson,
            InputParameters = inputParameters
        };
    }
}
