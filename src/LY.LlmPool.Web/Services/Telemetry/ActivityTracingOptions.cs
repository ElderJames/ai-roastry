namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// Activity 追踪配置选项
/// </summary>
public class ActivityTracingOptions
{
    /// <summary>
    /// 持久化配置
    /// </summary>
    public PersistenceOptions Persistence { get; set; } = new();
      
    /// <summary>
    /// 分页配置
    /// </summary>
    public PaginationOptions Pagination { get; set; } = new();
}

/// <summary>
/// 持久化配置
/// </summary>
public class PersistenceOptions
{
    /// <summary>
    /// 批量保存的批次大小
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// 批次延迟（毫秒）
    /// </summary>
    public int BatchDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// 最大重试次数
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Channel 容量（防止内存溢出）
    /// </summary>
    public int ChannelCapacity { get; set; } = 10000;
}

 
/// <summary>
/// 分页配置
/// </summary>
public class PaginationOptions
{
    /// <summary>
    /// 已完成调用每页显示数量
    /// </summary>
    public int CompletedTracesPageSize { get; set; } = 20;

    /// <summary>
    /// 会话列表每页显示数量
    /// </summary>
    public int ConversationsPageSize { get; set; } = 20;
}
