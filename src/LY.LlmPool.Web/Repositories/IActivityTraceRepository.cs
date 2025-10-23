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
    /// 分页查询会话概要信息。
    /// </summary>
    /// <param name="skip">跳过的会话数量。</param>
    /// <param name="take">获取的会话数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话列表及总数。</returns>
    Task<(IReadOnlyList<ConversationInfo> Items, int TotalCount)> GetConversationsAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据会话 ID 集合获取对应的完整 Trace 树。
    /// </summary>
    /// <param name="conversationIds">会话 ID 集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整的 Trace 树列表。</returns>
    Task<List<TraceNode>> GetTracesByConversationIdsAsync(
        IReadOnlyCollection<string> conversationIds,
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
