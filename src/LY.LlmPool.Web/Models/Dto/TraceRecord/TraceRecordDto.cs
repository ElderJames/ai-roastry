namespace LY.LlmPool.Web.Models.Dto;

/// <summary>
/// 追踪记录数据传输对象（用于表格展示）
/// </summary>
public class TraceRecordDto
{
    /// <summary>
    /// 名称（App 名称或 Tool 名称）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 追踪 ID
    /// </summary>
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// 会话 ID
    /// </summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// 开始时间（本地时间）
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 结束时间（本地时间）
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// 持续时间（毫秒）
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// 状态
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 输出内容
    /// </summary>
    public string? OutputContent { get; set; }

    /// <summary>
    /// 输入消息（JSON 格式）
    /// </summary>
    public string? InputMessagesJson { get; set; }

    /// <summary>
    /// 输入参数（从 TagsJson 的 app.input.parameters 提取）
    /// </summary>
    public string? InputParameters { get; set; }
}
