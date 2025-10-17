using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LY.LlmPool.Web.Data.Entities;

/// <summary>
/// 聊天执行记录主表
/// </summary>
[Table("chat_execution_records")]
public class ChatExecutionRecord
{
    /// <summary>主键</summary>
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>请求ID（唯一标识）</summary>
    [Column("request_id")]
    [Required]
    [MaxLength(100)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>请求的模型标识（来自 API 请求的 model 字段，可能是 app 名称、endpoint 名称等）</summary>
    [Column("request_model")]
    [MaxLength(200)]
    public string? RequestModel { get; set; }

    /// <summary>实际执行的模型名称（底层 LLM 模型，如 gpt-4、deepseek-chat）</summary>
    [Column("model_name")]
    [MaxLength(200)]
    public string? ModelName { get; set; }

    /// <summary>开始时间</summary>
    [Column("start_time")]
    public DateTime StartTime { get; set; }

    /// <summary>结束时间</summary>
    [Column("end_time")]
    public DateTime? EndTime { get; set; }

    /// <summary>总执行时长（毫秒）</summary>
    [Column("total_duration_ms")]
    public double? TotalDurationMs { get; set; }

    /// <summary>首字节返回时间（毫秒）</summary>
    [Column("ttfb_ms")]
    public double? TtfbMs { get; set; }

    /// <summary>消息数量</summary>
    [Column("message_count")]
    public int MessageCount { get; set; }

    /// <summary>工具数量</summary>
    [Column("tool_count")]
    public int ToolCount { get; set; }

    /// <summary>工具调用次数</summary>
    [Column("tool_call_count")]
    public int ToolCallCount { get; set; }

    /// <summary>文本块数量</summary>
    [Column("chunk_count")]
    public int ChunkCount { get; set; }

    /// <summary>总字符数</summary>
    [Column("total_characters")]
    public int TotalCharacters { get; set; }

    /// <summary>是否成功</summary>
    [Column("is_successful")]
    public bool IsSuccessful { get; set; }

    /// <summary>错误信息</summary>
    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>执行摘要（JSON）</summary>
    [Column("summary", TypeName = "jsonb")]
    public string? Summary { get; set; }

    /// <summary>创建时间</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>时间线节点集合</summary>
    public virtual ICollection<ChatExecutionTimelineNode> TimelineNodes { get; set; } = new List<ChatExecutionTimelineNode>();
}
