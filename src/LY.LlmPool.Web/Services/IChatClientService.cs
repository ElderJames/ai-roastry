using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services;

public interface IChatClientService
{
    Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null);
    
    Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<object>? toolObjects = null);
    
    IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null);
    
    IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<object>? toolObjects = null);
    
    IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint config, List<ChatMessage> messages, IEnumerable<object>? toolObjects = null);
}
