using Microsoft.AspNetCore.Mvc;
using LY.LlmPool.Web.Models.Anthropic;
using LY.LlmPool.Web.Services;
using System.Text.Json;
using System.Text;

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
    private readonly ILogger<AnthropicApiController> _logger;

    public AnthropicApiController(
        LlmPoolService llmPoolService,
        ChatClientService chatClientService,
        AnthropicTransformService transformService,
        ILogger<AnthropicApiController> logger)
    {
        _llmPoolService = llmPoolService;
        _chatClientService = chatClientService;
        _transformService = transformService;
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

            // 根据apiKey作为 key 查找 endpoint config
            var endpoint = await _llmPoolService.GetAvailableConfigByKeyAsync(apiKey);
            if (endpoint == null)
            {
                return BadRequest(new { error = new { message = $"API key '{apiKey}' not found or not available", type = "invalid_request_error" } });
            }

            _logger.LogInformation("Processing message request: {OriginalModel} -> Endpoint: {EndpointName}", 
                request.OriginalModel, endpoint.Name);

            // 转换请求格式
            var messages = _transformService.ConvertToInternalMessages(request);

            if (request.Stream == true)
            {
                // 流式响应
                return await HandleStreamingRequest(request, endpoint, messages);
            }
            else
            {
                // 非流式响应
                var response = await _chatClientService.SendMessageAsync(endpoint, messages);
                var anthropicResponse = _transformService.ConvertToAnthropicResponse(response, request);
                
                _logger.LogInformation("Message request completed successfully");
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
        try
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["X-Accel-Buffering"] = "no"; // 禁用nginx缓冲

            var responseStream = Response.Body;
            var cancellationToken = HttpContext.RequestAborted;

            // 获取流式响应
            var streamingResponse = _chatClientService.SendStreamingMessageAsync(endpoint, messages);
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

            // 发送结束标记
            var doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await responseStream.WriteAsync(doneBytes, cancellationToken);
            await responseStream.FlushAsync(cancellationToken);

            return new EmptyResult();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Streaming request was cancelled");
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in streaming response");
            
            // 尝试发送错误事件
            try
            {
                var errorEvent = CreateErrorStreamEvent(ex.Message);
                var errorData = SerializeStreamEvent(errorEvent);
                var errorBytes = Encoding.UTF8.GetBytes(errorData);
                await Response.Body.WriteAsync(errorBytes);
                await Response.Body.FlushAsync();
            }
            catch
            {
                // 忽略发送错误事件时的异常
            }

            return new EmptyResult();
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
