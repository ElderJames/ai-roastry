using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LY.LlmPool.Web.Data.Entities;

/// <summary>
/// 时间线节点类型
/// </summary>
public enum TimelineNodeType
{
    /// <summary>开始节点 - 记录初始信息</summary>
    Start = 0,

    /// <summary>文本块节点 - 记录合并的文本内容</summary>
    TextChunk = 1,

    /// <summary>工具调用节点</summary>
    ToolCall = 2,

    /// <summary>工具结果节点</summary>
    ToolResult = 3,

    /// <summary>完成节点 - 记录执行摘要</summary>
    Complete = 4
}

/// <summary>
/// 聊天执行时间线节点（子表）
/// </summary>
[Table("chat_execution_timeline_nodes")]
public class ChatExecutionTimelineNode
{
    /// <summary>主键</summary>
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>执行记录ID（外键）</summary>
    [Column("execution_record_id")]
    [Required]
    [MaxLength(100)]
    public string ExecutionRecordId { get; set; } = string.Empty;

    /// <summary>节点类型</summary>
    [Column("node_type")]
    public TimelineNodeType NodeType { get; set; }

    /// <summary>节点序号（按时间顺序）</summary>
    [Column("sequence")]
    public int Sequence { get; set; }

    /// <summary>时间戳</summary>
    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>相对于开始的耗时（毫秒）</summary>
    [Column("elapsed_ms")]
    public double ElapsedMs { get; set; }

    /// <summary>相对于上一节点的耗时（毫秒）</summary>
    [Column("delta_ms")]
    public double DeltaMs { get; set; }

    /// <summary>节点数据（JSON）- 存储详细信息</summary>
    /// <remarks>
    /// Start: { "messages": [...], "model": "...", "tools": [...], "options": {...} }
    /// TextChunk: { "text": "...", "length": 123 }
    /// ToolCall: { "name": "...", "callId": "...", "arguments": {...} }
    /// ToolResult: { "callId": "...", "result": "...", "isSuccess": true, "error": "..." }
    /// Complete: { "summary": "...", "phaseEvents": [...], "toolCalls": [...] }
    /// </remarks>
    [Column("data", TypeName = "jsonb")]
    public string Data { get; set; } = "{}";

    /// <summary>创建时间</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>关联的执行记录</summary>
    [ForeignKey(nameof(ExecutionRecordId))]
    public virtual ChatExecutionRecord? ExecutionRecord { get; set; }
}
