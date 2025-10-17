using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services.Monitoring
{
    /// <summary>
    /// 聊天执行阶段
    /// </summary>
    public enum ExecutionPhase
    {
        /// <summary>请求开始</summary>
        RequestStart,
        /// <summary>首字节返回 (TTFB)</summary>
        FirstToken,
        /// <summary>工具调用开始</summary>
        ToolCallStart,
        /// <summary>工具执行完成</summary>
        ToolCallComplete,
        /// <summary>接收到文本块</summary>
        TextChunk,
        /// <summary>请求完成</summary>
        RequestComplete
    }

    /// <summary>
    /// 执行阶段事件
    /// </summary>
    public class ExecutionPhaseEvent
    {
        /// <summary>阶段类型</summary>
        public ExecutionPhase Phase { get; set; }

        /// <summary>时间戳</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>相对于请求开始的耗时（毫秒）</summary>
        public double ElapsedMs { get; set; }

        /// <summary>相对于上一阶段的耗时（毫秒）</summary>
        public double DeltaMs { get; set; }

        /// <summary>附加信息</summary>
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// 工具调用信息
    /// </summary>
    public class ToolCallInfo
    {
        /// <summary>工具名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>调用ID</summary>
        public string CallId { get; set; } = string.Empty;

        /// <summary>调用参数（JSON）</summary>
        public string Arguments { get; set; } = string.Empty;

        /// <summary>开始时间</summary>
        public DateTime StartTime { get; set; }

        /// <summary>完成时间</summary>
        public DateTime? CompleteTime { get; set; }

        /// <summary>执行时长（毫秒）</summary>
        public double? DurationMs => CompleteTime.HasValue
            ? (CompleteTime.Value - StartTime).TotalMilliseconds
            : null;

        /// <summary>是否成功</summary>
        public bool? IsSuccess { get; set; }

        /// <summary>结果内容</summary>
        public string? Result { get; set; }

        /// <summary>错误信息</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// 聊天执行监控器
    /// </summary>
    public class ChatExecutionMonitor
    {
        private readonly Stopwatch _stopwatch = new();
        private DateTime? _lastEventTime;
        private readonly object _lock = new();

        /// <summary>请求ID</summary>
        public string RequestId { get; }

        /// <summary>模型名称</summary>
        public string ModelName { get; set; } = string.Empty;

        /// <summary>执行记录ID（由 Controller 创建后传入）</summary>
        public string? ExecutionRecordId { get; set; }

        /// <summary>消息数量</summary>
        public int MessageCount { get; set; }

        /// <summary>工具数量</summary>
        public int ToolCount { get; set; }

        /// <summary>执行阶段事件列表</summary>
        public List<ExecutionPhaseEvent> PhaseEvents { get; } = new();

        /// <summary>工具调用列表</summary>
        public List<ToolCallInfo> ToolCalls { get; } = new();

        /// <summary>接收的文本块数量</summary>
        public int ChunkCount { get; private set; }

        /// <summary>接收的总字符数</summary>
        public int TotalCharacters { get; private set; }

        /// <summary>是否已完成</summary>
        public bool IsCompleted { get; private set; }

        public ChatExecutionMonitor(string requestId)
        {
            RequestId = requestId;
        }

        /// <summary>
        /// 开始监控
        /// </summary>
        public void Start(int messageCount, int toolCount)
        {
            lock (_lock)
            {
                MessageCount = messageCount;
                ToolCount = toolCount;
                _stopwatch.Start();
                _lastEventTime = DateTime.UtcNow;

                RecordPhase(ExecutionPhase.RequestStart, new Dictionary<string, object>
                {
                    ["MessageCount"] = messageCount,
                    ["ToolCount"] = toolCount
                });
            }
        }

        /// <summary>
        /// 记录首字节返回
        /// </summary>
        public void RecordFirstToken()
        {
            lock (_lock)
            {
                if (PhaseEvents.Any(e => e.Phase == ExecutionPhase.FirstToken))
                    return; // 已记录过

                RecordPhase(ExecutionPhase.FirstToken, new Dictionary<string, object>
                {
                    ["TTFB_Ms"] = _stopwatch.Elapsed.TotalMilliseconds
                });
            }
        }

        /// <summary>
        /// 记录工具调用
        /// </summary>
        public void RecordToolCall(string name, string callId, string arguments)
        {
            lock (_lock)
            {
                var toolCall = new ToolCallInfo
                {
                    Name = name,
                    CallId = callId,
                    Arguments = arguments,
                    StartTime = DateTime.UtcNow
                };

                ToolCalls.Add(toolCall);

                RecordPhase(ExecutionPhase.ToolCallStart, new Dictionary<string, object>
                {
                    ["ToolName"] = name,
                    ["CallId"] = callId,
                    ["Arguments"] = arguments,
                    ["ToolCallIndex"] = ToolCalls.Count
                });
            }
        }

        /// <summary>
        /// 记录工具执行结果
        /// </summary>
        public void RecordToolResult(string callId, bool isSuccess, string? result, string? error)
        {
            lock (_lock)
            {
                var toolCall = ToolCalls.FirstOrDefault(t => t.CallId == callId);
                if (toolCall != null)
                {
                    toolCall.CompleteTime = DateTime.UtcNow;
                    toolCall.IsSuccess = isSuccess;
                    toolCall.Result = result;
                    toolCall.Error = error;
                }

                RecordPhase(ExecutionPhase.ToolCallComplete, new Dictionary<string, object>
                {
                    ["CallId"] = callId,
                    ["IsSuccess"] = isSuccess,
                    ["ResultLength"] = result?.Length ?? 0,
                    ["DurationMs"] = toolCall?.DurationMs ?? 0
                });
            }
        }

        /// <summary>
        /// 记录文本块
        /// </summary>
        public void RecordTextChunk(string text)
        {
            lock (_lock)
            {
                ChunkCount++;
                TotalCharacters += text.Length;

                // 不记录每个文本块的详细事件，避免日志过多
                // 只在特定间隔记录
                if (ChunkCount % 10 == 1) // 每10个块记录一次
                {
                    RecordPhase(ExecutionPhase.TextChunk, new Dictionary<string, object>
                    {
                        ["ChunkCount"] = ChunkCount,
                        ["TotalCharacters"] = TotalCharacters
                    });
                }
            }
        }

        /// <summary>
        /// 完成监控
        /// </summary>
        public void Complete()
        {
            lock (_lock)
            {
                if (IsCompleted)
                    return;

                IsCompleted = true;
                _stopwatch.Stop();

                RecordPhase(ExecutionPhase.RequestComplete, new Dictionary<string, object>
                {
                    ["TotalDurationMs"] = _stopwatch.Elapsed.TotalMilliseconds,
                    ["ChunkCount"] = ChunkCount,
                    ["TotalCharacters"] = TotalCharacters,
                    ["ToolCallCount"] = ToolCalls.Count
                });
            }
        }

        /// <summary>
        /// 记录阶段事件
        /// </summary>
        private void RecordPhase(ExecutionPhase phase, Dictionary<string, object>? metadata = null)
        {
            var now = DateTime.UtcNow;
            var elapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
            var deltaMs = _lastEventTime.HasValue
                ? (now - _lastEventTime.Value).TotalMilliseconds
                : 0;

            PhaseEvents.Add(new ExecutionPhaseEvent
            {
                Phase = phase,
                Timestamp = now,
                ElapsedMs = elapsedMs,
                DeltaMs = deltaMs,
                Metadata = metadata
            });

            _lastEventTime = now;
        }

        /// <summary>
        /// 获取执行摘要
        /// </summary>
        public string GetSummary()
        {
            lock (_lock)
            {
                var ttfb = PhaseEvents.FirstOrDefault(e => e.Phase == ExecutionPhase.FirstToken)?.ElapsedMs ?? 0;
                var totalTime = _stopwatch.Elapsed.TotalMilliseconds;
                var avgToolTime = ToolCalls.Any()
                    ? ToolCalls.Where(t => t.DurationMs.HasValue).Average(t => t.DurationMs!.Value)
                    : 0;

                return $"RequestId={RequestId}, Model={ModelName}, " +
                       $"TotalTime={totalTime:F2}ms, TTFB={ttfb:F2}ms, " +
                       $"Messages={MessageCount}, Tools={ToolCount}, " +
                       $"ToolCalls={ToolCalls.Count}, AvgToolTime={avgToolTime:F2}ms, " +
                       $"Chunks={ChunkCount}, Characters={TotalCharacters}";
            }
        }
    }
}
