using LY.LlmPool.Web.Models.Dto;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// 追踪记录服务接口
/// </summary>
public interface ITraceRecordService
{
    /// <summary>
    /// 分页查询追踪记录
    /// </summary>
    /// <param name="query">查询参数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>分页结果</returns>
    Task<PagedResult<TraceRecordDto>> GetPagedRecordsAsync(
        TraceRecordQueryDto query,
        CancellationToken cancellationToken = default);
}
