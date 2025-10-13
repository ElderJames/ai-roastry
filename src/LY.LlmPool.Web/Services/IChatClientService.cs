using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services.Agents;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services;

public interface IChatClientService
{
    Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<AITool>? tools = null);

    Task<ChatResponse> SendMessageAsync(LlmConfig config, List<ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<AITool>? tools = null);

    IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages, IEnumerable<AITool>? tools = null);

    IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<AITool>? tools = null);

    IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint config, List<ChatMessage> messages, IEnumerable<AITool>? tools = null);

    /// <summary>
    /// 发送流式消息并返回详细更新信息(包含文本和工具调用)
    /// </summary>
    /// <param name="config">LLM 配置</param>
    /// <param name="messages">消息列表（会被更新以包含工具调用和结果）</param>
    /// <param name="parameters">可选参数</param>
    /// <param name="tools">可选工具列表</param>
    /// <returns>流式更新</returns>
    IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(
        LlmConfig config, 
        List<ChatMessage> messages, 
        Dictionary<string, object>? parameters = null, 
        IEnumerable<AITool>? tools = null);

    /// <summary>
    /// 发送流式消息并返回详细更新信息(Endpoint 版本)
    /// </summary>
    /// <param name="config">端点配置</param>
    /// <param name="messages">消息列表（会被更新以包含工具调用和结果）</param>
    /// <param name="tools">可选工具列表</param>
    /// <returns>流式更新</returns>
    IAsyncEnumerable<ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(
        LlmEndpoint config, 
        List<ChatMessage> messages, 
        IEnumerable<AITool>? tools = null);
}
