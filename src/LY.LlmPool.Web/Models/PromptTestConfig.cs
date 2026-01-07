using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Models
{
    public class PromptTestConfig
    {
        public string Type { get; set; } = "Model";
        public string ConfigId { get; set; } = string.Empty;
        public string? Parameters { get; set; }
        public PromptTestResult? TestResult { get; set; }
        public bool IsStreaming { get; set; }
        // 是否折叠历史记录（默认展开）
        public bool IsHistoryCollapsed { get; set; } = false;
        public List<Microsoft.Extensions.AI.ChatMessage> ChatHistory { get; set; } = new();
        
        /// <summary>
        /// 🎯 此测试配置的 ConversationId（独立维护）
        /// </summary>
        public string? ConversationId { get; set; }

        public static PromptTestConfig FromTestConfigRecord(TestConfigRecord record)
        {
            var testResult = new PromptTestResult
            {
                Success = record.Success,
                Error = record.Error
            };
            
            // 如果有响应文本，添加为文本片段
            if (!string.IsNullOrEmpty(record.Response))
            {
                testResult.Segments.Add(new ResponseSegment
                {
                    Type = ResponseSegmentType.Text,
                    Text = record.Response
                });
            }
            
            return new PromptTestConfig
            {
                Type = record.Type,
                ConfigId = record.ConfigId ?? string.Empty,
                Parameters = record.Parameters,
                TestResult = testResult,
                IsStreaming = false,
                ChatHistory = new List<Microsoft.Extensions.AI.ChatMessage>()
            };
        }
    }

    public class PromptTestResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        
        /// <summary>
        /// 按时间顺序的响应片段（文本和工具调用交织）
        /// 这是主要的数据存储，包含完整的响应内容
        /// </summary>
        public List<ResponseSegment> Segments { get; set; } = new();
        // 评分结果（可选）
        public int? Score { get; set; }
        public string? ScoreComment { get; set; }
    }

    /// <summary>
    /// 响应片段：可以是文本或工具调用
    /// </summary>
    public class ResponseSegment
    {
        public ResponseSegmentType Type { get; set; }
        public string? Text { get; set; }
        public List<ToolCallRecord>? ToolCalls { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// 工具调用批次ID - 用于区分并行调用和串行调用
        /// 同一批次ID的工具调用应该在界面上并排显示（网格布局）
        /// 不同批次ID的工具调用应该垂直排列（独立块）
        /// </summary>
        public string? CallBatchId { get; set; }
    }

    /// <summary>
    /// 响应片段类型
    /// </summary>
    public enum ResponseSegmentType
    {
        Text,
        ToolCalls
    }

    /// <summary>
    /// 工具调用记录
    /// </summary>
    public class ToolCallRecord
    {
        public string CallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string ToolType { get; set; } = string.Empty; // "App" or "MCP"
        public string Arguments { get; set; } = string.Empty;
        public string? Result { get; set; }
        public bool Success { get; set; } = true;
        public string? Error { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
        public long DurationMs => EndTime.HasValue 
            ? (long)(EndTime.Value - StartTime).TotalMilliseconds 
            : 0;
        
        /// <summary>
        /// 从 ToolCallInfo 转换为 ToolCallRecord
        /// </summary>
        public static ToolCallRecord FromToolCallInfo(ToolCallInfo info)
        {
            return new ToolCallRecord
            {
                CallId = info.CallId,
                ToolName = info.ToolName,
                ToolType = info.ToolType,
                Arguments = info.Arguments,
                Result = info.Result,
                Success = info.IsSuccess,
                Error = info.ErrorMessage,
                StartTime = info.StartTime,
                EndTime = info.EndTime
            };
        }
    }
} 

