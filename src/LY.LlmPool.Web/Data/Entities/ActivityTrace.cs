using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LY.LlmPool.Web.Data.Entities;

/// <summary>
/// Activity 追踪记录实体
/// </summary>
[Table("ActivityTraces")]
public class ActivityTrace
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Activity ID（唯一标识）
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string ActivityId { get; set; } = string.Empty;

    /// <summary>
    /// Trace ID（分布式追踪标识）
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// Span ID（当前 Activity 标识）
    /// </summary>
    [Required]
    [MaxLength(16)]
    public string SpanId { get; set; } = string.Empty;

    /// <summary>
    /// Parent Span ID（父 Activity 标识）
    /// </summary>
    [MaxLength(16)]
    public string ParentSpanId { get; set; } = string.Empty;

    /// <summary>
    /// Conversation ID（会话标识）
    /// </summary>
    [MaxLength(256)]
    public string? ConversationId { get; set; }

    /// <summary>
    /// 操作名称
    /// </summary>
    [Required]
    [MaxLength(256)]
    public string OperationName { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称
    /// </summary>
    [MaxLength(512)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 开始时间（UTC）
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// 结束时间（UTC）
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 持续时间（毫秒）
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Activity 类型（Server/Client/Internal等）
    /// </summary>
    [MaxLength(50)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// 状态（Success/Error/Completed等）
    /// </summary>
    [MaxLength(50)]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 状态描述
    /// </summary>
    public string? StatusDescription { get; set; }

    /// <summary>
    /// 错误类型
    /// </summary>
    [MaxLength(256)]
    public string? ErrorType { get; set; }

    /// <summary>
    /// 错误堆栈跟踪
    /// </summary>
    public string? ErrorStackTrace { get; set; }

    // ==================== AI 相关属性 ====================

    /// <summary>
    /// 操作类型（chat/execute_tool等）
    /// </summary>
    [MaxLength(50)]
    public string? OperationType { get; set; }

    /// <summary>
    /// 模型 ID
    /// </summary>
    [MaxLength(256)]
    public string? ModelId { get; set; }

    /// <summary>
    /// 响应模型 ID
    /// </summary>
    [MaxLength(256)]
    public string? ResponseModelId { get; set; }

    /// <summary>
    /// 提供商名称
    /// </summary>
    [MaxLength(100)]
    public string? ProviderName { get; set; }

    /// <summary>
    /// 响应 ID
    /// </summary>
    [MaxLength(256)]
    public string? ResponseId { get; set; }

    /// <summary>
    /// 结束原因（stop/length/tool_calls等）
    /// </summary>
    [MaxLength(100)]
    public string? FinishReason { get; set; }

    /// <summary>
    /// Temperature 参数
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// MaxTokens 参数
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// 输入 Token 数
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 输出 Token 数
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 服务器地址
    /// </summary>
    [MaxLength(256)]
    public string? ServerAddress { get; set; }

    /// <summary>
    /// 服务器端口
    /// </summary>
    public int? ServerPort { get; set; }

    // ==================== App 相关属性 ====================

    /// <summary>
    /// 是否为 App 调用
    /// </summary>
    public bool IsAppCall { get; set; }

    // ==================== Tool 相关属性 ====================

    /// <summary>
    /// 是否为工具调用
    /// </summary>
    public bool IsToolCall { get; set; }

    /// <summary>
    /// 名称（App 名称或 Tool 名称的统一字段）
    /// 当 IsAppCall=true 时存储 App 名称，当 IsToolCall=true 时存储 Tool 名称
    /// </summary>
    [MaxLength(256)]
    public string? Name { get; set; }

    /// <summary>
    /// 工具类型（None/AppTool/McpTool/Other）
    /// </summary>
    public ActivityToolType ToolType { get; set; } = ActivityToolType.None;

    /// <summary>
    /// 工具数据（参数和结果，JSON 格式）
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ToolDataJson { get; set; }

    // ==================== Server 相关属性 ====================

    /// <summary>
    /// 服务器类型（None/LlmPoolServer/McpServer）
    /// </summary>
    public ActivityServerType ServerType { get; set; } = ActivityServerType.None;

    /// <summary>
    /// MCP Server 名称
    /// </summary>
    [MaxLength(256)]
    public string? McpServerName { get; set; }

    // ==================== 消息内容 ====================

    /// <summary>
    /// 输入消息（JSON 格式）
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? InputMessagesJson { get; set; }

    /// <summary>
    /// 输出内容
    /// </summary>
    public string? OutputContent { get; set; }

    // ==================== Tags 和 Events ====================

    /// <summary>
    /// Tags（JSON 格式）
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? TagsJson { get; set; }

    /// <summary>
    /// Events（JSON 格式）
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? EventsJson { get; set; }

    // ==================== 元数据 ====================

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 服务器类型枚举
/// </summary>
public enum ActivityServerType
{
    /// <summary>
    /// 非服务器调用
    /// </summary>
    None = 0,

    /// <summary>
    /// LlmPool Server（llmpool.server）
    /// </summary>
    LlmPoolServer = 1,

    /// <summary>
    /// MCP Server（initialize, tools/list等）
    /// </summary>
    McpServer = 2
}


/// <summary>
/// 工具调用数据（参数和结果的合并表示）
/// </summary>
public class ActivityToolData
{
    /// <summary>
    /// 工具调用参数（JSON 格式）
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// 工具执行结果
    /// </summary>
    public string? Result { get; set; }
}

/// <summary>
/// 工具类型枚举
/// </summary>
public enum ActivityToolType
{
    /// <summary>
    /// 不是工具调用
    /// </summary>
    None = 0,

    /// <summary>
    /// App Tool（llmpool.tool 或 llmpool.app）
    /// </summary>
    AppTool = 1,

    /// <summary>
    /// MCP Tool（tools/call）
    /// </summary>
    McpTool = 2,

    /// <summary>
    /// 其他类型的工具（预留扩展）
    /// </summary>
    Other = 3
}
