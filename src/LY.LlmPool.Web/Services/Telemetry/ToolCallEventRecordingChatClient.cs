using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// DelegatingChatClient 用于在模型响应后记录工具调用信息到 Activity Events
/// 使得 ActivityTraceService 可以更容易地展示工具调用
/// </summary>
public class ToolCallEventRecordingChatClient : DelegatingChatClient
{
    private readonly ILogger<ToolCallEventRecordingChatClient> _logger;

    public ToolCallEventRecordingChatClient(IChatClient innerClient, ILogger<ToolCallEventRecordingChatClient> logger) 
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        
        // 记录工具调用到当前 Activity Events
        RecordToolCallsFromResponse(response);
        
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, 
        ChatOptions? options = null, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 累积流式响应中的工具调用
        List<FunctionCallContent> toolCalls = new();
        List<FunctionResultContent> toolResults = new();

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            // 提取工具调用
            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fcc)
                    {
                        toolCalls.Add(fcc);
                    }
                    else if (content is FunctionResultContent frc)
                    {
                        toolResults.Add(frc);
                    }
                }
            }

            yield return update;
        }

        // 流式完成后,记录所有工具调用
        if (toolCalls.Count > 0)
        {
            Activity.Current.RecordToolCalls(toolCalls);
        }
        if (toolResults.Count > 0)
        {
            Activity.Current.RecordToolResults(toolResults);
        }
    }

    private void RecordToolCallsFromResponse(Microsoft.Extensions.AI.ChatResponse response)
    {
        var toolCalls = new List<FunctionCallContent>();
        var toolResults = new List<FunctionResultContent>();

        // 从响应消息中提取工具调用和结果
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fcc)
                {
                    toolCalls.Add(fcc);
                }
                else if (content is FunctionResultContent frc)
                {
                    toolResults.Add(frc);
                }
            }
        }

        // 记录到 Activity Events
        if (toolCalls.Count > 0)
        {
            Activity.Current.RecordToolCalls(toolCalls);
            _logger.LogDebug("记录了 {Count} 个工具调用到 Activity Events", toolCalls.Count);
        }
        if (toolResults.Count > 0)
        {
            Activity.Current.RecordToolResults(toolResults);
            _logger.LogDebug("记录了 {Count} 个工具结果到 Activity Events", toolResults.Count);
        }
    }
}
