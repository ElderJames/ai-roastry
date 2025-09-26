using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Models.Anthropic;
using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// Anthropic API 请求转换服务
/// </summary>
public class AnthropicTransformService
{
    private readonly ILogger<AnthropicTransformService> _logger;

    public AnthropicTransformService(ILogger<AnthropicTransformService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 将 Anthropic 消息请求转换为内部 ChatMessage 格式
    /// </summary>
    public List<ChatMessage> ConvertToInternalMessages(MessagesRequest request)
    {
        var messages = new List<ChatMessage>();

        // 添加系统消息
        var systemContent = ExtractSystemContent(request.System);
        if (!string.IsNullOrEmpty(systemContent))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = systemContent,
                Timestamp = DateTime.UtcNow,
                Tools = ConvertTools(request.Tools),
                ToolChoice = request.ToolChoice
            });
        }

        // 转换对话消息
        foreach (var msg in request.Messages)
        {
            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = ConvertContentToString(msg.Content),
                ContentItems = ConvertContentToItems(msg.Content),
                Timestamp = DateTime.UtcNow,
                Tools = ConvertTools(request.Tools),
                ToolChoice = request.ToolChoice
            };
            messages.Add(chatMessage);
        }

        return messages;
    }

    /// <summary>
    /// 将 Anthropic 响应转换为标准格式
    /// </summary>
    public MessagesResponse ConvertToAnthropicResponse(ChatResponse response, MessagesRequest originalRequest)
    {
        var content = new List<ContentBlock>();

        // 添加文本内容（如果有）
        if (!string.IsNullOrEmpty(response.Message))
        {
            content.Add(new ContentBlock
            {
                Type = "text",
                Text = response.Message
            });
        }

        // 添加工具调用内容（如果有）
        if (response.ToolCalls != null && response.ToolCalls.Count > 0)
        {
            foreach (var toolCall in response.ToolCalls)
            {
                // 解析参数 JSON
                Dictionary<string, object>? inputParams = null;
                var argsText = toolCall.Function.Arguments;
                if (string.IsNullOrWhiteSpace(argsText))
                {
                    // 归一化空字符串为 {}，避免下游解析报错
                    inputParams = new Dictionary<string, object>();
                }
                else
                {
                    try
                    {
                        inputParams = JsonSerializer.Deserialize<Dictionary<string, object>>(argsText);
                    }
                    catch
                    {
                        // 如果解析失败，退化为将原串包裹到 arguments 字段，避免抛错
                        inputParams = new Dictionary<string, object> { { "arguments", argsText } };
                    }
                }

                content.Add(new ContentBlock
                {
                    Type = "tool_use",
                    Id = toolCall.Id,
                    Name = toolCall.Function.Name,
                    Input = inputParams
                });
            }
        }

        var anthropicResponse = new MessagesResponse
        {
            Id = Guid.NewGuid().ToString(),
            Model = originalRequest.OriginalModel ?? originalRequest.Model,
            Role = "assistant",
            Type = "message",
            Content = content,
            StopReason = response.Status == "success" ? (response.ToolCalls?.Count > 0 ? "tool_use" : "end_turn") : "error",
            Usage = new Usage
            {
                InputTokens = EstimateTokens(string.Join(" ", originalRequest.Messages.Select(m => ConvertContentToString(m.Content)))),
                OutputTokens = EstimateTokens(response.Message),
                CacheCreationInputTokens = 0,
                CacheReadInputTokens = 0
            }
        };

        return anthropicResponse;
    }

    /// <summary>
    /// 创建流式响应事件
    /// </summary>
    public IEnumerable<StreamEvent> ConvertToStreamEvents(IAsyncEnumerable<string> streamingResponse, MessagesRequest originalRequest)
    {
        // 发送开始事件
        yield return new MessageStartEvent
        {
            Message = new MessagesResponse
            {
                Id = Guid.NewGuid().ToString(),
                Model = originalRequest.OriginalModel ?? originalRequest.Model,
                Role = "assistant",
                Type = "message",
                Content = new List<ContentBlock>(),
                Usage = new Usage()
            }
        };

        // 发送内容开始事件
        yield return new ContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = new ContentBlock
            {
                Type = "text",
                Text = ""
            }
        };

        // 这里需要异步枚举流式内容
        // 注意：在实际实现中，这个方法需要重新设计为异步方法
    }

    /// <summary>
    /// 异步转换流式响应
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> ConvertToStreamEventsAsync(IAsyncEnumerable<string> streamingResponse, MessagesRequest originalRequest)
    {
        var messageId = Guid.NewGuid().ToString();

        // 发送开始事件
        yield return new MessageStartEvent
        {
            Message = new MessagesResponse
            {
                Id = messageId,
                Model = originalRequest.OriginalModel ?? originalRequest.Model,
                Role = "assistant",
                Type = "message",
                Content = new List<ContentBlock>(),
                Usage = new Usage()
            }
        };

        // 发送内容开始事件
        yield return new ContentBlockStartEvent
        {
            Index = 0,
            ContentBlock = new ContentBlock
            {
                Type = "text",
                Text = ""
            }
        };

        // 处理流式内容
        var totalOutputTokens = 0;
        await foreach (var chunk in streamingResponse)
        {
            if (!string.IsNullOrEmpty(chunk))
            {
                totalOutputTokens += EstimateTokens(chunk);

                yield return new ContentBlockDeltaEvent
                {
                    Index = 0,
                    Delta = new Dictionary<string, object>
                    {
                        ["type"] = "text_delta",
                        ["text"] = chunk
                    }
                };
            }
        }

        // 发送内容结束事件
        yield return new ContentBlockStopEvent
        {
            Index = 0
        };

        // 发送消息结束事件
        yield return new MessageDeltaEvent
        {
            Delta = new Dictionary<string, object>
            {
                ["stop_reason"] = "end_turn"
            },
            Usage = new Usage
            {
                OutputTokens = totalOutputTokens
            }
        };

        yield return new MessageStopEvent();
    }

    /// <summary>
    /// 创建 Token 计数响应
    /// </summary>
    public TokenCountResponse ConvertToTokenCountResponse(TokenCountRequest request)
    {
        var totalTokens = 0;

        // 计算系统消息 tokens
        var systemContent = ExtractSystemContent(request.System);
        if (!string.IsNullOrEmpty(systemContent))
        {
            totalTokens += EstimateTokens(systemContent);
        }

        // 计算对话消息 tokens
        foreach (var message in request.Messages)
        {
            totalTokens += EstimateTokens(ConvertContentToString(message.Content));
        }

        return new TokenCountResponse
        {
            InputTokens = totalTokens
        };
    }

    /// <summary>
    /// 提取系统内容
    /// </summary>
    private string ExtractSystemContent(JsonElement? systemElement)
    {
        if (!systemElement.HasValue)
            return string.Empty;

        var element = systemElement.Value;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            
            case JsonValueKind.Array:
                var texts = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textProp))
                    {
                        texts.Add(textProp.GetString() ?? string.Empty);
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        texts.Add(item.GetString() ?? string.Empty);
                    }
                }
                return string.Join(" ", texts);
            
            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var textProperty))
                {
                    return textProperty.GetString() ?? string.Empty;
                }
                break;
        }

        return string.Empty;
    }

    /// <summary>
    /// 将内容对象转换为字符串
    /// </summary>
    private string ConvertContentToString(object content)
    {
        if (content is string str)
        {
            return str;
        }

        if (content is JsonElement jsonElement)
        {
            switch (jsonElement.ValueKind)
            {
                case JsonValueKind.String:
                    return jsonElement.GetString() ?? string.Empty;
                case JsonValueKind.Array:
                    var texts = new List<string>();
                    foreach (var item in jsonElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out var textProp))
                        {
                            texts.Add(textProp.GetString() ?? string.Empty);
                        }
                        else if (item.ValueKind == JsonValueKind.String)
                        {
                            texts.Add(item.GetString() ?? string.Empty);
                        }
                    }
                    return string.Join(" ", texts);
                case JsonValueKind.Object:
                    if (jsonElement.TryGetProperty("text", out var textProperty))
                    {
                        return textProperty.GetString() ?? string.Empty;
                    }
                    break;
            }
        }

        try
        {
            return JsonSerializer.Serialize(content);
        }
        catch
        {
            return content?.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// 将 Anthropic 内容转换为 ChatMessageContentItemCollection
    /// </summary>
    private ChatMessageContentItemCollection? ConvertContentToItems(object content)
    {
        if (content is string str)
        {
            // 简单文本内容，返回 null 让系统使用 Content 属性
            return null;
        }

        if (content is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            var contentItems = new ChatMessageContentItemCollection();
            
            foreach (var item in jsonElement.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typeProp))
                {
                    var type = typeProp.GetString();
                    switch (type)
                    {
                        case "text":
                            if (item.TryGetProperty("text", out var textProp))
                            {
                                var text = textProp.GetString() ?? string.Empty;
                                contentItems.Add(new TextContent(text));
                            }
                            break;
                        
                        case "image":
                            if (item.TryGetProperty("source", out var sourceProp))
                            {
                                if (sourceProp.TryGetProperty("type", out var sourceTypeProp) && 
                                    sourceTypeProp.GetString() == "base64")
                                {
                                    if (sourceProp.TryGetProperty("data", out var dataProp))
                                    {
                                        var base64Data = dataProp.GetString() ?? string.Empty;
                                        var mediaType = "image/jpeg"; // 默认类型
                                        
                                        if (sourceProp.TryGetProperty("media_type", out var mediaTypeProp))
                                        {
                                            mediaType = mediaTypeProp.GetString() ?? mediaType;
                                        }
                                        
                                        try
                                        {
                                            var imageBytes = Convert.FromBase64String(base64Data);
                                            contentItems.Add(new ImageContent(imageBytes, mediaType));
                                        }
                                        catch
                                        {
                                            // 如果 base64 解析失败，跳过这个图片
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
            }
            
            return contentItems.Count > 0 ? contentItems : null;
        }

        return null;
    }

    /// <summary>
    /// 转换 Anthropic tools 到 OpenAI 工具形状
    /// </summary>
    private List<Models.OpenAITool>? ConvertTools(List<Models.Anthropic.AnthropicTool>? anthropicTools)
    {
        if (anthropicTools == null || anthropicTools.Count == 0)
            return null;

        var tools = new List<Models.OpenAITool>();
        foreach (var anthropicTool in anthropicTools)
        {
            tools.Add(new Models.OpenAITool
            {
                Type = "function",
                Function = new Models.OpenAIFunction
                {
                    Name = anthropicTool.Name,
                    Description = anthropicTool.Description,
                    Parameters = anthropicTool.InputSchema
                }
            });
        }

        return tools;
    }

    /// <summary>
    /// 简单的 Token 估算（基于单词数）
    /// </summary>
    private int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // 简单估算：平均每个单词约 1.3 个 token
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return (int)(wordCount * 1.3);
    }
}
