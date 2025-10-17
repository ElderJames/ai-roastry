using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Diagnostics;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Agents;
using LY.LlmPool.Web.Services.Tools;
using LY.LlmPool.Web.Services.Telemetry;
using LY.LlmPool.Web.Services.Monitoring;
using LY.LlmPool.Web.Components.ChatHelpers;
using LY.LlmPool.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.AI;

// Alias to avoid ambiguity
using AgentOrchestratorServiceAlias = LY.LlmPool.Web.Services.Agents.AgentOrchestratorService;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace LY.LlmPool.Web.Controllers
{
    [ApiController]
    [Route("v1")]
    [ActivityCapture] // 🎯 AOP: 自动捕获和保存 Activity,替代手动 FunctionCall 检测
    public class OpenAICompatController : ControllerBase
    {
        private readonly LlmPoolService _llmPoolService;
        private readonly CallRecordService _callRecordService;
        private readonly PromptParameterService _promptParameterService;
        private readonly ToolProviderService _toolProviderService;
        private readonly ChatClientFactory _chatClientFactory;
        private readonly ILogger<OpenAICompatController> _logger;
        private readonly ILogger<LoggingHttpHandler> _httpLogger;
        private readonly AgentOrchestratorServiceAlias _agentOrchestrator;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ChatExecutionPersistenceService _persistenceService;

        private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public OpenAICompatController(
            LlmPoolService llmPoolService,
            CallRecordService callRecordService,
            PromptParameterService promptParameterService,
            ToolProviderService toolProviderService,
            ChatClientFactory chatClientFactory,
            ILogger<OpenAICompatController> logger,
            ILogger<LoggingHttpHandler> httpLogger,
            AgentOrchestratorServiceAlias agentOrchestrator,
            IHttpClientFactory httpClientFactory,
            ChatExecutionPersistenceService persistenceService)
        {
            _llmPoolService = llmPoolService;
            _callRecordService = callRecordService;
            _promptParameterService = promptParameterService;
            _toolProviderService = toolProviderService;
            _chatClientFactory = chatClientFactory;
            _logger = logger;
            _httpLogger = httpLogger;
            _agentOrchestrator = agentOrchestrator;
            _httpClientFactory = httpClientFactory;
            _persistenceService = persistenceService;
        }

        [HttpPost("chat/completions")]
        public async Task ChatCompletions()
        {
            await ProcessChatCompletions();
        }

        private async Task ProcessChatCompletions()
        {
            // 🎯 从 HTTP Headers 中提取父 Activity Context（如果存在）
            ActivityContext parentContext = default;
            if (Request.Headers.TryGetValue("traceparent", out var traceparentValue))
            {
                var traceparent = traceparentValue.ToString();
                if (ActivityContext.TryParse(traceparent, null, out var parsedContext))
                {
                    parentContext = parsedContext;
                    _logger.LogInformation("从 traceparent header 提取父 Activity: TraceId={TraceId}, SpanId={SpanId}", 
                        parsedContext.TraceId, parsedContext.SpanId);
                }
            }

            // 🎯 创建 LlmPool 服务端请求处理 Activity
            using var requestActivity = ActivityExtensions.StartServerRequestActivity(
                route: "/v1/chat/completions",
                appName: null, // 稍后从请求体中解析后设置
                parentContext: parentContext);
            
            if (requestActivity != null)
            {
                requestActivity.SetTag("http.method", "POST");
                
                _logger.LogInformation("🌐 LlmPool Server Activity 已启动: TraceId={TraceId}, SpanId={SpanId}, ParentSpanId={ParentSpanId}",
                    requestActivity.TraceId, requestActivity.SpanId, requestActivity.ParentSpanId);
            }
            else
            {
                _logger.LogWarning("未能创建 Server Request Activity - 可能 ActivitySource 未启用");
            }
            
            EndpointCallRecord? callRecord = null;
            var requestStartTime = DateTime.UtcNow;
            object? requestData = null;
            string? requestBody = null;

            try
            {
                using (var reader = new StreamReader(Request.Body))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                if (!string.IsNullOrEmpty(requestBody))
                {
                    try
                    {
                        requestData = JsonSerializer.Deserialize<object>(requestBody, _jsonSerializerOptions);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Error parsing request JSON");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading request data");
            }

            // 从请求头中获取API Key
            if (!Request.Headers.TryGetValue("Authorization", out var authHeader) ||
                string.IsNullOrEmpty(authHeader) ||
                !authHeader.ToString().StartsWith("Bearer "))
            {
                Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                var err = new { error = new { message = "缺少或无效的 API Key。" } };
                await Response.WriteAsync(JsonSerializer.Serialize(err, _jsonSerializerOptions));
                return;
            }

            var apiKey = authHeader.ToString().Replace("Bearer ", "");

            try
            {
                _logger.LogInformation("收到聊天请求，请求体: {RequestBody}", requestBody);
                ChatRequest chatRequest;
                try
                {
                    chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestBody ?? "{}", _jsonSerializerOptions) ?? throw new InvalidOperationException("Invalid chat request");
                }
                catch (JsonException jsonEx)
                {
                    Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    var err = new { error = new { message = "Invalid JSON in request." } };
                    _logger.LogError(jsonEx, "Invalid JSON in chat request");
                    await Response.WriteAsync(JsonSerializer.Serialize(err, _jsonSerializerOptions));
                    return;
                }

                // 立即创建执行记录（记录请求开始）
                ChatExecutionRecord? executionRecord = null;
                var requestId = $"req_{Guid.NewGuid():N}";
                try
                {
                    executionRecord = await _persistenceService.CreateExecutionRecordAsync(
                        requestId: requestId,
                        requestModel: chatRequest.Model,
                        messageCount: chatRequest.Messages.Count,
                        startTime: requestStartTime);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "创建执行记录失败，继续处理请求");
                }

                _logger.LogInformation("解析的聊天请求 - RequestId: {RequestId}, 模型: {Model}, 消息数量: {MessageCount}",
                    requestId, chatRequest.Model, chatRequest.Messages.Count);

                var modelAcquireStartTime = DateTime.UtcNow;
                LlmConfig? config = null;
                LlmApp? app = null;
                string? promptContent = null;
                LlmPrompt? prompt = null;
                Dictionary<string, object>? modelParameters = null; // 从 Prompt.ModelParameters 解析的参数
                List<AITool>? promptTools = null;
                string? actualModelName = chatRequest.Model;
                string? actualEndpointId = null;
                string selectionStrategy = string.Empty;

                // 先按 app 名称解析
                if (!string.IsNullOrEmpty(chatRequest.Model))
                {
                    app = await _llmPoolService.GetAppByNameAsync(chatRequest.Model);
                }

                if (app != null)
                {
                    _logger.LogInformation("找到应用: {AppName}, 类型: {AppType}", app.Name, app.AppType);

                    // 🎯 记录 App 信息到 Activity
                    requestActivity?.SetTag("app.name", app.Name);
                    requestActivity?.SetTag("app.type", app.AppType);
                    requestActivity?.SetTag("app.id", app.Id);

                    // AgentGroup 应用走编排分支
                    if (string.Equals(app.AppType, "AgentGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("App {AppName} 为 AgentGroup 类型，进入编排分支。", app.Name);
                        requestActivity?.SetTag("routing", "agent_group");
                        await HandleAgentGroupAsync(chatRequest, app, requestData, requestStartTime);
                        return;
                    }

                    if (!string.IsNullOrEmpty(app.LlmConfigId))
                    {
                        var appConfig = await _llmPoolService.GetConfigByIdAsync(app.LlmConfigId);
                        if (appConfig != null && appConfig.IsEnabled)
                        {
                            config = appConfig;
                            actualModelName = appConfig.Model;
                            var acquired = await _llmPoolService.AcquireConfigIfAvailableAsync(appConfig.Id!);
                            if (!acquired)
                            {
                                await WriteAssistantMessageAsync(chatRequest.Model, "所有配置当前繁忙，请稍后重试。");
                                return;
                            }
                            selectionStrategy = "app-config";
                            var epForCfg = await _llmPoolService.GetEndpointForConfigAsync(appConfig.Id!);
                            if (epForCfg != null)
                            {
                                actualEndpointId = epForCfg.Id;
                                callRecord = await _callRecordService.CreateAsync(epForCfg.Id, requestData);
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(app.EndpointId))
                    {
                        config = await _llmPoolService.GetAvailableConfigByKeyAsync(app.EndpointId);
                        if (config != null)
                        {
                            actualModelName = config.Model;
                            actualEndpointId = app.EndpointId;
                            selectionStrategy = "app-endpoint-lb";
                            callRecord = await _callRecordService.CreateAsync(app.EndpointId, requestData);
                        }
                    }

                    if (!string.IsNullOrEmpty(app.PromptId))
                    {
                        prompt = await _llmPoolService.GetPromptByIdAsync(app.PromptId);
                        if (prompt != null)
                        {
                            promptContent = prompt.Content;
                            
                            // 解析 Prompt 的 ModelParameters
                            if (!string.IsNullOrWhiteSpace(prompt.ModelParameters))
                            {
                                modelParameters = ParameterUtils.ParseParametersToDict(prompt.ModelParameters);
                                _logger.LogInformation("从 Prompt 解析模型参数: {ModelParameters}", prompt.ModelParameters);
                            }
                        }
                    }
                    
                    // 🔧 获取 App 关联的所有工具（包括 App Tool 和 MCP Tool）
                    _logger.LogInformation("加载 App {AppName} 的工具...", app.Name);
                    promptTools = await _toolProviderService.GetToolsForAppAsync(app);
                    if (promptTools != null && promptTools.Count > 0)
                    {
                        _logger.LogInformation("成功加载 {Count} 个工具用于 App {AppName}", promptTools.Count, app.Name);
                        // 🎯 记录工具数量到 Activity
                        requestActivity?.SetTag("app.tools.count", promptTools.Count);
                    }
                    else
                    {
                        _logger.LogInformation("App {AppName} 没有关联的工具", app.Name);
                        requestActivity?.SetTag("app.tools.count", 0);
                    }

                    if (config == null)
                    {
                        await WriteAssistantMessageAsync(chatRequest.Model, $"应用 '{app.Name}' 没有有效的模型配置。");
                        return;
                    }
                }
                else
                {
                    // 非 app 请求：按 config 名称或 endpoint 名称解析
                    var cfgByName = await _llmPoolService.GetConfigByNameAsync(chatRequest.Model);
                    if (cfgByName != null)
                    {
                        var acquired = await _llmPoolService.AcquireConfigIfAvailableAsync(cfgByName.Id!);
                        if (!acquired)
                        {
                            await WriteAssistantMessageAsync(chatRequest.Model, "所有配置当前繁忙，请稍后重试。");
                            return;
                        }
                        config = cfgByName;
                        actualModelName = cfgByName.Model;
                        selectionStrategy = "config-name-direct";
                    }
                    else
                    {
                        var epByName = await _llmPoolService.GetEndpointByNameAsync(chatRequest.Model);
                        if (epByName != null)
                        {
                            config = await _llmPoolService.GetAvailableConfigByKeyAsync(epByName.Id);
                            actualEndpointId = epByName.Id;
                            if (config != null)
                            {
                                callRecord = await _callRecordService.CreateAsync(epByName.Id, requestData);
                                selectionStrategy = "endpoint-name-lb";
                                actualModelName = config.Model;
                            }
                        }
                        else
                        {
                            // 兼容：将 model 视为 endpoint id
                            config = await _llmPoolService.GetAvailableConfigByKeyAsync(chatRequest.Model);
                            actualEndpointId = chatRequest.Model;
                            if (config != null)
                            {
                                callRecord = await _callRecordService.CreateAsync(chatRequest.Model, requestData);
                                selectionStrategy = "endpoint-id-lb";
                            }
                        }
                    }
                }

                var waitTime = DateTime.UtcNow - modelAcquireStartTime;
                if (callRecord != null)
                {
                    await _callRecordService.UpdateWaitAsync(callRecord, waitTime);
                }

                if (config == null)
                {
                    if (executionRecord != null)
                    {
                        await _persistenceService.UpdateExecutionRecordErrorAsync(
                            executionRecord.Id,
                            "No available model found or invalid API key");
                    }

                    if (callRecord != null)
                    {
                        callRecord.IsSuccessful = false;
                        callRecord.ErrorMessage = "No available model found or invalid API key";
                        await _llmPoolService.UpdateCallRecordAsync(callRecord);
                    }

                    // 🎯 记录错误状态
                    requestActivity?.SetStatus(ActivityStatusCode.Error, "No available model found");
                    requestActivity?.SetTag("error.type", "ModelNotFound");

                    Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    _logger.LogError("No available model found or invalid API key");
                    var err = new { error = new { message = "未找到可用模型或 API Key 无效。" } };
                    await Response.WriteAsync(JsonSerializer.Serialize(err, _jsonSerializerOptions));
                    return;
                }

                // 🎯 记录成功获取的模型配置信息
                requestActivity?.SetTag("gen_ai.request.model", actualModelName ?? config.Model);
                requestActivity?.SetTag("gen_ai.system", config.BaseUrl);
                requestActivity?.SetTag("selection.strategy", selectionStrategy);
                requestActivity?.SetTag("app.name", config.Name); // 🎯 记录 LlmConfig 的名称
                if (!string.IsNullOrEmpty(actualEndpointId))
                {
                    requestActivity?.SetTag("endpoint.id", actualEndpointId);
                }

                if (callRecord != null)
                {
                    await _callRecordService.UpdateConfigAsync(callRecord, config.Id);
                    await _callRecordService.MarkStartAsync(callRecord);
                }

                // 参数替换（仅当存在应用 Prompt）
                if (!string.IsNullOrEmpty(promptContent) && app != null && chatRequest.Parameters != null && chatRequest.Parameters.Count > 0)
                {
                    var missingParams = _promptParameterService.ValidateParameters(promptContent, chatRequest.Parameters);
                    if (missingParams.Count > 0)
                    {
                        var errorMsg = $"Missing required parameters: {string.Join(", ", missingParams)}";

                        if (executionRecord != null)
                        {
                            await _persistenceService.UpdateExecutionRecordErrorAsync(
                                executionRecord.Id,
                                errorMsg);
                        }

                        if (callRecord != null)
                        {
                            callRecord.IsSuccessful = false;
                            callRecord.ErrorMessage = errorMsg;
                            await _llmPoolService.UpdateCallRecordAsync(callRecord);
                        }
                        await WriteAssistantMessageAsync(chatRequest.Model, $"缺少必要参数: {string.Join(", ", missingParams)}");
                        return;
                    }

                    var originalPromptContent = promptContent;
                    promptContent = _promptParameterService.ReplaceParameters(promptContent, chatRequest.Parameters);
                    _logger.LogInformation("参数替换完成 - 原始长度: {OriginalLength}, 替换后长度: {NewLength}, 参数数量: {ParamCount}", originalPromptContent.Length, promptContent.Length, chatRequest.Parameters.Count);
                }

                // 使用 Microsoft.Extensions.AI 处理请求
                // 🎯 创建 App 执行 Activity（如果有 App）
                using var appActivity = app != null 
                    ? ActivityExtensions.StartAppExecutionActivity(
                        appName: app.Name,
                        appType: app.AppType,
                        modelId: actualModelName
                      )
                    : null;
                
                if (appActivity != null)
                {
                    appActivity.SetTag("app.id", app!.Id);
                    appActivity.SetTag("selection.strategy", selectionStrategy);
                    if (!string.IsNullOrEmpty(actualEndpointId))
                    {
                        appActivity.SetTag("endpoint.id", actualEndpointId);
                    }
                    if (promptTools != null && promptTools.Count > 0)
                    {
                        appActivity.SetTag("app.tools_count", promptTools.Count);
                    }
                    
                    _logger.LogInformation("📱 App Activity 已启动: {AppName}, TraceId={TraceId}, SpanId={SpanId}",
                        app.Name, appActivity.TraceId, appActivity.SpanId);
                }
                
                try
                {
                    var convertedMessages = ConvertToAIChatMessages(chatRequest.Messages);
                    
                    if (chatRequest.Stream == true)
                    {
                        // 流式响应
                        await ProcessChatStreamingWithAI(
                            config: config,
                            messages: convertedMessages,
                            systemPrompt: promptContent,
                            tools: promptTools,
                            modelName: actualModelName,
                            modelParameters: modelParameters,
                            executionRecord: executionRecord,
                            cancellationToken: HttpContext.RequestAborted);
                    }
                    else
                    {
                        // 非流式响应
                        var aiResponse = await ProcessChatWithAI(
                            config: config,
                            messages: convertedMessages,
                            systemPrompt: promptContent,
                            tools: promptTools,
                            modelParameters: modelParameters,
                            executionRecord: executionRecord,
                            cancellationToken: HttpContext.RequestAborted);

                        await WriteChatCompletionResponse(aiResponse, actualModelName);
                    }
                    
                    // 🎯 记录 App 执行成功
                    if (appActivity != null)
                    {
                        appActivity.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                        _logger.LogInformation("✅ App 执行成功: {AppName}", app!.Name);
                    }

                    // 更新调用记录为成功
                    if (callRecord != null)
                    {
                        callRecord.IsSuccessful = true;
                        callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                        await _llmPoolService.UpdateCallRecordAsync(callRecord);
                        
                        await _callRecordService.FinalizeAsync(
                            callRecord,
                            selectionStrategy,
                            actualEndpointId,
                            config.Id,
                            config.Name,
                            app?.Name,
                            actualModelName ?? chatRequest.Model ?? string.Empty,
                            requestStartTime);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "使用 Extensions.AI 处理请求时出错");
                    
                    // 🎯 记录 App 执行失败
                    if (appActivity != null)
                    {
                        appActivity.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                        appActivity.AddTag("error.type", ex.GetType().Name);
                        appActivity.AddTag("error.message", ex.Message);
                        _logger.LogError("❌ App 执行失败: {AppName}, Error: {ErrorMessage}", app!.Name, ex.Message);
                    }
                    

                    if (executionRecord != null)
                    {
                        await _persistenceService.UpdateExecutionRecordErrorAsync(
                            executionRecord.Id,
                            ex.Message);
                    }

                    if (callRecord != null)
                    {
                        callRecord.IsSuccessful = false;
                        callRecord.ErrorMessage = ex.Message;
                        await _llmPoolService.UpdateCallRecordAsync(callRecord);
                    }

                    await WriteOpenAIErrorAsync(chatRequest.Model, "处理请求时出错: " + ex.Message, HttpStatusCode.InternalServerError);
                }
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogInformation("Request canceled by client.");
                return;
            }
            catch (TaskCanceledException)
            {
                if (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("Request task canceled by client.");
                    return;
                }
                throw;
            }
            catch (IOException ioEx) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.LogInformation(ioEx, "IO canceled due to client disconnect.");
                // 🎯 记录中断状态
                requestActivity?.SetStatus(ActivityStatusCode.Error, "Client disconnected");
                requestActivity?.SetTag("error.type", "IOException");
                return;
            }
            catch (Exception ex)
            {
                if (callRecord != null)
                {
                    await _callRecordService.MarkErrorAsync(callRecord, ex.Message);
                }
                
                // 🎯 记录异常状态
                requestActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                requestActivity?.SetTag("error.type", ex.GetType().Name);
                requestActivity?.SetTag("error.message", ex.Message);
                
                _logger.LogError(ex, "Error processing request");
                await WriteAssistantMessageAsync(null, "服务器内部错误。");
            }
            finally
            {
                // 🎯 如果没有设置状态，默认为成功
                if (requestActivity != null && requestActivity.Status == ActivityStatusCode.Unset)
                {
                    requestActivity.SetStatus(ActivityStatusCode.Ok);
                }
                
                if (Request != null)
                {
                    // 释放占用的配置
                    // 注意：只有通过 AcquireConfigIfAvailable 成功占用的才需要释放
                    // 这里简化：若解析到了 config.Id 则尝试释放
                }
            }
        }

        private async Task WriteOpenAIErrorAsync(string? model, string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest, string type = "invalid_request_error", string? code = null)
        {
            if (Response.HasStarted)
            {
                _logger.LogWarning("Skip writing OpenAI error because response has already started: {Message}", message);
                return;
            }
            Response.StatusCode = (int)statusCode;
            var payload = new
            {
                error = new
                {
                    message = message,
                    type = type,
                    param = (string?)null,
                    code = code
                }
            };

            await Response.WriteAsync(JsonSerializer.Serialize(payload, _jsonSerializerOptions));
        }

        private async Task WriteAssistantMessageAsync(string? model, string message)
        {
            if (Response.HasStarted)
            {
                _logger.LogWarning("Skip writing assistant message because response has already started: {Message}", message);
                return;
            }
            
            // 🎯 从当前 Activity 中获取 ConversationId 并添加到响应 header
            var conversationId = Activity.Current?.GetTagItem(ActivityExtensions.GenAIConversationId)?.ToString();
            if (!string.IsNullOrEmpty(conversationId))
            {
                Response.Headers.Append("X-Conversation-Id", conversationId);
                _logger.LogDebug("📤 响应 Header: X-Conversation-Id={ConversationId}", conversationId);
            }
            
            Response.StatusCode = (int)HttpStatusCode.OK;
            var payload = new
            {
                id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                Object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = model,
                choices = new[]
                {
                    new
                    {
                        message = new { role = "assistant", content = message },
                        index = 0,
                        finish_reason = "stop"
                    }
                },
                usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
            };

            await Response.WriteAsync(JsonSerializer.Serialize(payload, _jsonSerializerOptions));
        }

        private async Task HandleAgentGroupAsync(ChatRequest chatRequest, LlmApp app, object? requestData, DateTime requestStartTime)
        {
            try
            {
                // 创建调用记录（为AgentGroup使用虚拟endpointId）
                var callRecord = await _callRecordService.CreateAsync("agent-group", requestData);

                // 转换消息格式
                var userMessages = ConvertToAIChatMessages(chatRequest.Messages);

                // 若请求需要流式返回（SSE），通过 onProgress 回调写 SSE 数据
                if (chatRequest.Stream == true)
                {
                    // 🎯 从当前 Activity 中获取 ConversationId 并添加到响应 header
                    var conversationId = Activity.Current?.GetTagItem(ActivityExtensions.GenAIConversationId)?.ToString();
                    if (!string.IsNullOrEmpty(conversationId))
                    {
                        Response.Headers.Append("X-Conversation-Id", conversationId);
                        _logger.LogDebug("📤 响应 Header (Stream): X-Conversation-Id={ConversationId}", conversationId);
                    }
                    
                    Response.StatusCode = (int)HttpStatusCode.OK;
                    Response.ContentType = "text/event-stream";
                    Response.Headers.Append("Cache-Control", "no-cache");

                    // onProgress 写出 data: {json}\n\n 格式
                    async Task ProgressWriter(string agentName, string? role, int step, string text, bool done)
                    {
                        if (!Response.HasStarted) { /* no-op: headers already sent */ }
                        var chunk = new
                        {
                            id = $"chatcmpl-{Guid.NewGuid().ToString("N")}",
                            @object = "chat.completion.chunk",
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            choices = new[]
                            {
                                new
                                {
                                    delta = new { role = "assistant", content = text },
                                    index = 0
                                }
                            }
                        };
                        var json = JsonSerializer.Serialize(chunk, _jsonSerializerOptions);
                        await Response.WriteAsync($"data: {json}\n\n");
                        await Response.Body.FlushAsync();
                        if (done)
                        {
                            await Response.WriteAsync("data: [DONE]\n\n");
                            await Response.Body.FlushAsync();
                        }
                    }

                    // 调用编排服务并传入 progress 回调
                    var result = await _agentOrchestrator.ExecuteAsync(app, userMessages, async (name, role, step, text, done) =>
                    {
                        try { await ProgressWriter(name, role, step, text, done); } catch { }
                    });

                    // 在将结束标记写回客户端之前，先尝试更新调用记录，避免在写入完成后宿主可能已释放请求作用域导致的 ObjectDisposedException
                    if (callRecord != null)
                    {
                        try
                        {
                            callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                            callRecord.IsSuccessful = true;
                            await _llmPoolService.UpdateCallRecordAsync(callRecord);
                        }
                        catch (ObjectDisposedException odEx)
                        {
                            _logger.LogWarning(odEx, "Call record update failed due to disposed service provider during streaming finalization for call {CallId}", callRecord.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Call record update failed during streaming finalization for call {CallId}", callRecord.Id);
                        }
                    }

                    // 最后写入结束标记（如果服务没有写完）
                    try
                    {
                        await Response.WriteAsync("data: [DONE]\n\n");
                        await Response.Body.FlushAsync();
                    }
                    catch { }

                    return;
                }

                // 非流式，直接调用并返回完整结果
                var nonStreamResult = await _agentOrchestrator.ExecuteAsync(app, userMessages);

                // 记录响应时间（简化处理）
                if (callRecord != null)
                {
                    callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                    callRecord.IsSuccessful = true;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                // 返回OpenAI兼容格式
                var response = new
                {
                    id = $"chatcmpl-{Guid.NewGuid().ToString("N")}",
                    @object = "chat.completion",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = chatRequest.Model,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = nonStreamResult
                            },
                            finish_reason = "stop"
                        }
                    },
                    usage = new
                    {
                        prompt_tokens = 0, // TODO: 计算实际token数
                        completion_tokens = 0,
                        total_tokens = 0
                    }
                };

                Response.ContentType = "application/json";
                await Response.WriteAsync(JsonSerializer.Serialize(response, _jsonSerializerOptions));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling AgentGroup request for app {AppName}", app.Name);
                await WriteAssistantMessageAsync(chatRequest.Model, $"Agent orchestration failed: {ex.Message}");
            }
        }

        private class ChatRequest
        {
            public string Model { get; set; } = string.Empty;
            public List<JsonElement> Messages { get; set; } = new();
            public bool? Stream { get; set; }
            [JsonPropertyName("parameters")] public Dictionary<string, object>? Parameters { get; set; }
            [JsonPropertyName("tools")] public List<OpenAITool>? Tools { get; set; }
            [JsonPropertyName("tool_choice")] public JsonElement ToolChoice { get; set; }
            public float? Temperature { get; set; }
            public int? MaxTokens { get; set; }
            public float? TopP { get; set; }
            public float? FrequencyPenalty { get; set; }
            public float? PresencePenalty { get; set; }
        }

        /// <summary>
        /// 将 Microsoft.Extensions.AI.ChatMessage 转换为 Microsoft.Extensions.AI.ChatMessage
        /// </summary>
        private List<AIChatMessage> ConvertToAIChatMessages(List<AIChatMessage> messages)
        {
            var result = new List<AIChatMessage>();
            
            foreach (var msg in messages)
            {
                // msg.Role 已经是 ChatRole 类型，直接使用
                result.Add(new AIChatMessage(msg.Role, msg.Text ?? string.Empty));
            }

            return result;
        }

        private static string ExtractRole(JsonElement message)
        {
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("role", out var roleElement))
            {
                return roleElement.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        private static string ExtractContent(JsonElement message)
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (!message.TryGetProperty("content", out var contentElement))
            {
                return string.Empty;
            }

            if (contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString() ?? string.Empty;
            }

            if (contentElement.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in contentElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (item.TryGetProperty("type", out var typeElement) &&
                        typeElement.ValueKind == JsonValueKind.String &&
                        string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                        item.TryGetProperty("text", out var textElement) &&
                        textElement.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(textElement.GetString());
                    }
                }

                return sb.ToString();
            }

            return string.Empty;
        }

        /// <summary>
        /// 将原始消息 JSON 转换为 AIChatMessage
        /// </summary>
        private List<AIChatMessage> ConvertToAIChatMessages(List<JsonElement> messages)
        {
            return messages.Select(m =>
            {
                var roleName = ExtractRole(m);
                var role = roleName.ToLowerInvariant() switch
                {
                    "system" => ChatRole.System,
                    "assistant" => ChatRole.Assistant,
                    "tool" => ChatRole.Tool,
                    _ => ChatRole.User
                };

                var content = ExtractContent(m);
                return new AIChatMessage(role, content);
            }).ToList();
        }

        /// <summary>
        /// 使用 Microsoft.Extensions.AI 处理聊天完成（简化版本）
        /// </summary>
        private async Task<AIChatResponse> ProcessChatWithAI(
            LlmConfig config,
            List<AIChatMessage> messages,
            string? systemPrompt = null,
            List<AITool>? tools = null,
            Dictionary<string, object>? modelParameters = null,
            ChatExecutionRecord? executionRecord = null,
            bool stream = false,
            CancellationToken cancellationToken = default)
        {
            // 创建执行监控器
            var requestId = executionRecord?.RequestId ?? $"req_{Guid.NewGuid():N}";
            var monitor = new ChatExecutionMonitor(requestId);
            monitor.ExecutionRecordId = executionRecord?.Id;

            // 创建带监控的 IChatClient
            var chatClient = _chatClientFactory.CreateClientWithMonitoring(
                config,
                monitor,
                "UpstreamLlm");
            //// 🎯 创建启用了 OpenTelemetry 和 FunctionInvocation 的 IChatClient
            //// 这样会自动创建 gen_ai.choice Activity，并支持工具调用
            //var chatClient = _chatClientFactory.CreateClientWithHttpClient(
            //    config, 
            //    "UpstreamLlm",
            //    enableFunctionInvocation: true  // 🔑 启用以触发 UseOpenTelemetry()
            //);

            // 转换消息
            var aiMessages = messages;

            // 如果有系统提示，插入到开头
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                aiMessages.Insert(0, new AIChatMessage(ChatRole.System, systemPrompt));
            }

            // 🎯 记录输入消息到 Activity (用于追踪)
            var currentActivity = Activity.Current;
            if (currentActivity != null && aiMessages.Any())
            {
                var inputMessagesJson = JsonSerializer.Serialize(
                    aiMessages.Select(m => new { role = m.Role.ToString(), content = m.Text }).ToList()
                );
                currentActivity.SetTag("gen_ai.prompt", inputMessagesJson);
                _logger.LogDebug("📝 记录输入消息到 Activity: {MessageCount} messages", aiMessages.Count);
            }

            // 创建选项
            var options = new ChatOptions();

            // 添加工具（如果有）
            if (tools != null && tools.Count > 0)
            {
                options.Tools = tools;
                _logger.LogInformation("添加 {Count} 个工具到 ChatOptions", tools.Count);
            }

            // 应用模型参数（从 Prompt.ModelParameters）
            if (modelParameters != null && modelParameters.Count > 0)
            {
                ParameterUtils.ApplyParametersToChatOptions(options, modelParameters);
                _logger.LogInformation("应用 Prompt 模型参数到 ChatOptions");
            }

            // 调用 AI - 只支持非流式
            if (stream)
            {
                throw new NotSupportedException("请使用 ProcessChatStreamingWithAI 处理流式响应");
            }

            // 🎯 不再手动创建 llmpool.external_model Activity
            // Microsoft.Extensions.AI 的 UseOpenTelemetry() 已经创建了 "chat {model}" Activity
            // 并且会自动创建工具调用的 "execute_tool {toolName}" Activity 作为其子级
            
            _logger.LogInformation("📡 开始非流式 AI 调用: Model={Model}", config.Model);

            try
            {
                var response = await chatClient.GetResponseAsync(aiMessages, options, cancellationToken);
                
                // 🎯 记录输出内容到 Activity (用于追踪)
                if (currentActivity != null)
                {
                    var lastMessage = response.Messages.LastOrDefault();
                    var outputText = lastMessage?.Text ?? "";
                    if (!string.IsNullOrEmpty(outputText))
                    {
                        currentActivity.SetTag("gen_ai.completion", outputText);
                        _logger.LogDebug("📝 记录输出内容到 Activity: {Length} chars", outputText.Length);
                    }
                }
                
                _logger.LogInformation("✅ 非流式 AI 调用完成: Model={ModelId}, Tokens={InputTokens}+{OutputTokens}", 
                    response.ModelId,
                    response.Usage?.InputTokenCount ?? 0,
                    response.Usage?.OutputTokenCount ?? 0);
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 非流式 AI 调用失败");
                throw;
            }
        }

        /// <summary>
        /// 使用 Microsoft.Extensions.AI 处理流式聊天完成
        /// </summary>
        private async Task ProcessChatStreamingWithAI(
            LlmConfig config,
            List<AIChatMessage> messages,
            string? systemPrompt = null,
            List<AITool>? tools = null,
            string? modelName = null,
            Dictionary<string, object>? modelParameters = null,
            ChatExecutionRecord? executionRecord = null,
            CancellationToken cancellationToken = default)
        {
            // 创建执行监控器
            var requestId = executionRecord?.RequestId ?? $"req_{Guid.NewGuid():N}";
            var monitor = new ChatExecutionMonitor(requestId);
            monitor.ExecutionRecordId = executionRecord?.Id;

            // 创建带监控的 IChatClient
            var chatClient = _chatClientFactory.CreateClientWithMonitoring(
                config,
                monitor,
                "UpstreamLlm");
            // 创建 IChatClient
            //var chatClient = _chatClientFactory.CreateClientWithHttpClient(config, "UpstreamLlm");
            //// 🎯 创建启用了 OpenTelemetry 和 FunctionInvocation 的 IChatClient
            //// 这样会自动创建 gen_ai.choice Activity，并支持工具调用
            //var chatClient = _chatClientFactory.CreateClientWithHttpClient(
            //    config, 
            //    "UpstreamLlm",
            //    enableFunctionInvocation: true  // 🔑 启用以触发 UseOpenTelemetry()
            //);

            // 转换消息
            var aiMessages = messages;

            // 如果有系统提示，插入到开头
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                aiMessages.Insert(0, new AIChatMessage(ChatRole.System, systemPrompt));
            }

            // 🎯 记录输入消息到 Activity (用于追踪)
            var currentActivity = Activity.Current;
            if (currentActivity != null && aiMessages.Any())
            {
                var inputMessagesJson = JsonSerializer.Serialize(
                    aiMessages.Select(m => new { role = m.Role.ToString(), content = m.Text }).ToList()
                );
                currentActivity.SetTag("gen_ai.prompt", inputMessagesJson);
                _logger.LogDebug("📝 记录输入消息到 Activity (Streaming): {MessageCount} messages", aiMessages.Count);
            }

            // 创建选项
            var options = new ChatOptions();

            // 添加工具（如果有）
            if (tools != null && tools.Count > 0)
            {
                options.Tools = tools;
                _logger.LogInformation("添加 {Count} 个工具到流式 ChatOptions", tools.Count);
            }

            // 应用模型参数（从 Prompt.ModelParameters）
            if (modelParameters != null && modelParameters.Count > 0)
            {
                ParameterUtils.ApplyParametersToChatOptions(options, modelParameters);
                _logger.LogInformation("应用 Prompt 模型参数到 ChatOptions");
            }

            // 设置响应头
            Response.StatusCode = (int)HttpStatusCode.OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");
            
            // 🎯 从当前 Activity 中获取 ConversationId 并添加到响应 header
            var conversationId = Activity.Current?.GetTagItem(ActivityExtensions.GenAIConversationId)?.ToString();
            if (!string.IsNullOrEmpty(conversationId))
            {
                Response.Headers.Append("X-Conversation-Id", conversationId);
                _logger.LogDebug("📤 响应 Header (Extensions.AI Stream): X-Conversation-Id={ConversationId}", conversationId);
            }

            // 🎯 不再手动创建 llmpool.external_model Activity
            // Microsoft.Extensions.AI 的 UseOpenTelemetry() 已经创建了 "chat {model}" Activity
            // 并且会自动创建工具调用的 "execute_tool {toolName}" Activity 作为其子级
            
            _logger.LogInformation("📡 开始流式 AI 调用: Model={Model}", config.Model);

            // 流式调用 AI
            var firstChunk = true;
            var fullContent = new StringBuilder();
            
            // 🎯 AOP 优化: ActivityCaptureAttribute 已在 Action 执行前自动保存 Activity
            // 无需在 foreach 中手动捕获
            
            try
            {
                await foreach (var update in chatClient.GetStreamingResponseAsync(aiMessages, options, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // 处理文本内容
                    var text = update.Text ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        fullContent.Append(text);

                    // 构建 SSE 格式的响应
                    var delta = firstChunk 
                        ? (object)new { role = "assistant", content = text }
                        : new { content = text };

                    var chunk = new
                    {
                        id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                        Object = "chat.completion.chunk",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = modelName,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                delta = delta,
                                finish_reason = (string?)null
                            }
                        }
                    };

                    var json = JsonSerializer.Serialize(chunk, _jsonSerializerOptions);
                    await Response.WriteAsync($"data: {json}\n\n");
                    await Response.Body.FlushAsync(cancellationToken);

                    firstChunk = false;
                }

                // 处理工具调用 (FunctionCallContent)
                if (update.Contents != null)
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is Microsoft.Extensions.AI.FunctionCallContent functionCall)
                        {
                            // 🎯 AOP 优化: ActivityCaptureAttribute 已自动保存 Activity.Current
                            // 无需手动检测和保存,直接处理 FunctionCall
                            
                            var callId = functionCall.CallId ?? $"call_{Guid.NewGuid():N}";
                            
                            // 序列化工具参数
                            string argumentsJson;
                            if (functionCall.Arguments != null && functionCall.Arguments.Count > 0)
                            {
                                argumentsJson = JsonSerializer.Serialize(functionCall.Arguments, _jsonSerializerOptions);
                            }
                            else
                            {
                                argumentsJson = "{}";
                            }

                            // 构建 OpenAI 格式的 tool_calls 更新
                            var toolCallDelta = firstChunk
                                ? (object)new
                                {
                                    role = "assistant",
                                    tool_calls = new[]
                                    {
                                        new
                                        {
                                            index = 0,
                                            id = callId,
                                            type = "function",
                                            function = new
                                            {
                                                name = functionCall.Name,
                                                arguments = argumentsJson
                                            }
                                        }
                                    }
                                }
                                : new
                                {
                                    tool_calls = new[]
                                    {
                                        new
                                        {
                                            index = 0,
                                            id = callId,
                                            type = "function",
                                            function = new
                                            {
                                                name = functionCall.Name,
                                                arguments = argumentsJson
                                            }
                                        }
                                    }
                                };

                            var toolCallChunk = new
                            {
                                id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                                Object = "chat.completion.chunk",
                                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                model = modelName,
                                choices = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        delta = toolCallDelta,
                                        finish_reason = (string?)null
                                    }
                                }
                            };

                            var toolCallJson = JsonSerializer.Serialize(toolCallChunk, _jsonSerializerOptions);
                            await Response.WriteAsync($"data: {toolCallJson}\n\n");
                            await Response.Body.FlushAsync(cancellationToken);

                            _logger.LogInformation("返回工具调用 SSE 更新: {ToolName}, CallId: {CallId}", functionCall.Name, callId);
                            firstChunk = false;
                        }
                        // 处理工具结果 (FunctionResultContent)
                        else if (content is Microsoft.Extensions.AI.FunctionResultContent functionResult)
                        {
                            var callId = functionResult.CallId ?? string.Empty;
                            var result = functionResult.Result?.ToString() ?? string.Empty;
                            var isSuccess = functionResult.Exception == null;
                            var errorMessage = functionResult.Exception?.Message;

                            // 构建工具结果更新（使用 content 字段传递结果）
                            var resultDelta = new
                            {
                                content = isSuccess 
                                    ? $"\n[Tool Result: {callId}]\n{result}\n" 
                                    : $"\n[Tool Error: {callId}]\n{errorMessage}\n"
                            };

                            var resultChunk = new
                            {
                                id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                                Object = "chat.completion.chunk",
                                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                model = modelName,
                                choices = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        delta = resultDelta,
                                        finish_reason = (string?)null
                                    }
                                }
                            };

                            var resultJson = JsonSerializer.Serialize(resultChunk, _jsonSerializerOptions);
                            await Response.WriteAsync($"data: {resultJson}\n\n");
                            await Response.Body.FlushAsync(cancellationToken);

                            _logger.LogInformation("返回工具结果 SSE 更新: CallId: {CallId}, Success: {Success}", callId, isSuccess);
                        }
                    }
                }
            }

            // 🎯 记录输出内容到 Activity (用于追踪)
            var fullContentText = fullContent.ToString();
            if (currentActivity != null && !string.IsNullOrEmpty(fullContentText))
            {
                currentActivity.SetTag("gen_ai.completion", fullContentText);
                _logger.LogDebug("📝 记录输出内容到 Activity (Streaming): {Length} chars", fullContentText.Length);
            }

            // 发送结束标记
            var finalChunk = new
            {
                id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                Object = "chat.completion.chunk",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = modelName,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = "stop"
                    }
                }
            };

            var finalJson = JsonSerializer.Serialize(finalChunk, _jsonSerializerOptions);
            await Response.WriteAsync($"data: {finalJson}\n\n");
            await Response.WriteAsync("data: [DONE]\n\n");
            await Response.Body.FlushAsync(cancellationToken);
            
            // ✅ 完成状态
            // OpenTelemetryChatClient 已经自动记录了所有必要的信息
            _logger.LogInformation("✅ 流式 AI 调用完成,总字符数: {Length}", fullContent.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 流式 AI 调用失败");
                throw;
            }
        }

        /// <summary>
        /// 将 ChatResponse 转换为 OpenAI 格式的响应
        /// </summary>
        private async Task WriteChatCompletionResponse(AIChatResponse completion, string? modelName)
        {
            // ChatResponse.Messages 是完整的对话历史，最后一条是 AI 的回复
            var lastMessage = completion.Messages.LastOrDefault();
            var content = lastMessage?.Text ?? string.Empty;
            var role = lastMessage?.Role.Value.ToLowerInvariant() ?? "assistant";

            var payload = new
            {
                id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                Object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = modelName,
                choices = new[]
                {
                    new
                    {
                        message = new 
                        { 
                            role = role, 
                            content = content 
                        },
                        index = 0,
                        finish_reason = completion.FinishReason?.ToString()?.ToLowerInvariant() ?? "stop"
                    }
                },
                usage = new 
                { 
                    prompt_tokens = completion.Usage?.InputTokenCount ?? 0, 
                    completion_tokens = completion.Usage?.OutputTokenCount ?? 0, 
                    total_tokens = completion.Usage?.TotalTokenCount ?? 0 
                }
            };

            Response.StatusCode = (int)HttpStatusCode.OK;
            Response.ContentType = "application/json";
            await Response.WriteAsync(JsonSerializer.Serialize(payload, _jsonSerializerOptions));
        }
    }
}

