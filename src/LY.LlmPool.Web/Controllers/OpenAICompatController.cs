using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// Alias to avoid ambiguity
using AgentOrchestratorServiceAlias = LY.LlmPool.Web.Services.Agents.AgentOrchestratorService;

namespace LY.LlmPool.Web.Controllers
{
    [ApiController]
    [Route("v1")]
    public class OpenAICompatController : ControllerBase
    {
        private readonly LlmPoolService _llmPoolService;
        private readonly CallRecordService _callRecordService;
        private readonly PromptParameterService _promptParameterService;
        private readonly ILogger<OpenAICompatController> _logger;
        private readonly ILogger<LoggingHttpHandler> _httpLogger;
        private readonly AgentOrchestratorServiceAlias _agentOrchestrator;
        private readonly IHttpClientFactory _httpClientFactory;

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
            ILogger<OpenAICompatController> logger,
            ILogger<LoggingHttpHandler> httpLogger,
            AgentOrchestratorServiceAlias agentOrchestrator,
            IHttpClientFactory httpClientFactory)
        {
            _llmPoolService = llmPoolService;
            _callRecordService = callRecordService;
            _promptParameterService = promptParameterService;
            _logger = logger;
            _httpLogger = httpLogger;
            _agentOrchestrator = agentOrchestrator;
            _httpClientFactory = httpClientFactory;
        }

        [HttpPost("chat/completions")]
        public async Task ChatCompletions()
        {
            await ProcessChatCompletions();
        }

        private async Task ProcessChatCompletions()
        {
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

                _logger.LogInformation("解析的聊天请求 - 模型: {Model}, 消息数量: {MessageCount}", chatRequest.Model, chatRequest.Messages.Count);

                var modelAcquireStartTime = DateTime.UtcNow;
                LlmConfig? config = null;
                LlmApp? app = null;
                string? promptContent = null;
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

                    // AgentGroup 应用走编排分支
                    if (string.Equals(app.AppType, "AgentGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("App {AppName} 为 AgentGroup 类型，进入编排分支。", app.Name);
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
                        var prompt = await _llmPoolService.GetPromptByIdAsync(app.PromptId);
                        if (prompt != null)
                        {
                            promptContent = prompt.Content;
                        }
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
                    if (callRecord != null)
                    {
                        callRecord.IsSuccessful = false;
                        callRecord.ErrorMessage = "No available model found or invalid API key";
                        await _llmPoolService.UpdateCallRecordAsync(callRecord);
                    }

                    Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    _logger.LogError("No available model found or invalid API key");
                    var err = new { error = new { message = "未找到可用模型或 API Key 无效。" } };
                    await Response.WriteAsync(JsonSerializer.Serialize(err, _jsonSerializerOptions));
                    return;
                }

                if (callRecord != null)
                {
                    await _callRecordService.UpdateConfigAsync(callRecord, config.Id);
                    await _callRecordService.MarkStartAsync(callRecord);
                }

                // Note: tests should override the named HttpClient 'UpstreamLlm' to avoid real upstream requests.

                // 参数替换（仅当存在应用 Prompt）
                if (!string.IsNullOrEmpty(promptContent) && app != null && chatRequest.Parameters != null && chatRequest.Parameters.Count > 0)
                {
                    var missingParams = _promptParameterService.ValidateParameters(promptContent, chatRequest.Parameters);
                    if (missingParams.Count > 0)
                    {
                        if (callRecord != null)
                        {
                            callRecord.IsSuccessful = false;
                            callRecord.ErrorMessage = $"Missing required parameters: {string.Join(", ", missingParams)}";
                            await _llmPoolService.UpdateCallRecordAsync(callRecord);
                        }
                        await WriteAssistantMessageAsync(chatRequest.Model, $"缺少必要参数: {string.Join(", ", missingParams)}");
                        return;
                    }

                    var originalPromptContent = promptContent;
                    promptContent = _promptParameterService.ReplaceParameters(promptContent, chatRequest.Parameters);
                    _logger.LogInformation("参数替换完成 - 原始长度: {OriginalLength}, 替换后长度: {NewLength}, 参数数量: {ParamCount}", originalPromptContent.Length, promptContent.Length, chatRequest.Parameters.Count);
                }

                // 重写并上游转发（先进行一次 DOM 归一化，确保 tool_calls.arguments 空串→"{}"）
                var normalizedBody = NormalizeEmptyToolArguments(requestBody ?? "{}");
                using var doc = JsonDocument.Parse(normalizedBody ?? "{}");
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();

                    bool modelWritten = false;
                    bool messagesWritten = false;

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "model", StringComparison.OrdinalIgnoreCase))
                        {
                            writer.WriteString("model", actualModelName ?? chatRequest.Model);
                            modelWritten = true;
                            continue;
                        }

                        if (string.Equals(prop.Name, "parameters", StringComparison.OrdinalIgnoreCase))
                        {
                            // 不透传自定义 parameters
                            continue;
                        }

                        if (string.Equals(prop.Name, "messages", StringComparison.OrdinalIgnoreCase))
                        {
                            writer.WritePropertyName("messages");
                            writer.WriteStartArray();

                            if (!string.IsNullOrEmpty(promptContent))
                            {
                                writer.WriteStartObject();
                                writer.WriteString("role", "system");
                                writer.WriteString("content", promptContent);
                                writer.WriteEndObject();
                            }

                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in prop.Value.EnumerateArray())
                                {
                                    WriteNormalizedMessage(writer, item);
                                }
                            }
                            else
                            {
                                // 单对象也尝试归一化
                                WriteNormalizedMessage(writer, prop.Value);
                            }

                            writer.WriteEndArray();
                            messagesWritten = true;
                            continue;
                        }

                        writer.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(writer);
                    }

                    if (!modelWritten)
                    {
                        writer.WriteString("model", actualModelName ?? chatRequest.Model);
                    }

                    if (!messagesWritten && !string.IsNullOrEmpty(promptContent))
                    {
                        writer.WritePropertyName("messages");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", promptContent);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                var upstreamBody = Encoding.UTF8.GetString(ms.ToArray());

                using var httpClient = _httpClientFactory.CreateClient("UpstreamLlm");

                var baseUri = new Uri(config.BaseUrl);
                var path = baseUri.AbsolutePath.TrimEnd('/')  + "/chat/completions"; ;

                var ubFinal = new UriBuilder(baseUri)
                {
                    Path = path
                };
                // 保留 BaseUrl 上的查询串（例如 Azure OpenAI 的 api-version）
                if (!string.IsNullOrEmpty(baseUri.Query))
                {
                    ubFinal.Query = baseUri.Query.TrimStart('?');
                }
                var upstreamUri = ubFinal.Uri;

                using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
                {
                    Content = new StringContent(upstreamBody, Encoding.UTF8, "application/json")
                };

                upstreamRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);

                bool hasAccept = false;
                if (Request.Headers.TryGetValue("Accept", out var accept))
                {
                    hasAccept = true;
                    upstreamRequest.Headers.TryAddWithoutValidation("Accept", (IEnumerable<string>)accept);
                }
                if (!hasAccept && (chatRequest.Stream == true))
                {
                    upstreamRequest.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                }

                // 透传配置中的额外请求头（跳过已设置和敏感头）
                if (config.AdditionalHeaders != null && config.AdditionalHeaders.Count > 0)
                {
                    foreach (var kv in config.AdditionalHeaders)
                    {
                        var key = kv.Key?.Trim();
                        var value = kv.Value;
                        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;

                        if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                        {
                            upstreamRequest.Content?.Headers.TryAddWithoutValidation(key, value);
                        }
                        else
                        {
                            upstreamRequest.Headers.TryAddWithoutValidation(key, value);
                        }
                    }
                }

                _logger.LogInformation("代理请求到上游: {Uri}", upstreamUri);

                using var upstreamResponse = await httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

                Response.StatusCode = (int)upstreamResponse.StatusCode;
                if (upstreamResponse.Content.Headers.ContentType != null)
                {
                    Response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();
                }
                // 对于 SSE，尽早开始响应并避免缓存
                if (!string.IsNullOrEmpty(Response.ContentType) && Response.ContentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    Response.Headers["Cache-Control"] = "no-cache";
                    await Response.StartAsync(HttpContext.RequestAborted);
                }

                if (callRecord != null)
                {
                    await _callRecordService.MarkResponseStartAsync(callRecord);
                }

                try
                {
                    using (var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(HttpContext.RequestAborted))
                    {
                        var buffer = new byte[8192];
                        int read;
                        while ((read = await upstreamStream.ReadAsync(buffer, 0, buffer.Length, HttpContext.RequestAborted)) > 0)
                        {
                            await Response.Body.WriteAsync(buffer.AsMemory(0, read), HttpContext.RequestAborted);
                            await Response.Body.FlushAsync(HttpContext.RequestAborted);

                            // Log and persist this chunk for streaming call record
                            try
                            {
                                var chunkText = Encoding.UTF8.GetString(buffer, 0, read);
                                _logger.LogInformation("[Stream {CallId}] {Len} bytes:\n{Chunk}", callRecord?.Id, read, chunkText);
                                if (callRecord != null)
                                {
                                    await _callRecordService.AppendStreamEventAsync(callRecord, chunkText);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation("Client disconnected during streaming (canceled).");
                    return;
                }
                catch (IOException ioEx) when (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    _logger.LogInformation(ioEx, "Client disconnected during streaming (IO).");
                    return;
                }

                if (callRecord != null)
                {
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
                return;
            }
            catch (Exception ex)
            {
                if (callRecord != null)
                {
                    await _callRecordService.MarkErrorAsync(callRecord, ex.Message);
                }
                _logger.LogError(ex, "Error processing request");
                await WriteAssistantMessageAsync(null, "服务器内部错误。");
            }
            finally
            {
                if (Request != null)
                {
                    // 释放占用的配置
                    // 注意：只有通过 AcquireConfigIfAvailable 成功占用的才需要释放
                    // 这里简化：若解析到了 config.Id 则尝试释放
                }
            }
        }

        private static void WriteNormalizedMessage(Utf8JsonWriter writer, JsonElement message)
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                message.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (var p in message.EnumerateObject())
            {
                if (string.Equals(p.Name, "tool_calls", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("tool_calls");
                    writer.WriteStartArray();
                    foreach (var tc in p.Value.EnumerateArray())
                    {
                        if (tc.ValueKind == JsonValueKind.Object)
                        {
                            writer.WriteStartObject();
                            foreach (var tp in tc.EnumerateObject())
                            {
                                if (string.Equals(tp.Name, "function", StringComparison.OrdinalIgnoreCase) && tp.Value.ValueKind == JsonValueKind.Object)
                                {
                                    writer.WritePropertyName("function");
                                    writer.WriteStartObject();
                                    foreach (var fp in tp.Value.EnumerateObject())
                                    {
                                        if (string.Equals(fp.Name, "arguments", StringComparison.OrdinalIgnoreCase) && fp.Value.ValueKind == JsonValueKind.String)
                                        {
                                            var argsText = fp.Value.GetString();
                                            writer.WriteString("arguments", string.IsNullOrWhiteSpace(argsText) ? "{}" : argsText);
                                        }
                                        else
                                        {
                                            writer.WritePropertyName(fp.Name);
                                            fp.Value.WriteTo(writer);
                                        }
                                    }
                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    writer.WritePropertyName(tp.Name);
                                    tp.Value.WriteTo(writer);
                                }
                            }
                            writer.WriteEndObject();
                        }
                        else
                        {
                            tc.WriteTo(writer);
                        }
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WritePropertyName(p.Name);
                    p.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
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
                var userMessages = chatRequest.Messages.Select(m => new ChatMessage
                {
                    Role = m.Role,
                    Content = m.Content
                }).ToList();

                // 若请求需要流式返回（SSE），通过 onProgress 回调写 SSE 数据
                if (chatRequest.Stream == true)
                {
                    Response.StatusCode = (int)HttpStatusCode.OK;
                    Response.ContentType = "text/event-stream";
                    Response.Headers.Add("Cache-Control", "no-cache");

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
            public List<Message> Messages { get; set; } = new();
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

        private class Message
        {
            public string Role { get; set; } = string.Empty;
            [JsonPropertyName("content")] public JsonElement ContentElement { get; set; }
            [JsonIgnore]
            public string Content
            {
                get
                {
                    if (ContentElement.ValueKind == JsonValueKind.String)
                        return ContentElement.GetString() ?? string.Empty;
                    if (ContentElement.ValueKind == JsonValueKind.Array)
                    {
                        var textContent = "";
                        foreach (var item in ContentElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var typeElement) &&
                                typeElement.GetString() == "text" &&
                                item.TryGetProperty("text", out var textElement))
                            {
                                textContent += textElement.GetString();
                            }
                        }
                        return textContent;
                    }
                    return string.Empty;
                }
            }
        }

        private static string? NormalizeEmptyToolArguments(string body)
        {
            try
            {
                var node = JsonNode.Parse(body);
                if (node is JsonObject root)
                {
                    if (root["choices"] is JsonArray choices)
                    {
                        foreach (var choice in choices.OfType<JsonObject>())
                        {
                            if (choice["message"] is JsonObject message)
                            {
                                if (message["tool_calls"] is JsonArray toolCalls)
                                {
                                    foreach (var tc in toolCalls.OfType<JsonObject>())
                                    {
                                        if (tc["function"] is JsonObject fn)
                                        {
                                            var argsNode = fn["arguments"];
                                            if (argsNode is JsonValue jv && jv.TryGetValue<string>(out var s))
                                            {
                                                if (string.IsNullOrWhiteSpace(s))
                                                {
                                                    fn["arguments"] = "{}";
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                return node?.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? body;
            }
            catch
            {
                return body;
            }
        }
    }
}
