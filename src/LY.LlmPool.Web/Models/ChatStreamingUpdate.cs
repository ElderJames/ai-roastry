using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace LY.LlmPool.Web.Models;

/// <summary>
/// 聊天流式更新,包含文本内容和工具调用信息
/// </summary>
public class ChatStreamingUpdate
{
    /// <summary>
    /// 文本内容(如果有)
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// 工具调用列表(如果有)
    /// </summary>
    public List<ToolCallInfo>? ToolCalls { get; set; }

    /// <summary>
    /// 是否是工具调用更新
    /// </summary>
    public bool IsToolCall => ToolCalls != null && ToolCalls.Any();

    /// <summary>
    /// 完成原因(如果有)
    /// </summary>
    public string? FinishReason { get; set; }
}

/// <summary>
/// 工具调用信息
/// </summary>
public class ToolCallInfo
{
    /// <summary>
    /// 工具调用 ID
    /// </summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>
    /// 工具名称
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// 工具类型 (App/MCP)
    /// </summary>
    public string ToolType { get; set; } = string.Empty;

    /// <summary>
    /// 输入参数 (JSON 字符串)
    /// </summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// 输出结果 (JSON 字符串或文本)
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// 调用开始时间
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 调用结束时间
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 调用时长(毫秒)
    /// </summary>
    public long DurationMs => EndTime.HasValue 
        ? (long)(EndTime.Value - StartTime).TotalMilliseconds 
        : 0;

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess { get; set; } = true;

    /// <summary>
    /// 错误信息(如果失败)
    /// </summary>
    public string? ErrorMessage { get; set; }
}
