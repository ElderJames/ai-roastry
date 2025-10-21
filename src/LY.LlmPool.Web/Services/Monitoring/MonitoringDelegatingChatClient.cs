using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services.Monitoring
{
    /// <summary>
    /// 监控委托聊天客户端，拦截并记录执行信息
    /// </summary>
    public class MonitoringDelegatingChatClient : DelegatingChatClient
    {
        private readonly ILogger<MonitoringDelegatingChatClient> _logger;
        private readonly ChatExecutionMonitor _monitor;
        private readonly ChatExecutionPersistenceService? _persistenceService;

        // 文本块缓冲区（用于合并连续的文本）
        private readonly StringBuilder _textBuffer = new();
        private double _textBufferStartElapsedMs;
        private ChatExecutionRecord? _executionRecord;

        public MonitoringDelegatingChatClient(
            IChatClient innerClient,
            ChatExecutionMonitor monitor,
            ILogger<MonitoringDelegatingChatClient> logger,
            ChatExecutionPersistenceService? persistenceService = null)
            : base(innerClient)
        {
            _monitor = monitor;
            _logger = logger;
            _persistenceService = persistenceService;
        }

        /// <summary>
        /// 拦截流式响应调用
        /// </summary>
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            var toolCount = options?.Tools?.Count ?? 0;

            // 记录请求开始
            _monitor.Start(messageList.Count, toolCount);

            _logger.LogInformation("[{RequestId}] 开始流式请求 - 模型: {Model}, 消息数: {MessageCount}, 工具数: {ToolCount}",
                _monitor.RequestId, _monitor.ModelName, messageList.Count, toolCount);

            // 获取或创建执行记录并保存 Start 节点
            if (_persistenceService != null)
            {
                try
                {
                    // 如果 Controller 已经创建了执行记录，则使用该记录
                    if (!string.IsNullOrEmpty(_monitor.ExecutionRecordId))
                    {
                        _executionRecord = await _persistenceService.GetExecutionRecordByIdAsync(_monitor.ExecutionRecordId);

                        if (_executionRecord != null)
                        {
                            // 添加 Start 节点（包含完整信息）
                            await _persistenceService.AddStartNodeAsync(
                                _executionRecord.Id,
                                _monitor,
                                messageList,
                                _monitor.ModelName,
                                options?.Tools?.ToList(),
                                options);
                        }
                    }
                    else
                    {
                        // 降级方案：如果没有预创建记录，则创建新记录（向后兼容）
                        _executionRecord = await _persistenceService.CreateExecutionRecordAsync(
                            _monitor.RequestId,
                            _monitor.ModelName,
                            messageList.Count,
                            DateTime.UtcNow);

                        if (_executionRecord != null)
                        {
                            await _persistenceService.AddStartNodeAsync(
                                _executionRecord.Id,
                                _monitor,
                                messageList,
                                _monitor.ModelName,
                                options?.Tools?.ToList(),
                                options);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{RequestId}] 创建/获取执行记录失败", _monitor.RequestId);
                }
            }

            // 记录详细的消息内容（Debug级别）
            for (int i = 0; i < messageList.Count; i++)
            {
                var msg = messageList[i];
                var contentPreview = msg.Text?.Length > 100 ? msg.Text[..100] + "..." : msg.Text;
                _logger.LogDebug("[{RequestId}] 消息 [{Index}] - 角色: {Role}, 内容: {Content}",
                    _monitor.RequestId, i, msg.Role.Value, contentPreview);
            }

            // 记录工具信息（Debug级别）
            if (options?.Tools != null)
            {
                for (int i = 0; i < options.Tools.Count; i++)
                {
                    var tool = options.Tools[i];
                    _logger.LogDebug("[{RequestId}] 工具 [{Index}]: {ToolName}",
                        _monitor.RequestId, i, tool?.Name ?? "Unknown");
                }
            }

            var firstChunk = true;

            // 调用底层客户端并拦截响应
            await foreach (var update in base.GetStreamingResponseAsync(messageList, options, cancellationToken))
            {
                // 记录首字节返回时间
                if (firstChunk)
                {
                    firstChunk = false;
                    _monitor.RecordFirstToken();
                    _logger.LogInformation("[{RequestId}] 收到首个响应块 (TTFB)",
                        _monitor.RequestId);
                }

                // 处理文本内容
                if (!string.IsNullOrEmpty(update.Text))
                {
                    _monitor.RecordTextChunk(update.Text);

                    // 缓冲文本用于持久化（合并连续的文本块）
                    BufferText(update.Text);

                    // 记录文本块（仅在 Trace 级别）
                    _logger.LogTrace("[{RequestId}] 文本块: {Text}",
                        _monitor.RequestId, update.Text);
                }

                // 先 yield return update (立即返回给调用者,不阻塞流式输出)
                yield return update;

                // 然后处理持久化操作 (在后台异步执行,不阻塞下一个 update)
                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        // 处理工具调用
                        if (content is FunctionCallContent functionCall)
                        {
                            // 先刷新文本缓冲区（保存之前累积的文本）
                            await FlushTextBufferAsync();

                            var callId = functionCall.CallId ?? $"call_{Guid.NewGuid():N}";
                            var argumentsJson = JsonSerializer.Serialize(
                                functionCall.Arguments ?? new Dictionary<string, object?>(),
                                new JsonSerializerOptions { WriteIndented = false });

                            _monitor.RecordToolCall(functionCall.Name, callId, argumentsJson);

                            // 保存工具调用节点
                            if (_persistenceService != null && _executionRecord != null)
                            {
                                try
                                {
                                    var phaseEvent = _monitor.PhaseEvents.LastOrDefault();
                                    await _persistenceService.AddToolCallNodeAsync(
                                        _executionRecord.Id,
                                        _monitor.RequestId,
                                        functionCall.Name,
                                        callId,
                                        argumentsJson,
                                        phaseEvent?.ElapsedMs ?? 0);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[{RequestId}] 保存工具调用节点失败", _monitor.RequestId);
                                }
                            }

                            _logger.LogInformation(
                                "[{RequestId}] 工具调用: {ToolName}, CallId: {CallId}, 参数: {Arguments}",
                                _monitor.RequestId, functionCall.Name, callId, argumentsJson);
                        }
                        // 处理工具结果
                        else if (content is FunctionResultContent functionResult)
                        {
                            var callId = functionResult.CallId ?? string.Empty;
                            var isSuccess = functionResult.Exception == null;
                            var result = functionResult.Result?.ToString();
                            var error = functionResult.Exception?.Message;

                            _monitor.RecordToolResult(callId, isSuccess, result, error);

                            // 保存工具结果节点
                            if (_persistenceService != null && _executionRecord != null)
                            {
                                try
                                {
                                    var phaseEvent = _monitor.PhaseEvents.LastOrDefault();
                                    await _persistenceService.AddToolResultNodeAsync(
                                        _executionRecord.Id,
                                        _monitor.RequestId,
                                        callId,
                                        isSuccess,
                                        result,
                                        error,
                                        phaseEvent?.ElapsedMs ?? 0);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[{RequestId}] 保存工具结果节点失败", _monitor.RequestId);
                                }
                            }

                            if (isSuccess)
                            {
                                var resultPreview = result?.Length > 200 ? result[..200] + "..." : result;
                                _logger.LogInformation(
                                    "[{RequestId}] 工具结果: CallId: {CallId}, 结果长度: {Length}, 预览: {Preview}",
                                    _monitor.RequestId, callId, result?.Length ?? 0, resultPreview);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "[{RequestId}] 工具执行失败: CallId: {CallId}, 错误: {Error}",
                                    _monitor.RequestId, callId, error);
                            }
                        }
                    }
                }
            }

            // 刷新最后的文本缓冲区
            await FlushTextBufferAsync();

            // 完成监控
            _monitor.Complete();

            // 保存 Complete 节点
            if (_persistenceService != null && _executionRecord != null)
            {
                try
                {
                    await _persistenceService.CompleteExecutionRecordAsync(
                        _executionRecord.Id,
                        _monitor,
                        isSuccessful: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{RequestId}] 保存完成节点失败", _monitor.RequestId);
                }
            }

            // 记录执行摘要
            _logger.LogInformation("[{RequestId}] 流式请求完成 - {Summary}",
                _monitor.RequestId, _monitor.GetSummary());

            // 记录详细的阶段信息（Debug级别）
            _logger.LogDebug("[{RequestId}] 执行阶段详情:", _monitor.RequestId);
            foreach (var phase in _monitor.PhaseEvents)
            {
                var metadataJson = phase.Metadata != null
                    ? JsonSerializer.Serialize(phase.Metadata)
                    : "{}";
                _logger.LogDebug(
                    "[{RequestId}]   {Phase}: ElapsedMs={ElapsedMs:F2}, DeltaMs={DeltaMs:F2}, Metadata={Metadata}",
                    _monitor.RequestId, phase.Phase, phase.ElapsedMs, phase.DeltaMs, metadataJson);
            }

            // 记录工具调用详情（Debug级别）
            if (_monitor.ToolCalls.Any())
            {
                _logger.LogDebug("[{RequestId}] 工具调用详情:", _monitor.RequestId);
                foreach (var toolCall in _monitor.ToolCalls)
                {
                    _logger.LogDebug(
                        "[{RequestId}]   {ToolName}: CallId={CallId}, DurationMs={Duration:F2}, Success={Success}",
                        _monitor.RequestId, toolCall.Name, toolCall.CallId, toolCall.DurationMs ?? 0, toolCall.IsSuccess);
                }
            }
        }

        /// <summary>
        /// 拦截非流式响应调用
        /// </summary>
        public override async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            var toolCount = options?.Tools?.Count ?? 0;

            // 记录请求开始
            _monitor.Start(messageList.Count, toolCount);

            _logger.LogInformation("[{RequestId}] 开始非流式请求 - 模型: {Model}, 消息数: {MessageCount}, 工具数: {ToolCount}",
                _monitor.RequestId, _monitor.ModelName, messageList.Count, toolCount);

            // 获取或创建执行记录并保存 Start 节点
            if (_persistenceService != null)
            {
                try
                {
                    // 如果 Controller 已经创建了执行记录，则使用该记录
                    if (!string.IsNullOrEmpty(_monitor.ExecutionRecordId))
                    {
                        _executionRecord = await _persistenceService.GetExecutionRecordByIdAsync(_monitor.ExecutionRecordId);

                        if (_executionRecord != null)
                        {
                            // 添加 Start 节点（包含完整信息）
                            await _persistenceService.AddStartNodeAsync(
                                _executionRecord.Id,
                                _monitor,
                                messageList,
                                _monitor.ModelName,
                                options?.Tools?.ToList(),
                                options);
                        }
                    }
                    else
                    {
                        // 降级方案：如果没有预创建记录，则创建新记录（向后兼容）
                        _executionRecord = await _persistenceService.CreateExecutionRecordAsync(
                            _monitor.RequestId,
                            _monitor.ModelName,
                            messageList.Count,
                            DateTime.UtcNow);

                        if (_executionRecord != null)
                        {
                            await _persistenceService.AddStartNodeAsync(
                                _executionRecord.Id,
                                _monitor,
                                messageList,
                                _monitor.ModelName,
                                options?.Tools?.ToList(),
                                options);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{RequestId}] 创建/获取执行记录失败", _monitor.RequestId);
                }
            }

            // 记录详细的消息内容（Debug级别）
            for (int i = 0; i < messageList.Count; i++)
            {
                var msg = messageList[i];
                var contentPreview = msg.Text?.Length > 100 ? msg.Text[..100] + "..." : msg.Text;
                _logger.LogDebug("[{RequestId}] 消息 [{Index}] - 角色: {Role}, 内容: {Content}",
                    _monitor.RequestId, i, msg.Role.Value, contentPreview);
            }

            // 记录工具信息（Debug级别）
            if (options?.Tools != null)
            {
                for (int i = 0; i < options.Tools.Count; i++)
                {
                    var tool = options.Tools[i];
                    _logger.LogDebug("[{RequestId}] 工具 [{Index}]: {ToolName}",
                        _monitor.RequestId, i, tool.Name ?? "Unknown");
                }
            }

            // 调用底层客户端
            var response = await base.GetResponseAsync(messageList, options, cancellationToken);

            // 记录首次响应
            _monitor.RecordFirstToken();

            // 处理响应内容
            // 注意：ChatResponse.Messages 是消息列表，包含完整的对话历史
            // 最后一条消息通常是AI的响应
            if (response?.Messages != null && response.Messages.Count > 0)
            {
                // 遍历所有消息，记录文本内容和工具调用
                foreach (var message in response.Messages)
                {
                    // 记录文本内容
                    if (!string.IsNullOrEmpty(message.Text))
                    {
                        _monitor.RecordTextChunk(message.Text);

                        // 缓冲文本用于持久化
                        BufferText(message.Text);

                        _logger.LogDebug("[{RequestId}] 消息文本长度: {Length}, 角色: {Role}",
                            _monitor.RequestId, message.Text.Length, message.Role.Value);
                    }

                    // 处理消息中的工具调用和结果
                    if (message.Contents != null && message.Contents.Count > 0)
                    {
                        foreach (var content in message.Contents)
                        {
                            // 处理工具调用
                            if (content is FunctionCallContent functionCall)
                            {
                                var callId = functionCall.CallId ?? $"call_{Guid.NewGuid():N}";
                                var argumentsJson = JsonSerializer.Serialize(
                                    functionCall.Arguments ?? new Dictionary<string, object?>(),
                                    new JsonSerializerOptions { WriteIndented = false });

                                _monitor.RecordToolCall(functionCall.Name, callId, argumentsJson);

                                // 保存工具调用节点
                                if (_persistenceService != null && _executionRecord != null)
                                {
                                    try
                                    {
                                        var phaseEvent = _monitor.PhaseEvents.LastOrDefault();
                                        await _persistenceService.AddToolCallNodeAsync(
                                            _executionRecord.Id,
                                            _monitor.RequestId,
                                            functionCall.Name,
                                            callId,
                                            argumentsJson,
                                            phaseEvent?.ElapsedMs ?? 0);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[{RequestId}] 保存工具调用节点失败", _monitor.RequestId);
                                    }
                                }

                                _logger.LogInformation(
                                    "[{RequestId}] 工具调用: {ToolName}, CallId: {CallId}, 参数: {Arguments}",
                                    _monitor.RequestId, functionCall.Name, callId, argumentsJson);
                            }
                            // 处理工具结果
                            else if (content is FunctionResultContent functionResult)
                            {
                                var callId = functionResult.CallId ?? string.Empty;
                                var isSuccess = functionResult.Exception == null;
                                var result = functionResult.Result?.ToString();
                                var error = functionResult.Exception?.Message;

                                _monitor.RecordToolResult(callId, isSuccess, result, error);

                                // 保存工具结果节点
                                if (_persistenceService != null && _executionRecord != null)
                                {
                                    try
                                    {
                                        var phaseEvent = _monitor.PhaseEvents.LastOrDefault();
                                        await _persistenceService.AddToolResultNodeAsync(
                                            _executionRecord.Id,
                                            _monitor.RequestId,
                                            callId,
                                            isSuccess,
                                            result,
                                            error,
                                            phaseEvent?.ElapsedMs ?? 0);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[{RequestId}] 保存工具结果节点失败", _monitor.RequestId);
                                    }
                                }

                                _logger.LogInformation(
                                    "[{RequestId}] 工具结果: CallId: {CallId}, 成功: {Success}",
                                    _monitor.RequestId, callId, isSuccess);
                            }
                        }
                    }
                }
            }

            // 记录 Token 使用情况
            if (response?.Usage != null)
            {
                _logger.LogInformation(
                    "[{RequestId}] Token 使用: Input={InputTokens}, Output={OutputTokens}, Total={TotalTokens}",
                    _monitor.RequestId,
                    response.Usage.InputTokenCount ?? 0,
                    response.Usage.OutputTokenCount ?? 0,
                    response.Usage.TotalTokenCount ?? 0);
            }

            // 保存文本节点（非流式响应中的文本）
            if (_persistenceService != null && _executionRecord != null && _textBuffer.Length > 0)
            {
                try
                {
                    var text = _textBuffer.ToString();
                    var phaseEvent = _monitor.PhaseEvents.LastOrDefault();
                    await _persistenceService.AddTextChunkNodeAsync(
                        _executionRecord.Id,
                        _monitor.RequestId,
                        text,
                        phaseEvent?.ElapsedMs ?? 0);
                    _textBuffer.Clear();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{RequestId}] 保存文本节点失败", _monitor.RequestId);
                }
            }

            // 完成监控
            _monitor.Complete();

            // 保存 Complete 节点
            if (_persistenceService != null && _executionRecord != null)
            {
                try
                {
                    await _persistenceService.CompleteExecutionRecordAsync(
                        _executionRecord.Id,
                        _monitor,
                        isSuccessful: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{RequestId}] 保存完成节点失败", _monitor.RequestId);
                }
            }

            // 记录执行摘要
            _logger.LogInformation("[{RequestId}] 非流式请求完成 - {Summary}",
                _monitor.RequestId, _monitor.GetSummary());

            // 记录详细的阶段信息（Debug级别）
            _logger.LogDebug("[{RequestId}] 执行阶段详情:", _monitor.RequestId);
            foreach (var phase in _monitor.PhaseEvents)
            {
                var metadataJson = phase.Metadata != null
                    ? JsonSerializer.Serialize(phase.Metadata)
                    : "{}";
                _logger.LogDebug(
                    "[{RequestId}]   {Phase}: ElapsedMs={ElapsedMs:F2}, DeltaMs={DeltaMs:F2}, Metadata={Metadata}",
                    _monitor.RequestId, phase.Phase, phase.ElapsedMs, phase.DeltaMs, metadataJson);
            }

            // 记录工具调用详情（Debug级别）
            if (_monitor.ToolCalls.Any())
            {
                _logger.LogDebug("[{RequestId}] 工具调用详情:", _monitor.RequestId);
                foreach (var toolCall in _monitor.ToolCalls)
                {
                    _logger.LogDebug(
                        "[{RequestId}]   {ToolName}: CallId={CallId}, DurationMs={Duration:F2}, Success={Success}",
                        _monitor.RequestId, toolCall.Name, toolCall.CallId, toolCall.DurationMs ?? 0, toolCall.IsSuccess);
                }
            }

            return response;
        }

        /// <summary>
        /// 刷新文本缓冲区（保存合并的文本块）
        /// </summary>
        private async Task FlushTextBufferAsync()
        {
            if (_textBuffer.Length == 0 || _persistenceService == null || _executionRecord == null)
                return;

            try
            {
                var text = _textBuffer.ToString();
                var currentElapsedMs = _monitor.PhaseEvents.LastOrDefault()?.ElapsedMs ?? 0;

                await _persistenceService.AddTextChunkNodeAsync(
                    _executionRecord.Id,
                    _monitor.RequestId,
                    text,
                    currentElapsedMs);

                _textBuffer.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{RequestId}] 刷新文本缓冲区失败", _monitor.RequestId);
            }
        }

        /// <summary>
        /// 添加文本到缓冲区
        /// </summary>
        private void BufferText(string text)
        {
            if (_textBuffer.Length == 0)
            {
                _textBufferStartElapsedMs = _monitor.PhaseEvents.LastOrDefault()?.ElapsedMs ?? 0;
            }
            _textBuffer.Append(text);
        }
    }
}
