using Microsoft.SemanticKernel.ChatCompletion;

namespace LY.LlmPool.Web.Models;

public class ChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsStreaming { get; set; }
    public List<Tool>? Tools { get; set; }
    public Dictionary<string, object>? ToolChoice { get; set; }
    
    /// <summary>
    /// 多媒体内容项集合，支持文本、图片等多种类型
    /// 如果此属性有值，则优先使用此属性而非 Content 属性
    /// </summary>
    public ChatMessageContentItemCollection? ContentItems { get; set; }
}

public class Tool
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Dictionary<string, object> InputSchema { get; set; } = new();
} 