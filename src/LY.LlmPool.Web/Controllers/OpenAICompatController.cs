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
using LY.LlmPool.Web.Models.OpenAI;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Agents;
using LY.LlmPool.Web.Services.Tools;
using LY.LlmPool.Web.Services.Telemetry;
using LY.LlmPool.Web.Services.Monitoring;
using LY.LlmPool.Web.Services.LoadBalancing;
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
        // 🎯 HttpContext.Items 键，用于存储当前请求的 requestActivity
        private const string RequestActivityKey = "LlmPool.RequestActivity";

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
        private readonly LoadBalancerService _loadBalancer; // 🎯 注入负载均衡服务

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
            ChatExecutionPersistenceService persistenceService,
            LoadBalancerService loadBalancer) // 🎯 注入负载均衡服务
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
            _loadBalancer = loadBalancer; // 🎯 注入负载均衡服务
        }

        [HttpPost("chat/completions")]
        public async Task ChatCompletions()
        {
            await ProcessChatCompletions();
        }

        /// <summary>
        /// 从 HttpContext.Items 获取当前请求的 requestActivity
        /// </summary>
        private Activity? GetRequestActivity()
        {
            return HttpContext.Items[RequestActivityKey] as Activity;
        }

        private async Task ProcessChatCompletions()
        {
            // 🎯 从 HTTP Headers 中提取父 Activity Context（如果存在）
            ActivityContext parentContext = default;
            bool hasTraceparent = false;

            if (Request.Headers.TryGetValue("traceparent", out var traceparentValue))
            {
                hasTraceparent = true;
                var traceparent = traceparentValue.ToString();

                _logger.LogInformation("📨 收到 traceparent header: {Traceparent}", traceparent);

                if (ActivityContext.TryParse(traceparent, null, out var parsedContext))
                {
                    parentContext = parsedContext;
                    _logger.LogInformation("✅ 成功解析 traceparent - TraceId={TraceId}, SpanId={SpanId}",
                        parsedContext.TraceId, parsedContext.SpanId);
                }
                else
                {
                    _logger.LogWarning("⚠️ 无法解析 traceparent: {Traceparent}", traceparent);
                }
            }
            else
            {
                _logger.LogInformation("ℹ️ 未收到 traceparent header - 将创建新的 TraceId");
            }

            // 🎯 创建 LlmPool 服务端请求处理 Activity
            using var requestActivity = ActivityExtensions.StartServerRequestActivity(
                route: "/v1/chat/completions",
                appName: null, // 稍后从请求体中解析后设置
                parentContext: parentContext);

            // 🎯 将 requestActivity 存储到 HttpContext.Items 供所有方法使用
            HttpContext.Items[RequestActivityKey] = requestActivity;

            if (requestActivity != null)
            {
                requestActivity.SetTag("http.method", "POST");
                requestActivity.SetTag("http.has_traceparent", hasTraceparent);
                requestActivity.SetTag("llmpool.is_root", parentContext == default);  // 🎯 标记是否为根节点
            }
            else
            {
                _logger.LogWarning("❌ 未能创建 Server Request Activity - ActivitySource 可能未启用");
            }

            EndpointCallRecord? callRecord = null;
            var requestStartTime = DateTime.UtcNow;
            object? requestData = null;
            string? requestBody = null;
            LlmConfig? config = null; // 🎯 移到外层以便 finally 访问
            bool shouldReleaseConfig = false; // 🎯 标记是否需要释放配置锁

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
                requestActivity?.SetTag("request.body", requestData);
                _logger.LogInformation("收到聊天请求，请求体: {RequestBody}", requestData);
                ChatRequest chatRequest;
                try
                {
                    chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestBody ?? "{}", _jsonSerializerOptions) ?? throw new InvalidOperationException("Invalid chat request");
                    
                    // 🎯 日志记录解析后的 Parameters
                    if (chatRequest.Parameters != null && chatRequest.Parameters.Count > 0)
                    {
                        _logger.LogInformation("📦 解析到的 Parameters 数量: {Count}", chatRequest.Parameters.Count);
                        foreach (var kvp in chatRequest.Parameters)
                        {
                            _logger.LogInformation("  - {Key} = {Value} (Type: {Type})", 
                                kvp.Key, kvp.Value, kvp.Value?.GetType().Name ?? "null");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ 未解析到任何 Parameters");
                    }
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
                config = null; // 重置
                LlmApp? app = null;
                string? promptContent = null;
                LlmPrompt? prompt = null;
                Dictionary<string, object>? modelParameters = null; // 从 Prompt.ModelParameters 解析的参数
                List<AITool>? promptTools = null;
                string? actualModelName = chatRequest.Model;
                string? actualEndpointId = null;
                string selectionStrategy = string.Empty;
                shouldReleaseConfig = false; // 重置

                // 🎯 使用 LoadBalancerService 进行配置选择和负载均衡
                _logger.LogInformation("使用 LoadBalancerService 为模型名称 '{ModelName}' 选择配置...", chatRequest.Model);
                var selectionResult = await _loadBalancer.SelectConfigAsync(chatRequest.Model);

                if (selectionResult == null)
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

                    requestActivity?.SetStatus(ActivityStatusCode.Error, "No available model found");
                    requestActivity?.SetTag("error.type", "ModelNotFound");

                    Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    _logger.LogError("LoadBalancerService 未能找到 '{ModelName}' 的可用配置", chatRequest.Model);
                    var err = new { error = new { message = $"未能找到 '{chatRequest.Model}' 的可用配置" } };
                    await Response.WriteAsync(JsonSerializer.Serialize(err, _jsonSerializerOptions));
                    return;
                }

                // 🎯 从 LoadBalancerService 的结果中提取信息
                app = selectionResult.App;
                selectionStrategy = selectionResult.Strategy;
                shouldReleaseConfig = selectionResult.NeedsRelease;

                // 🎯 AgentGroup 应用走编排分支（AgentGroup 不需要 Config）
                if (app != null && string.Equals(app.AppType, "AgentGroup", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("App {AppName} 为 AgentGroup 类型，进入编排分支。", app.Name);
                    requestActivity?.SetTag("app.name", app.Name);
                    requestActivity?.SetTag("app.type", app.AppType);
                    requestActivity?.SetTag("app.id", app.Id);
                    requestActivity?.SetTag("routing", "agent_group");

                    await HandleAgentGroupAsync(chatRequest, app, requestData, requestStartTime);
                    return;
                }

                // 🎯 非 AgentGroup 类型必须有 Config
                config = selectionResult.Config!;
                actualModelName = config.Model;
                actualEndpointId = selectionResult.Endpoint?.Id;

                _logger.LogInformation("LoadBalancerService 选择配置: Config={ConfigName}, Strategy={Strategy}, NeedsRelease={NeedsRelease}",
                    config.Name, selectionStrategy, shouldReleaseConfig);

                // 🎯 记录 App 信息到 Activity（如果是 App 请求）
                if (app != null)
                {
                    requestActivity?.SetTag("app.name", app.Name);
                    requestActivity?.SetTag("app.type", app.AppType);
                    requestActivity?.SetTag("app.id", app.Id);

                    // 🎯 加载 App 的 Prompt（从缓存中获取，无需额外查询）
                    if (app.LlmPrompt != null)
                    {
                        prompt = app.LlmPrompt;
                        promptContent = prompt.Content;

                        // 解析 Prompt 的 ModelParameters
                        if (!string.IsNullOrWhiteSpace(prompt.ModelParameters))
                        {
                            modelParameters = ParameterUtils.ParseParametersToDict(prompt.ModelParameters);
                            _logger.LogInformation("从 Prompt 解析模型参数: {ModelParameters}", prompt.ModelParameters);
                        }
                    }

                    // 🔧 获取 App 关联的所有工具（包括 App Tool 和 MCP Tool）
                    _logger.LogInformation("加载 App {AppName} 的工具...", app.Name);
                    promptTools = await _toolProviderService.GetToolsForAppAsync(app);
                    if (promptTools != null && promptTools.Count > 0)
                    {
                        _logger.LogInformation("成功加载 {Count} 个工具用于 App {AppName}", promptTools.Count, app.Name);
                        requestActivity?.SetTag("app.tools.count", promptTools.Count);
                    }
                    else
                    {
                        _logger.LogInformation("App {AppName} 没有关联的工具", app.Name);
                        requestActivity?.SetTag("app.tools.count", 0);
                    }
                }

                // 🎯 创建 CallRecord（如果需要）
                if (!string.IsNullOrEmpty(actualEndpointId) && callRecord == null)
                {
                    callRecord = await _callRecordService.CreateAsync(actualEndpointId, requestData);
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
                    var err = new { error = new { message = $"未找到 '{chatRequest.Model}' 的可用配置。" } };
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
                if (!string.IsNullOrEmpty(promptContent) && app != null)
                {
                    // 🎯 只有当参数不为空时才验证必填参数
                    if (chatRequest.Parameters != null && chatRequest.Parameters.Count > 0)
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
                    }

                    // 🎯 无论是否传参数,都执行替换(空参数会将占位符替换为空字符串)
                    var originalPromptContent = promptContent;
                    promptContent = _promptParameterService.ReplaceParameters(promptContent, chatRequest.Parameters);
                    _logger.LogInformation("参数替换完成 - 原始长度: {OriginalLength}, 替换后长度: {NewLength}, 参数数量: {ParamCount}", 
                        originalPromptContent.Length, promptContent.Length, chatRequest.Parameters?.Count ?? 0);
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
                    
                    // 🎯 记录输入参数
                    if (chatRequest.Parameters != null && chatRequest.Parameters.Count > 0)
                    {
                        var parametersJson = JsonSerializer.Serialize(chatRequest.Parameters, _jsonSerializerOptions);
                        appActivity.SetTag("app.input.parameters", parametersJson);
                        _logger.LogDebug("📝 记录输入参数到 App Activity: {ParamCount} parameters", chatRequest.Parameters.Count);
                    }
                    
                    // 🎯 记录输入消息(包括替换后的 prompt)
                    if (chatRequest.Messages != null && chatRequest.Messages.Count > 0)
                    {
                        var inputMessagesJson = JsonSerializer.Serialize(
                            chatRequest.Messages.Select(m => new { 
                                role = ExtractRole(m), 
                                content = ExtractContent(m) 
                            }).ToList(),
                            _jsonSerializerOptions
                        );
                        appActivity.SetTag("app.input.messages", inputMessagesJson);
                        _logger.LogDebug("📝 记录输入消息到 App Activity: {MessageCount} messages", chatRequest.Messages.Count);
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

                        // 🎯 记录响应数据到 requestActivity
                        var reqActivity = GetRequestActivity();
                        if (reqActivity != null)
                        {
                            var lastMessage = aiResponse.Messages.LastOrDefault();
                            var responseContent = lastMessage?.Text ?? string.Empty;

                            reqActivity.SetTag("response.content", responseContent);
                        }
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
            catch (System.Net.Http.HttpRequestException httpEx)
            {
                // 🎯 处理 LLM API 服务的网络连接错误（如 Connection reset by peer）
                var errorMessage = "LLM API 服务连接失败";
                var isConnectionReset = httpEx.InnerException is System.Net.Sockets.SocketException socketEx 
                    && socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset;
                
                if (isConnectionReset)
                {
                    errorMessage = "LLM API 服务连接被重置，请稍后重试";
                    _logger.LogWarning(httpEx, "LLM API 连接被重置 (Connection reset by peer), Config: {ConfigName}, BaseUrl: {BaseUrl}", 
                        config?.Name, config?.BaseUrl);
                }
                else
                {
                    _logger.LogError(httpEx, "LLM API HTTP 请求失败, Config: {ConfigName}, BaseUrl: {BaseUrl}", 
                        config?.Name, config?.BaseUrl);
                }

                if (callRecord != null)
                {
                    await _callRecordService.MarkErrorAsync(callRecord, errorMessage);
                }

                // 🎯 记录网络异常状态
                requestActivity?.SetStatus(ActivityStatusCode.Error, errorMessage);
                requestActivity?.SetTag("error.type", "HttpRequestException");
                requestActivity?.SetTag("error.message", httpEx.Message);
                requestActivity?.SetTag("error.is_connection_reset", isConnectionReset);
                requestActivity?.SetTag("llm_api.base_url", config?.BaseUrl); // 🎯 记录出错的 API 地址
                if (httpEx.InnerException != null)
                {
                    requestActivity?.SetTag("error.inner_type", httpEx.InnerException.GetType().Name);
                    if (httpEx.InnerException is System.Net.Sockets.SocketException sockEx)
                    {
                        requestActivity?.SetTag("error.socket_error_code", sockEx.SocketErrorCode.ToString());
                    }
                }

                await WriteAssistantMessageAsync(null, $"网络错误: {errorMessage}");
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

                // 🎯 使用 LoadBalancerService 释放配置（仅在需要时）
                if (shouldReleaseConfig && !string.IsNullOrEmpty(config?.Id))
                {
                    _loadBalancer.ReleaseConfig(config.Id);
                    _logger.LogDebug("通过 LoadBalancerService 释放配置: {ConfigId}", config.Id);
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

            // 🎯 记录错误响应到 requestActivity
            var requestActivity = GetRequestActivity();
            if (requestActivity != null)
            {
                requestActivity.SetTag("response.error", message);
                requestActivity.SetTag("response.error_type", type);
                requestActivity.SetTag("response.status_code", (int)statusCode);
                _logger.LogDebug("📝 记录错误响应到 requestActivity: {ErrorType} - {Message}", type, message);
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

            // 🎯 记录助手消息响应到 requestActivity
            var requestActivity = GetRequestActivity();
            if (requestActivity != null)
            {
                requestActivity.SetTag("response.content", message);
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

                // 🎯 提前获取 requestActivity 供后续使用
                var reqActivity = GetRequestActivity();

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

                    // 🎯 记录 AgentGroup 流式响应数据到 requestActivity
                    if (reqActivity != null && !string.IsNullOrEmpty(result))
                    {
                        reqActivity.SetTag("response.content", result);
                    }

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

                        // 🎯 显式完成响应,确保 SSE 连接正确关闭
                        await Response.CompleteAsync();
                    }
                    catch { }

                    return;
                }

                // 非流式，直接调用并返回完整结果
                var nonStreamResult = await _agentOrchestrator.ExecuteAsync(app, userMessages);

                // 🎯 记录 AgentGroup 非流式响应数据到 requestActivity
                if (reqActivity != null && !string.IsNullOrEmpty(nonStreamResult))
                {
                    reqActivity.SetTag("response.content", nonStreamResult);
                }

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
                    aiMessages.Select(m => new { role = m.Role.ToString(), content = m.Text }).ToList(),
                    _jsonSerializerOptions
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

            // 🎯 如果有系统提示，先移除已有的 system 消息（避免重复），然后插入新的
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                // 移除已有的 system 消息
                aiMessages = aiMessages.Where(m => m.Role != ChatRole.System).ToList();
                // 插入替换后的 system prompt
                aiMessages.Insert(0, new AIChatMessage(ChatRole.System, systemPrompt));
            }

            // 🎯 记录输入消息到 Activity (用于追踪)
            var currentActivity = Activity.Current;
            if (currentActivity != null && aiMessages.Any())
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,               // 保持换行缩进
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 不转义中文、<> 等
                };
                var inputMessagesJson = JsonSerializer.Serialize(
                    aiMessages.Select(m => new { role = m.Role.ToString(), content = m.Text }).ToList(),
                    jsonOptions
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

            // 设置 SSE 响应头
            Response.StatusCode = (int)HttpStatusCode.OK;
            Response.ContentType = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["X-Accel-Buffering"] = "no";  // 禁用 Nginx 缓冲

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
            
            // 🎯 记录流式响应的交互序列 (text-tool-text-tool-text)
            // 支持表达一个 chunk 中的多个并行工具调用
            var interactionSequence = new List<object>();

            // 🎯 AOP 优化: ActivityCaptureAttribute 已在 Action 执行前自动保存 Activity
            // 无需在 foreach 中手动捕获

            try
            {
                await foreach (var update in chatClient.GetStreamingResponseAsync(aiMessages, options, cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    // 🎯 收集当前 chunk 的所有工具调用
                    var currentChunkToolCalls = new List<object>();
                    
                    // 处理文本内容
                    var text = update.Text ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        fullContent.Append(text);
                        
                        // 🎯 记录文本片段到交互序列
                        interactionSequence.Add(new { type = "text", content = text });

                        // 🎯 使用强类型 OpenAI DTO
                        var chunk = new ChatCompletionChunk
                        {
                            Id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                            Object = "chat.completion.chunk",
                            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Model = modelName ?? "unknown",
                            Choices = new List<ChatCompletionChunkChoice>
                            {
                                new ChatCompletionChunkChoice
                                {
                                    Index = 0,
                                    Delta = new ChatCompletionChunkDelta
                                    {
                                        Role = firstChunk ? "assistant" : null,
                                        Content = text
                                    },
                                    FinishReason = null
                                }
                            }
                        };

                        var json = JsonSerializer.Serialize(chunk, _jsonSerializerOptions);
                        await Response.WriteAsync($"data: {json}\n\n", cancellationToken: cancellationToken);
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
                                
                                // 🎯 收集到当前 chunk 的工具调用列表
                                currentChunkToolCalls.Add(new { 
                                    name = functionCall.Name,
                                    call_id = callId,
                                    arguments = argumentsJson
                                });

                                // 🎯 使用强类型 OpenAI DTO 构建工具调用
                                var chunk = new ChatCompletionChunk
                                {
                                    Id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                                    Object = "chat.completion.chunk",
                                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                    Model = modelName ?? "unknown",
                                    Choices = new List<ChatCompletionChunkChoice>
                                    {
                                        new ChatCompletionChunkChoice
                                        {
                                            Index = 0,
                                            Delta = new ChatCompletionChunkDelta
                                            {
                                                Role = firstChunk ? "assistant" : null,
                                                ToolCalls = new List<ChatCompletionChunkToolCall>
                                                {
                                                    new ChatCompletionChunkToolCall
                                                    {
                                                        Index = 0,
                                                        Id = callId,
                                                        Type = "function",
                                                        Function = new ChatCompletionChunkToolCallFunction
                                                        {
                                                            Name = functionCall.Name,
                                                            Arguments = argumentsJson
                                                        }
                                                    }
                                                }
                                            },
                                            FinishReason = null
                                        }
                                    }
                                };

                                var toolCallJson = JsonSerializer.Serialize(chunk, _jsonSerializerOptions);
                                await Response.WriteAsync($"data: {toolCallJson}\n\n", cancellationToken: cancellationToken);
                                await Response.Body.FlushAsync(cancellationToken);

                                firstChunk = false;
                            }
                        }
                    }
                    
                    // 🎯 如果当前 chunk 有工具调用,将它们作为一组添加到交互序列
                    if (currentChunkToolCalls.Count > 0)
                    {
                        if (currentChunkToolCalls.Count == 1)
                        {
                            // 单个工具调用,直接添加
                            var singleTool = currentChunkToolCalls[0];
                            interactionSequence.Add(new { 
                                type = "tool_call",
                                tool = singleTool
                            });
                        }
                        else
                        {
                            // 多个工具调用,表示并行执行
                            interactionSequence.Add(new { 
                                type = "parallel_tool_calls",
                                count = currentChunkToolCalls.Count,
                                tools = currentChunkToolCalls.ToArray()
                            });
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
                
                // 🎯 记录完整的交互序列到 Activity (展示 text-tool-text 的穿插顺序)
                if (currentActivity != null && interactionSequence.Count > 0)
                {
                    var interactionJson = JsonSerializer.Serialize(interactionSequence, _jsonSerializerOptions);
                    currentActivity.SetTag("gen_ai.interaction_sequence", interactionJson);
                    _logger.LogDebug("📝 记录交互序列到 Activity: {Count} items", interactionSequence.Count);
                    
                    // 🎯 同时记录到父 Activity (如果是 app 调用,父级就是 appActivity)
                    if (currentActivity.Parent != null)
                    {
                        currentActivity.Parent.SetTag("app.output.interaction_sequence", interactionJson);
                        _logger.LogDebug("📝 记录交互序列到父 Activity: {Count} items", interactionSequence.Count);
                    }
                }

                // 🎯 记录流式响应数据到 requestActivity
                var requestActivity = GetRequestActivity();
                if (requestActivity != null && !string.IsNullOrEmpty(fullContentText))
                {
                    requestActivity.SetTag("response.content", fullContentText);
                }

                // 🎯 使用强类型发送结束标记
                var finalChunk = new ChatCompletionChunk
                {
                    Id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                    Object = "chat.completion.chunk",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = modelName ?? "unknown",
                    Choices = new List<ChatCompletionChunkChoice>
                {
                    new ChatCompletionChunkChoice
                    {
                        Index = 0,
                        Delta = new ChatCompletionChunkDelta(),
                        FinishReason = "stop"
                    }
                }
                };

                var finalJson = JsonSerializer.Serialize(finalChunk, _jsonSerializerOptions);
                await Response.WriteAsync($"data: {finalJson}\n\n");
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync(cancellationToken);

                // 🎯 显式完成响应,确保 SSE 连接正确关闭
                await Response.CompleteAsync();

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
            if (lastMessage == null)
            {
                await Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                    Object = "chat.completion",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = modelName,
                    choices = Array.Empty<object>(),
                    usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
                }, _jsonSerializerOptions));
                return;
            }

            var role = lastMessage.Role.Value.ToLowerInvariant();

            // 🎯 构建 OpenAI 格式的 message 对象
            var messageObj = BuildOpenAIMessage(lastMessage);

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
                        message = messageObj,
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

        /// <summary>
        /// 将 ChatMessage 转换为 OpenAI 格式的 message 对象
        /// 保留原始的 content 结构(text, tool_calls, images 等)
        /// </summary>
        private object BuildOpenAIMessage(AIChatMessage message)
        {
            var role = message.Role.Value.ToLowerInvariant();

            // 如果没有 Contents，只返回文本
            if (message.Contents == null || !message.Contents.Any())
            {
                return new
                {
                    role = role,
                    content = message.Text ?? string.Empty
                };
            }

            // 🎯 检查是否有工具调用
            var functionCalls = message.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>().ToList();
            if (functionCalls.Any())
            {
                // 构建 tool_calls 数组
                var toolCalls = functionCalls.Select(fc => new
                {
                    id = fc.CallId ?? $"call_{Guid.NewGuid():N}",
                    type = "function",
                    function = new
                    {
                        name = fc.Name,
                        arguments = fc.Arguments != null && fc.Arguments.Count > 0
                            ? JsonSerializer.Serialize(fc.Arguments, _jsonSerializerOptions)
                            : "{}"
                    }
                }).ToArray();

                // 提取文本内容(如果有)
                var textContent = message.Contents
                    .OfType<Microsoft.Extensions.AI.TextContent>()
                    .Select(tc => tc.Text)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .FirstOrDefault();

                return new
                {
                    role = role,
                    content = textContent ?? null,
                    tool_calls = toolCalls
                };
            }

            // 🎯 检查是否是工具结果消息(role=tool)
            var functionResults = message.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().ToList();
            if (role == "tool" && functionResults.Any())
            {
                // 工具结果消息格式
                var firstResult = functionResults.First();
                return new
                {
                    role = "tool",
                    tool_call_id = firstResult.CallId ?? string.Empty,
                    content = firstResult.Result?.ToString() ?? string.Empty
                };
            }

            // 🎯 多模态内容(多个文本片段或其他内容类型)
            var textContents = message.Contents.OfType<Microsoft.Extensions.AI.TextContent>().ToList();
            if (textContents.Count > 1)
            {
                // 多个文本片段,构建 content 数组
                var contentArray = textContents
                    .Where(tc => !string.IsNullOrEmpty(tc.Text))
                    .Select(tc => new
                    {
                        type = "text",
                        text = tc.Text
                    })
                    .ToArray();

                return new
                {
                    role = role,
                    content = contentArray.Length > 0 ? (object)contentArray : string.Empty
                };
            }

            // 🎯 其他未知的 Content 类型,尝试序列化
            if (message.Contents.Count > 0)
            {
                var contentArray = new List<object>();

                foreach (var content in message.Contents)
                {
                    if (content is Microsoft.Extensions.AI.TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        contentArray.Add(new
                        {
                            type = "text",
                            text = textContent.Text
                        });
                    }
                    else
                    {
                        // 其他类型,转换为文本
                        var contentStr = content.ToString();
                        if (!string.IsNullOrEmpty(contentStr))
                        {
                            contentArray.Add(new
                            {
                                type = "text",
                                text = contentStr
                            });
                        }
                    }
                }

                if (contentArray.Count > 0)
                {
                    return new
                    {
                        role = role,
                        content = contentArray.Count == 1 && contentArray[0].GetType().GetProperty("text") != null
                            ? contentArray[0].GetType().GetProperty("text")?.GetValue(contentArray[0])
                            : (object)contentArray.ToArray()
                    };
                }
            }

            // 🎯 单一文本内容(默认情况)
            return new
            {
                role = role,
                content = message.Text ?? string.Empty
            };
        }
    }
}