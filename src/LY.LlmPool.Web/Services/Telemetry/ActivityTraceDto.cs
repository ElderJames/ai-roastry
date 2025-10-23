namespace LY.LlmPool.Web.Services.Telemetry
{

    /// <summary>
    /// 追踪节点
    /// </summary>
    public class TraceNode
    {
        public string ActivityId { get; set; } = string.Empty;
        public string TraceId { get; set; } = string.Empty;
        public string SpanId { get; set; } = string.Empty;
        public string ParentSpanId { get; set; } = string.Empty;
        public string OperationName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? StatusDescription { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new();

        // AI 相关属性
        public string? OperationType { get; set; }
        public string? ModelId { get; set; }
        public string? ResponseModelId { get; set; }
        public string? ProviderName { get; set; }
        public string? ConversationId { get; set; }
        public string? ResponseId { get; set; }
        public string? FinishReason { get; set; }
        public float? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public string? ServerAddress { get; set; }
        public int? ServerPort { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorStackTrace { get; set; }

        // App 相关属性
        public bool IsAppCall { get; set; }

        // Tool 相关属性
        public bool IsToolCall { get; set; }

        /// <summary>
        /// 名称（App 名称或 Tool 名称的统一字段）
        /// </summary>
        public string? Name { get; set; }

        public string? ToolArguments { get; set; }
        public string? ToolResult { get; set; }

        // 工具类型（使用枚举）
        public LY.LlmPool.Web.Data.Entities.ActivityToolType ToolType { get; set; } = LY.LlmPool.Web.Data.Entities.ActivityToolType.None;

        // Server 类型（使用枚举）
        public LY.LlmPool.Web.Data.Entities.ActivityServerType ServerType { get; set; } = LY.LlmPool.Web.Data.Entities.ActivityServerType.None;

        public string? McpServerName { get; set; } // MCP Server 的配置名称

        // 🎯 兼容属性：保留用于向后兼容和简化判断逻辑
        public bool IsAppTool => ToolType == LY.LlmPool.Web.Data.Entities.ActivityToolType.AppTool;
        public bool IsMcpTool => ToolType == LY.LlmPool.Web.Data.Entities.ActivityToolType.McpTool;
        public bool IsLlmPoolServer => ServerType == LY.LlmPool.Web.Data.Entities.ActivityServerType.LlmPoolServer;
        public bool IsMcpServer => ServerType == LY.LlmPool.Web.Data.Entities.ActivityServerType.McpServer;

        // 🎯 向后兼容：AppName 和 ToolName 作为计算属性
        public string? AppName => IsAppCall ? Name : null;
        public string? ToolName => IsToolCall ? Name : null;

        // Chat 消息内容（从 Events 中提取）
        public List<TraceChatMessage> InputMessages { get; set; } = new();
        public string? OutputContent { get; set; }

        // Activity Events (用于展示工具调用、结果等事件)
        public List<ActivityEventInfo> Events { get; set; } = new();

        // 树形结构
        public List<TraceNode> Children { get; set; } = new();
    }

    /// <summary>
    /// Activity Event 信息 (用于 UI 展示)
    /// </summary>
    public class ActivityEventInfo
    {
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new();
    }

    /// <summary>
    /// Chat 消息 (从 Activity Events 中提取)
    /// </summary>
    public class TraceChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
    }

    /// <summary>
    /// Conversation 信息
    /// </summary>
    public class ConversationInfo
    {
        public string ConversationId { get; set; } = string.Empty;
        public int RequestCount { get; set; }
        public DateTime FirstRequestTime { get; set; }
        public DateTime LastRequestTime { get; set; }
        public int TotalInputTokens { get; set; }
        public int TotalOutputTokens { get; set; }
        public bool IsActive { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public List<string> Models { get; set; } = new();
    }

    /// <summary>
    /// 追踪统计信息
    /// </summary>
    public class TraceStatistics
    {
        public int TotalTraces { get; set; }
        public int SuccessCount { get; set; }
        public int ErrorCount { get; set; }
        public double TotalDurationMs { get; set; }
        public double AverageDurationMs { get; set; }
        public int TotalInputTokens { get; set; }
        public int TotalOutputTokens { get; set; }
        public int AppCallCount { get; set; }
        public int ToolCallCount { get; set; }
        public List<string> UniqueModels { get; set; } = new();
        public List<string> UniqueTools { get; set; } = new();
    }
}
