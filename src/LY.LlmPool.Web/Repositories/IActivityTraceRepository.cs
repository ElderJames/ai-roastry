using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Repositories;

/// <summary>
/// Activity 追踪数据仓储接口
/// </summary>
public interface IActivityTraceRepository
{
    /// <summary>
    /// 批量保存 Activity 追踪记录
    /// </summary>
    /// <param name="nodes">追踪节点列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SaveBatchAsync(IEnumerable<TraceNode> nodes, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 删除指定时间之前的记录（预留用于数据清理）
    /// </summary>
    /// <param name="cutoff">截止时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>删除的记录数</returns>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页查询“已完成”的根调用（根节点）树列表。
    /// - 当提供 <paramref name="conversationId"/> 时，仅返回该会话的记录；否则返回全部会话。
    /// - 仅返回根节点及其完整子树（同一 TraceId 下的所有节点）。
    /// </summary>
    /// <param name="conversationId">会话ID（可空，空表示全部会话）</param>
    /// <param name="pageIndex">页码（从1开始）</param>
    /// <param name="pageSize">每页大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>返回（Items, Total）元组，其中 Items 为当前页的根节点树列表，Total 为总根节点数</returns>
    Task<(List<TraceNode> Items, int Total)> GetCompletedRootTreesAsync(
        string? conversationId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页查询历史会话列表（基于持久化数据）。
    /// </summary>
    /// <param name="pageIndex">页码（从1开始）</param>
    /// <param name="pageSize">每页大小</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>返回（Items, Total）元组，其中 Items 为当前页会话摘要，Total 为历史会话总数</returns>
    Task<(List<ConversationInfo> Items, int Total)> GetHistoricalConversationsAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计“已完成”的根调用数据（按根节点聚合）。
    /// </summary>
    /// <param name="conversationId">会话ID（可空，空表示全部会话）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>TraceStatistics（TotalTraces=根调用数，AverageDurationMs/Token 汇总等）</returns>
    Task<TraceStatistics> GetCompletedRootStatisticsAsync(
        string? conversationId,
        CancellationToken cancellationToken = default);
}
