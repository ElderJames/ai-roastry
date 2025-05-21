using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using OpenAI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace LY.LlmPool.Web.Controllers;

[ApiController]
[Route("v1")]
public class OpenAICompatController : ControllerBase
{
    private readonly LlmPoolService _llmPoolService;
    private readonly ILogger<OpenAICompatController> _logger;
    private readonly ILogger<LoggingHttpHandler> _httpLogger;
    private readonly static JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public OpenAICompatController(
        LlmPoolService llmPoolService,
        ILogger<OpenAICompatController> logger,
        ILogger<LoggingHttpHandler> httpLogger)
    {
        _llmPoolService = llmPoolService;
        _logger = logger;
        _httpLogger = httpLogger;
    }

    private Kernel CreateKernel(LlmConfig config)
    {
        var handler = new LoggingHttpHandler(_httpLogger);
        handler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(config.BaseUrl) };

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(config.Model, config.ApiKey, httpClient: httpClient);
        
        return builder.Build();
    }

    [HttpPost("chat/completions")]
    public async Task ChatCompletions()
    {
        EndpointCallRecord? callRecord = null;
        var requestStartTime = DateTime.UtcNow;
        object? requestData = null;
        string? requestBody = null;

        try
        {
            // Capture request data
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
            await Response.WriteAsJsonAsync(new { error = "Missing or invalid API key" });
            return;
        }

        var apiKey = authHeader.ToString().Replace("Bearer ", "");

        try
        {
            // Create initial call record
            callRecord = await _llmPoolService.CreateCallRecordAsync(apiKey, requestData);

            var modelAcquireStartTime = DateTime.UtcNow;
            var config = await _llmPoolService.GetAvailableConfigByKeyAsync(apiKey);
            var waitTime = DateTime.UtcNow - modelAcquireStartTime;

            // Update call record with waiting time
            if (callRecord != null)
            {
                callRecord.WaitTime = waitTime;
            }

            if (config == null)
            {
                // Update call record with error
                if (callRecord != null)
                {
                    callRecord.IsSuccessful = false;
                    callRecord.ErrorMessage = "No available model found or invalid API key";
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await Response.WriteAsJsonAsync(new { error = "No available model found or invalid API key" });
                return;
            }

            // Update call record with config info
            if (callRecord != null)
            {
                callRecord.LlmConfigId = config.Id;
                await _llmPoolService.UpdateCallRecordAsync(callRecord);
            }

            try
            {
                // Parse request
                var chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestBody ?? "{}", _jsonSerializerOptions);
                if (chatRequest == null)
                {
                    throw new InvalidOperationException("Invalid chat request");
                }

                // Override model if specified in config
                if (!string.IsNullOrEmpty(config.Model))
                {
                    chatRequest.Model = config.Model;
                }

                // Record model call start time
                var modelCallStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    callRecord.ModelCallStartedAt = modelCallStartTime;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                // Set response headers

                if (Request.Headers.Accept.Any(x => x.Contains("text/event-stream")))
                {
                    Response.Headers["Transfer-Encoding"] = "chunked";
                    Response.Headers["Content-Type"] = "text/event-stream";
                }
                else
                {
                    Response.Headers["Content-Type"] = "application/json";
                }

                // Record model response start time
                var modelResponseStartTime = DateTime.UtcNow;
                if (callRecord != null)
                {
                    callRecord.ModelResponseStartedAt = modelResponseStartTime;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                // Create kernel and chat history
                var kernel = CreateKernel(config);
                var chatHistory = new ChatHistory();

                // Add messages to chat history
                foreach (var message in chatRequest.Messages)
                {
                    var role = MapRoleForDeepseek(message.Role);
                    switch (role.ToLower())
                    {
                        case "system":
                            chatHistory.AddSystemMessage(message.Content);
                            break;
                        case "assistant":
                            chatHistory.AddAssistantMessage(message.Content);
                            break;
                        case "user":
                        default:
                            chatHistory.AddUserMessage(message.Content);
                            break;
                    }
                }

                var settings = new OpenAIPromptExecutionSettings 
                { 
                    Temperature = chatRequest.Temperature,
                    MaxTokens = chatRequest.MaxTokens,
                    FrequencyPenalty = chatRequest.FrequencyPenalty,
                    PresencePenalty = chatRequest.PresencePenalty,
                    TopP = chatRequest.TopP
                };

                var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

                if (chatRequest.Stream ?? false)
                {
                    var streamingResult = chatCompletionService.GetStreamingChatMessageContentsAsync(
                        chatHistory,
                        settings
                    );

                    await foreach (var update in streamingResult)
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                            Object = "chat.completion.chunk",
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            model = chatRequest.Model,
                            choices = new[]
                            {
                                new
                                {
                                    delta = new
                                    {
                                        role = "assistant",
                                        content = update.Content
                                    },
                                    index = 0,
                                    finish_reason = (string?)null
                                }
                            }
                        }, _jsonSerializerOptions);

                        await Response.WriteAsync($"data: {json}\n\n");
                        await Response.Body.FlushAsync();
                    }

                    await Response.WriteAsync("data: [DONE]\n\n");
                    await Response.Body.FlushAsync();
                }
                else
                {
                    var response = await chatCompletionService.GetChatMessageContentAsync(
                        chatHistory,
                        settings
                    );

                    var json = JsonSerializer.Serialize(new
                    {
                        id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                        Object = "chat.completion",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = chatRequest.Model,
                        choices = new[]
                        {
                            new
                            {
                                message = new
                                {
                                    role = "assistant",
                                    content = response.Content
                                },
                                index = 0,
                                finish_reason = "stop"
                            }
                        },
                        usage = new
                        {
                            prompt_tokens = 0, // Semantic Kernel currently doesn't provide token counts
                            completion_tokens = 0,
                            total_tokens = 0
                        }
                    }, _jsonSerializerOptions);

                    await Response.WriteAsync(json);
                }

                // Record model response end time and success status
                if (callRecord != null)
                {
                    callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                    callRecord.IsSuccessful = true;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }
            }
            catch (Exception ex)
            {
                // Update call record with error
                if (callRecord != null)
                {
                    callRecord.IsSuccessful = false;
                    callRecord.ErrorMessage = ex.Message;
                    callRecord.ModelResponseEndedAt = DateTime.UtcNow;
                    await _llmPoolService.UpdateCallRecordAsync(callRecord);
                }

                _logger.LogError(ex, "Error processing request");
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await Response.WriteAsJsonAsync(new { error = "Internal server error" });
            }
            finally
            {
                if (config != null)
                {
                    _llmPoolService.ReleaseConfig(config.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in chat completions");
            Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await Response.WriteAsJsonAsync(new { error = "Internal server error" });
        }
    }

    private string MapRoleForDeepseek(string role)
    {
        return role.ToLower() switch
        {
            "assistant" => "assistant",
            "user" => "user",
            "system" => "system",
            _ => role
        };
    }

    private class ChatRequest
    {
        public string Model { get; set; } = string.Empty;
        public List<Message> Messages { get; set; } = new();
        public float? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public float? TopP { get; set; }
        public float? FrequencyPenalty { get; set; }
        public float? PresencePenalty { get; set; }
        public bool? Stream { get; set; }
    }

    private class Message
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}