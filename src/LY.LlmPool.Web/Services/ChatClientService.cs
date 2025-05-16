using LY.LlmPool.Web.Data.Entities;
using OpenAI;
using System.ClientModel;
using Microsoft.AspNetCore.Http;

namespace LY.LlmPool.Web.Services;

public class ChatClientService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ChatClientService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private string GetCurrentBaseUrl()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null) return string.Empty;
        
        return $"{request.Scheme}://{request.Host}";
    }

    private List<OpenAI.Chat.ChatMessage> ConvertToChatMessages(List<Models.ChatMessage> messages)
    {
        var chatMessages = new List<OpenAI.Chat.ChatMessage>();
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "user":
                    chatMessages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(message.Content));
                    break;
                case "assistant":
                    chatMessages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(message.Content));
                    break;
                case "system":
                    chatMessages.Add(OpenAI.Chat.ChatMessage.CreateSystemMessage(message.Content));
                    break;
                default:
                    chatMessages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(message.Content));
                    break;
            }
        }
        return chatMessages;
    }

    private async Task<ChatResponse> SendMessageInternalAsync(OpenAIClient client, string model, List<Models.ChatMessage> messages)
    {
        try
        {
            var chatClient = client.GetChatClient(model);
            var chatMessages = ConvertToChatMessages(messages);
            var response = await chatClient.CompleteChatAsync(chatMessages.ToArray());
            
            return new ChatResponse
            {
                Message = response.Value.Content[0].Text,
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

    public async Task<ChatResponse> SendMessageAsync(LlmConfig config, List<Models.ChatMessage> messages)
    {
        var openAIClientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(config.BaseUrl)
        };

        var client = new OpenAIClient(new ApiKeyCredential(config.ApiKey), openAIClientOptions);
        return await SendMessageInternalAsync(client, config.Model, messages);
    }

    public async Task<ChatResponse> SendMessageAsync(LlmEndpoint config, List<Models.ChatMessage> messages)
    {
        var openAIClientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(GetCurrentBaseUrl())
        };

        var client = new OpenAIClient(new ApiKeyCredential(config.Id), openAIClientOptions);
        return await SendMessageInternalAsync(client, config.Name, messages);
    }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    public string GetMessage() => Message;
} 