using Microsoft.AspNetCore.Http;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace LY.LlmPool.Web.Services;

public class ChatClientService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<LoggingHttpHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public ChatClientService(
        IHttpContextAccessor httpContextAccessor,
        ILogger<LoggingHttpHandler> logger,
        IHttpClientFactory httpClientFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private string GetCurrentBaseUrl()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null) return string.Empty;

        return $"{request.Scheme}://{request.Host}";
    }

    private Kernel CreateKernel(string apiKey, string baseUrl, string model)
    {
        var handler = new LoggingHttpHandler(_logger);
        handler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl+ "/v1") };

        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(model, apiKey, httpClient: httpClient);

        return builder.Build();
    }

    private ChatHistory BuildChatHistory(List<ChatMessage> messages)
    {
        var chatHistory = new ChatHistory();

        foreach (var message in messages)
        {
            switch (message.Role.ToLower())
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

        return chatHistory;
    }

    public async Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages)
    {
        try
        {
            var kernel = CreateKernel(config.ApiKey, config.BaseUrl, config.Model);
            var chatHistory = BuildChatHistory(messages);
            var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

            var result = await chatCompletionService.GetChatMessageContentsAsync(chatHistory);

            return new ChatResponse
            {
                Message = result[0].Content,
                Status = "success"
            };
        }
        catch (Exception ex)
        {
            return new ChatResponse
            {
                Message = ex.Message,
                Status = "error"
            };
        }
    }

    public async Task<ChatResponse> SendMessageAsync(LlmEndpoint config, List<ChatMessage> messages)
    {
        var baseUrl = GetCurrentBaseUrl();
        return await SendMessageAsync(new LlmConfig
        {
            ApiKey = config.Id,
            BaseUrl = baseUrl,
            Model = config.Name
        }, messages);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages)
    {
        return SendStreamingMessageInternalAsync(config.ApiKey, config.BaseUrl, config.Model, messages);
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint config, List<ChatMessage> messages)
    {
        var baseUrl = GetCurrentBaseUrl();
        return SendStreamingMessageInternalAsync(config.Id, baseUrl, config.Name, messages);
    }

    private async IAsyncEnumerable<string> SendStreamingMessageInternalAsync(
        string apiKey,
        string baseUrl,
        string model,
        List<ChatMessage> messages)
    {
        var kernel = CreateKernel(apiKey, baseUrl, model);
        var chatHistory = BuildChatHistory(messages);
        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
        var streamingResults = chatCompletionService.GetStreamingChatMessageContentsAsync(chatHistory);

        await foreach (var update in streamingResults)
        {
            if (!string.IsNullOrEmpty(update.Content))
            {
                yield return update.Content;
            }
        }
    }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}