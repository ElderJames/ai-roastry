using System.Text.Json;

namespace LY.LlmPool.Web.Components.ChatHelpers;

/// <summary>
/// 聊天消息处理工具类
/// </summary>
public static class ChatMessageUtils
{
    /// <summary>
    /// 消息段结构
    /// </summary>
    public sealed class MessageSegments
    {
        public string Thinking { get; set; } = string.Empty;
        public string Visible { get; set; } = string.Empty;
        public bool HasThinking => !string.IsNullOrWhiteSpace(Thinking);
    }

    /// <summary>
    /// 解析消息段，将思维链和可见内容分离
    /// </summary>
    public static MessageSegments ParseMessageSegments(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new MessageSegments { Visible = string.Empty };
        }

        const string startTag = "<think>";
        const string endTag = "</think>";

        var text = content;
        var startIdx = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        var endIdx = text.IndexOf(endTag, StringComparison.OrdinalIgnoreCase);

        string thinking = string.Empty;
        string visible;

        if (startIdx >= 0 && endIdx > startIdx)
        {
            var before = text.Substring(0, startIdx);
            var start = startIdx + startTag.Length;
            thinking = text.Substring(start, endIdx - start);
            visible = before + text.Substring(endIdx + endTag.Length);
        }
        else if (startIdx >= 0 && (endIdx == -1 || endIdx < startIdx))
        {
            var before = text.Substring(0, startIdx);
            var start = startIdx + startTag.Length;
            thinking = text.Substring(start);
            visible = before;
        }
        else if (startIdx == -1 && endIdx >= 0)
        {
            thinking = text.Substring(0, endIdx);
            visible = text.Substring(endIdx + endTag.Length);
        }
        else
        {
            visible = text;
        }

        thinking = thinking
            .Replace(startTag, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(endTag, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('\r', '\n');

        visible = visible
            .Replace(startTag, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(endTag, string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimStart('\r', '\n');

        return new MessageSegments
        {
            Thinking = thinking,
            Visible = visible
        };
    }

    /// <summary>
    /// 解析流式消息内容，支持工具调用和普通文本
    /// </summary>
    public static StreamingMessageParseResult ParseStreamingContent(string content)
    {
        var result = new StreamingMessageParseResult();

        // 尝试解析为 JSON
        if (content.TrimStart().StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                // 检查是否是 assistant 消息包含 tool_calls
                if (root.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    result.MessageType = StreamingMessageType.ToolCalls;
                    result.ToolCalls = new List<ToolCallInfo>();

                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var id = tc.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                        var func = tc.GetProperty("function");
                        var name = func.GetProperty("name").GetString() ?? "unknown";
                        var args = func.GetProperty("arguments").GetString() ?? "{}";

                        result.ToolCalls.Add(new ToolCallInfo
                        {
                            Id = id,
                            Name = name,
                            Arguments = args
                        });
                    }
                }
                // 检查是否是 tool 消息
                else if (root.TryGetProperty("role", out var role) && role.GetString() == "tool")
                {
                    result.MessageType = StreamingMessageType.ToolResult;
                    result.ToolCallId = root.GetProperty("tool_call_id").GetString();
                    result.ToolContent = root.GetProperty("content").GetString() ?? "";
                }
                // 检查是否是 assistant 消息有 content
                else if (root.TryGetProperty("content", out var msgContent) && msgContent.ValueKind == JsonValueKind.String)
                {
                    result.MessageType = StreamingMessageType.AssistantContent;
                    result.Content = msgContent.GetString() ?? "";
                }
                else
                {
                    result.MessageType = StreamingMessageType.Unknown;
                }
            }
            catch
            {
                // 不是有效 JSON，当普通文本处理
                result.MessageType = StreamingMessageType.PlainText;
                result.Content = content;
            }
        }
        else
        {
            // 普通文本
            result.MessageType = StreamingMessageType.PlainText;
            result.Content = content;
        }

        return result;
    }
}

/// <summary>
/// 流式消息类型
/// </summary>
public enum StreamingMessageType
{
    Unknown,
    PlainText,
    AssistantContent,
    ToolCalls,
    ToolResult
}

/// <summary>
/// 工具调用信息
/// </summary>
public class ToolCallInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>
/// 流式消息解析结果
/// </summary>
public class StreamingMessageParseResult
{
    public StreamingMessageType MessageType { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string ToolContent { get; set; } = string.Empty;
    public List<ToolCallInfo>? ToolCalls { get; set; }
}
