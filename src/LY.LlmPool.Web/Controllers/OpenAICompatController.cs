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

namespace LY.LlmPool.Web.Controllers;

[ApiController]
[Route("v1")]
public class OpenAICompatController : ControllerBase
{
    private readonly LlmPoolService _llmPoolService;
    private readonly ILogger<OpenAICompatController> _logger;
    private readonly IServiceProvider _serviceProvider;

    public OpenAICompatController(
        LlmPoolService llmPoolService,
        ILogger<OpenAICompatController> logger,
        IServiceProvider serviceProvider)
    {
        _llmPoolService = llmPoolService;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    private IChatClient CreateChatClient(LlmConfig config)
    {
        var modelType = config.ModelType?.Name?.ToLower() ?? "";

        IChatClient client = null;
        //var builder = new OpenAIClientBuilder(_serviceProvider, config.ApiKey, config.BaseUrl, config.Model);
        switch (modelType)
        {
            case "azure":
                // Azure OpenAI
                client = new Azure.AI.Inference.ChatCompletionsClient(
                        new("https://models.inference.ai.azure.com"),
                        new AzureKeyCredential(Environment.GetEnvironmentVariable("GH_TOKEN")!)
                    )
                    .AsIChatClient(config.Model);

                break;

            case "openai":
            case "deepseek":
            default:
                var openAIClientOptions = new OpenAIClientOptions();
                openAIClientOptions.Endpoint = new Uri(config.BaseUrl);
                client = new OpenAIClient(new ApiKeyCredential(config.ApiKey), openAIClientOptions)
                    .AsChatClient(config.Model);

                break;
        }

        return client;
    }

    [HttpPost("chat/completions")]
    public async Task ChatCompletions()
    {
        EndpointCallRecord? callRecord = null;
        var requestStartTime = DateTime.UtcNow;
        object? requestData = null;
        string? requestBody = null;
        IChatClient? chatClient = null;

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
                    requestData = JsonSerializer.Deserialize<object>(requestBody);
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
                // Create chat client based on config
                chatClient = CreateChatClient(config);

                // Parse request
                var chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestBody ?? "{}");
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
                Response.Headers["Transfer-Encoding"] = "chunked";
                if (Request.Headers.Accept.Any(x => x.Contains("text/event-stream")))
                {
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

                // Prepare chat messages and options
                var messages = new List<ChatMessage>();
                var modelType = config.ModelType?.Name?.ToLower() ?? "";

                foreach (var message in chatRequest.Messages)
                {
                    var role = modelType == "deepseek" ? MapRoleForDeepseek(message.Role) : message.Role;
                    var chatRole = role.ToLower() switch
                    {
                        "system" => ChatRole.System,
                        "assistant" => ChatRole.Assistant,
                        "user" => ChatRole.User,
                        _ => ChatRole.User
                    };
                    messages.Add(new ChatMessage(chatRole, new[] { new TextContent(message.Content) }));
                }

                var chatOptions = new ChatOptions
                {
                    Temperature = chatRequest.Temperature,
                    MaxOutputTokens = chatRequest.MaxTokens,
                    FrequencyPenalty = chatRequest.FrequencyPenalty,
                    PresencePenalty = chatRequest.PresencePenalty
                };

                if (chatRequest.Stream ?? false)
                {
                    await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions))
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                            @object = "chat.completion.chunk",
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            model = chatRequest.Model,
                            choices = new[]
                            {
                                new
                                {
                                    delta = new
                                    {
                                        role = update.Role.ToString().ToLower(),
                                        content = update.Contents.OfType<TextContent>().FirstOrDefault()?.Text
                                    },
                                    index = 0,
                                    finish_reason = update.FinishReason
                                }
                            }
                        });

                        await Response.WriteAsync($"data: {json}\n\n");
                        await Response.Body.FlushAsync();
                    }

                    await Response.WriteAsync("data: [DONE]\n\n");
                    await Response.Body.FlushAsync();
                }
                else
                {
                    var response = await chatClient.GetResponseAsync(messages, chatOptions);
                    var usage = response.Usage;
                    var responseMessage = response.Messages.FirstOrDefault();

                    var json = JsonSerializer.Serialize(new
                    {
                        id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                        @object = "chat.completion",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model = chatRequest.Model,
                        choices = new[]
                        {
                            new
                            {
                                message = new
                                {
                                    role = responseMessage?.Role.ToString().ToLower(),
                                    content = responseMessage?.Contents.OfType<TextContent>().FirstOrDefault()?.Text
                                },
                                index = 0,
                                finish_reason = response.FinishReason
                            }
                        },
                        usage = new
                        {
                            prompt_tokens = usage?.InputTokenCount,
                            completion_tokens = usage?.OutputTokenCount,
                            total_tokens = usage?.TotalTokenCount
                        }
                    });

                    await Response.WriteAsync(json);

                    // Update call record with token usage
                    if (callRecord != null && usage != null)
                    {
                        callRecord.PromptTokens = usage.InputTokenCount;
                        callRecord.CompletionTokens = usage.OutputTokenCount;
                        callRecord.TotalTokens = usage.TotalTokenCount;
                    }
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

                // Dispose chat client
                if (chatClient is IDisposable disposable)
                {
                    disposable.Dispose();
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