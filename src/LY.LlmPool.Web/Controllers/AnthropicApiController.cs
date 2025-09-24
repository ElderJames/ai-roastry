using Microsoft.AspNetCore.Mvc;
using LY.LlmPool.Web.Models.Anthropic;
using LY.LlmPool.Web.Services;
using System.Text.Json;
using System.Text;
using System.Text.Encodings.Web;

namespace LY.LlmPool.Web.Controllers;

/// <summary>
/// Anthropic API 兼容控制器
/// </summary>
[ApiController]
[Route("v1")]
public class AnthropicApiController : ControllerBase
{
    private readonly LlmPoolService _llmPoolService;
    private readonly ChatClientService _chatClientService;
    private readonly AnthropicTransformService _transformService;
    private readonly CallRecordService _callRecordService;
    private readonly ILogger<AnthropicApiController> _logger;

    public AnthropicApiController(
        LlmPoolService llmPoolService,
        ChatClientService chatClientService,
        AnthropicTransformService transformService,
        CallRecordService callRecordService,
        ILogger<AnthropicApiController> logger)
    {
        _llmPoolService = llmPoolService;
        _chatClientService = chatClientService;
        _transformService = transformService;
        _callRecordService = callRecordService;
        _logger = logger;
    }

    /// <summary>
    /// 创建消息 - Anthropic API 兼容端点
    /// </summary>
    [HttpPost("messages")]
    public async Task<IActionResult> CreateMessage()
    {
        try
        {
            var requestStartTime = DateTime.UtcNow;
            object? requestData = null;
            // 手动读取和解析 JSON
            using var reader = new StreamReader(Request.Body);
            var requestBody = await reader.ReadToEndAsync();
            
            _logger.LogInformation("Received raw request body: {RequestBody}", requestBody);

            if (string.IsNullOrEmpty(requestBody))
            {
                return BadRequest(new { error = new { message = "Request body is empty", type = "invalid_request_error" } });
            }

            // 使用更宽松的 JSON 选项
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            MessagesRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<MessagesRequest>(requestBody, jsonOptions);
                if (request == null)
                {
                    return BadRequest(new { error = new { message = "Failed to parse request", type = "invalid_request_error" } });
                }
                try
                {
                    // 存储原始请求数据以供观察
                    requestData = JsonSerializer.Deserialize<object>(requestBody, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                }
                catch { }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON deserialization failed for request: {RequestBody}", requestBody);
                return BadRequest(new { error = new { message = $"Invalid JSON format: {ex.Message}", type = "invalid_request_error" } });
            }

            // 记录原始模型名称
            request.OriginalModel = request.Model;

            // 从请求头中获取API Key
            if (!Request.Headers.TryGetValue("x-api-key", out var apiKey) ||
                string.IsNullOrEmpty(apiKey))
            {
                return BadRequest(new { error = new { message = "Missing or invalid API key", type = "invalid_request_error" } });
            }

            // 根据apiKey作为 key 查找 endpoint 下可用的 config（并尝试占用）
            var modelAcquireStartTime = DateTime.UtcNow;
            var config = await _llmPoolService.GetAvailableConfigByKeyAsync(apiKey.ToString());
            if (config == null)
            {
                return BadRequest(new { error = new { message = $"API key '{apiKey}' not found or not available", type = "invalid_request_error" } });
            }

            // 解析所属 endpoint
            var endpointForCfg = await _llmPoolService.GetEndpointForConfigAsync(config.Id!);
            var endpointId = endpointForCfg?.Id ?? apiKey.ToString();

            _logger.LogInformation("Processing message request: {OriginalModel} -> EndpointId: {EndpointId}, Config: {ConfigName}", 
                request.OriginalModel, endpointId, config.Name);

            // 创建调用记录并记录等待时间与选择信息
            var callRecord = await _callRecordService.CreateAsync(endpointId, requestData);
            var waitTime = DateTime.UtcNow - modelAcquireStartTime;
            await _callRecordService.UpdateWaitAsync(callRecord, waitTime);
            await _callRecordService.UpdateConfigAsync(callRecord, config.Id);

            // 转换请求格式
            var messages = _transformService.ConvertToInternalMessages(request);

            if (request.Stream == true)
            {
                // 流式响应
                // 记录开始与首包时间点
                await _callRecordService.MarkStartAsync(callRecord);

                // 设置 SSE 头
                Response.Headers["Transfer-Encoding"] = "chunked";
                Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
                Response.Headers["Cache-Control"] = "no-cache";
                Response.Headers["Connection"] = "keep-alive";
                Response.Headers["X-Accel-Buffering"] = "no";

                // 标记响应开始
                await _callRecordService.MarkResponseStartAsync(callRecord);

                var result = await HandleStreamingCore(request, config, messages, callRecord, endpointId, requestStartTime);
                return result;
            }
            else
            {
                // 非流式响应
                await _callRecordService.MarkStartAsync(callRecord);
                await _callRecordService.MarkResponseStartAsync(callRecord);

                var response = await _chatClientService.SendMessageAsync(config, messages);
                var anthropicResponse = _transformService.ConvertToAnthropicResponse(response, request);
                
                _logger.LogInformation("Message request completed successfully");

                // 提取文本消息用于观测
                string? msgText = null;
                try
                {
                    if (anthropicResponse?.Content != null && anthropicResponse.Content.Count > 0)
                    {
                        var texts = anthropicResponse.Content
                            .Where(c => string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
                            .Select(c => c.Text ?? string.Empty);
                        msgText = string.Join("", texts);
                    }
                }
                catch { }

                await _callRecordService.WriteSelectionAsync(
                    callRecord,
                    selectionStrategy: "endpoint-id-lb",
                    endpointId: endpointId,
                    configId: config.Id,
                    configName: config.Name,
                    appName: null,
                    model: config.Model,
                    message: msgText,
                    toolCalls: null,
                    requestReceivedAt: requestStartTime);

                await _callRecordService.FinalizeAsync(
                    callRecord,
                    selectionStrategy: "endpoint-id-lb",
                    endpointId: endpointId,
                    configId: config.Id,
                    configName: config.Name,
                    appName: null,
                    model: config.Model,
                    requestReceivedAt: requestStartTime);
                return Ok(anthropicResponse);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message request");
            return StatusCode(500, new { error = new { message = ex.Message, type = "internal_server_error" } });
        }
    }

    /// <summary>
    /// 计算Token数量 - Anthropic API 兼容端点
    /// </summary>
    [HttpPost("messages/count_tokens")]
    public async Task<IActionResult> CountTokens()
    {
        try
        {
            // 手动读取和解析 JSON
            using var reader = new StreamReader(Request.Body);
            var requestBody = await reader.ReadToEndAsync();
            
            _logger.LogInformation("Received token count request body: {RequestBody}", requestBody);

            if (string.IsNullOrEmpty(requestBody))
            {
                return BadRequest(new { error = new { message = "Request body is empty", type = "invalid_request_error" } });
            }

            // 使用更宽松的 JSON 选项
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            TokenCountRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<TokenCountRequest>(requestBody, jsonOptions);
                if (request == null)
                {
                    return BadRequest(new { error = new { message = "Failed to parse request", type = "invalid_request_error" } });
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON deserialization failed for token count request: {RequestBody}", requestBody);
                return BadRequest(new { error = new { message = $"Invalid JSON format: {ex.Message}", type = "invalid_request_error" } });
            }

            // 记录原始模型名称
            request.OriginalModel = request.Model;

            // 从请求头中获取API Key
            if (!Request.Headers.TryGetValue("x-api-key", out var apiKey) ||
                string.IsNullOrEmpty(apiKey))
            {
                return BadRequest(new { error = new { message = "Missing or invalid API key", type = "invalid_request_error" } });
            }

            // 根据apiKey作为 key 查找 endpoint config
            var endpoint = await _llmPoolService.GetAvailableConfigByKeyAsync(apiKey);
            if (endpoint == null)
            {
                return BadRequest(new { error = new { message = $"API key '{apiKey}' not found", type = "invalid_request_error" } });
            }

            _logger.LogInformation("Processing token count request: {OriginalModel} -> Endpoint: {EndpointName}", 
                request.OriginalModel, endpoint.Name);

            var response = _transformService.ConvertToTokenCountResponse(request);
            
            _logger.LogInformation("Token count request completed: {InputTokens} tokens", response.InputTokens);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting tokens");
            return StatusCode(500, new { error = new { message = ex.Message, type = "internal_server_error" } });
        }
    }

    /// <summary>
    /// 处理流式请求
    /// </summary>
    private async Task<IActionResult> HandleStreamingRequest(MessagesRequest request, Data.Entities.LlmConfig endpoint, List<Models.ChatMessage> messages)
    {
        // 已重构为 HandleStreamingCore 并在 CreateMessage 中设置时间点与选择信息
        return await Task.FromResult(new EmptyResult());
    }

    private async Task<IActionResult> HandleStreamingCore(MessagesRequest request, Data.Entities.LlmConfig config, List<Models.ChatMessage> messages,
        Data.Entities.EndpointCallRecord callRecord, string endpointId, DateTime requestStartTime)
    {
        try
        {
            var responseStream = Response.Body;
            var cancellationToken = HttpContext.RequestAborted;

            var streamingResponse = _chatClientService.SendStreamingMessageAsync(config, messages);
            var streamEvents = _transformService.ConvertToStreamEventsAsync(streamingResponse, request);

            await foreach (var streamEvent in streamEvents.WithCancellation(cancellationToken))
            {
                var eventData = SerializeStreamEvent(streamEvent);
                var eventBytes = Encoding.UTF8.GetBytes(eventData);
                await responseStream.WriteAsync(eventBytes, cancellationToken);
                await responseStream.FlushAsync(cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            var doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await responseStream.WriteAsync(doneBytes, cancellationToken);
            await responseStream.FlushAsync(cancellationToken);

            await _callRecordService.FinalizeAsync(
                callRecord,
                selectionStrategy: "endpoint-id-lb",
                endpointId: endpointId,
                configId: config.Id,
                configName: config.Name,
                appName: null,
                model: config.Model,
                requestReceivedAt: requestStartTime);

            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Streaming request was cancelled");
            await _callRecordService.MarkErrorAsync(callRecord, "client_canceled");
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in streaming response");
            try
            {
                var errorEvent = CreateErrorStreamEvent(ex.Message);
                var errorData = SerializeStreamEvent(errorEvent);
                var errorBytes = Encoding.UTF8.GetBytes(errorData);
                await Response.Body.WriteAsync(errorBytes);
                await Response.Body.FlushAsync();
            }
            catch { }

            await _callRecordService.MarkErrorAsync(callRecord, ex.Message);
            return new EmptyResult();
        }
        finally
        {
            if (!string.IsNullOrEmpty(config.Id))
            {
                _llmPoolService.ReleaseConfig(config.Id);
            }
        }
    }

    /// <summary>
    /// 序列化流事件为 Server-Sent Events 格式
    /// </summary>
    private string SerializeStreamEvent(StreamEvent streamEvent)
    {
        var eventType = streamEvent.Type.ToString().ToLower();
        var eventData = JsonSerializer.Serialize(streamEvent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        return $"event: {eventType}\ndata: {eventData}\n\n";
    }

    /// <summary>
    /// 创建错误流事件
    /// </summary>
    private StreamEvent CreateErrorStreamEvent(string message)
    {
        return new MessageDeltaEvent
        {
            Delta = new Dictionary<string, object>
            {
                ["stop_reason"] = "error"
            },
            Usage = new Usage { InputTokens = 0, OutputTokens = 0 }
        };
    }

    /// <summary>
    /// 健康检查端点 - 简化版本
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            // 简单检查服务是否可用
            var configs = await _llmPoolService.GetConfigsAsync();
            var hasEnabledConfigs = configs.Any(c => c.IsEnabled);
            
            if (hasEnabledConfigs)
            {
                return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
            }
            else
            {
                return StatusCode(503, new { status = "unhealthy", message = "No enabled configurations", timestamp = DateTime.UtcNow });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return StatusCode(503, new { 
                status = "unhealthy", 
                error = ex.Message, 
                timestamp = DateTime.UtcNow 
            });
        }
    }

    /// <summary>
    /// API信息端点 - 返回服务信息
    /// </summary>
    [HttpGet("/api/info")]
    public IActionResult ApiInfo()
    {
        return Ok(new { 
            message = "LLM Pool - Anthropic API Compatible Endpoint",
            version = "1.0.0",
            timestamp = DateTime.UtcNow
        });
    }
}
