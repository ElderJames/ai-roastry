using LY.LlmPool.Web.Models;
using System.Text;
using System.Text.Json;

namespace LY.LlmPool.Web.Services.Tools;

/// <summary>
/// 流式响应收集器 - 将 ResponseSegment 列表格式化为 Markdown
/// </summary>
public class StreamingResponseCollector
{
    private readonly List<ResponseSegment> _segments = new();
    private readonly ILogger<StreamingResponseCollector>? _logger;

    public StreamingResponseCollector(ILogger<StreamingResponseCollector>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 添加或更新 Segments
    /// </summary>
    public void UpdateSegments(List<ResponseSegment> segments)
    {
        _segments.Clear();
        _segments.AddRange(segments);
    }

    /// <summary>
    /// 获取收集到的纯文本(向后兼容)
    /// </summary>
    public string GetText()
    {
        return string.Join("", _segments
            .Where(s => s.Type == ResponseSegmentType.Text)
            .Select(s => s.Text ?? ""));
    }

    /// <summary>
    /// 获取收集到的工具调用(向后兼容)
    /// </summary>
    public List<ToolCallInfo> GetToolCalls()
    {
        var toolCalls = new List<ToolCallInfo>();
        foreach (var segment in _segments.Where(s => s.Type == ResponseSegmentType.ToolCalls))
        {
            if (segment.ToolCalls != null)
            {
                foreach (var toolCall in segment.ToolCalls)
                {
                    toolCalls.Add(new ToolCallInfo
                    {
                        CallId = toolCall.CallId,
                        ToolName = toolCall.ToolName,
                        ToolType = toolCall.ToolType,
                        Arguments = toolCall.Arguments,
                        Result = toolCall.Result,
                        StartTime = toolCall.StartTime,
                        EndTime = toolCall.EndTime,
                        IsSuccess = toolCall.Success,
                        ErrorMessage = toolCall.Error
                    });
                }
            }
        }
        return toolCalls;
    }

    /// <summary>
    /// 将收集到的响应格式化为 Markdown
    /// 🔥 按照 ResponseSegment 的顺序穿插展示文本和工具调用
    /// 格式示例: "文本-工具-文本-【并行工具1、并行工具2】-文本"
    /// </summary>
    public string ToMarkdown()
    {
        var markdown = new StringBuilder();

        // 按顺序渲染所有片段
        foreach (var segment in _segments)
        {
            if (segment.Type == ResponseSegmentType.Text)
            {
                // 渲染文本片段
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    markdown.AppendLine(segment.Text.Trim());
                    markdown.AppendLine(); // 添加空行分隔
                }
            }
            else if (segment.Type == ResponseSegmentType.ToolCalls && segment.ToolCalls != null && segment.ToolCalls.Any())
            {
                // 渲染工具调用片段
                var toolCallsInSegment = segment.ToolCalls;

                // 如果是并行工具调用(多个工具),添加标题
                if (toolCallsInSegment.Count > 1)
                {
                    markdown.AppendLine("---");
                    markdown.AppendLine();
                    markdown.AppendLine($"### ⚡ Parallel Tool Execution ({toolCallsInSegment.Count} tools)");
                    markdown.AppendLine();
                }
                else
                {
                    markdown.AppendLine("---");
                    markdown.AppendLine();
                }

                // 渲染每个工具调用
                foreach (var toolCall in toolCallsInSegment)
                {
                    RenderToolCall(markdown, toolCall, toolCallsInSegment.Count > 1);
                }
            }
        }

        return markdown.ToString().TrimEnd();
    }

    /// <summary>
    /// 渲染单个工具调用
    /// </summary>
    private void RenderToolCall(StringBuilder markdown, ToolCallRecord toolCall, bool isParallel)
    {
        // 工具标题
        var headerLevel = isParallel ? "####" : "###";
        markdown.AppendLine($"{headerLevel} {GetToolIcon(toolCall.ToolType)} {toolCall.ToolName}");
        markdown.AppendLine();

        // 状态和执行时间
        var statusEmoji = toolCall.Success ? "✅" : "❌";
        var statusText = toolCall.Success ? "Success" : "Failed";
        markdown.AppendLine($"**Status:** {statusEmoji} {statusText}");
        
        if (toolCall.EndTime.HasValue)
        {
            var duration = (toolCall.EndTime.Value - toolCall.StartTime).TotalMilliseconds;
            markdown.AppendLine($"**Duration:** {duration:F0}ms");
        }
        
        markdown.AppendLine();

        // 输入参数
        if (!string.IsNullOrEmpty(toolCall.Arguments) && toolCall.Arguments != "{}")
        {
            markdown.AppendLine("**Input:**");
            markdown.AppendLine("```json");
            markdown.AppendLine(FormatJson(toolCall.Arguments));
            markdown.AppendLine("```");
            markdown.AppendLine();
        }

        // 输出结果
        if (!string.IsNullOrEmpty(toolCall.Result))
        {
            markdown.AppendLine("**Output:**");
            
            // 尝试判断结果是否为 JSON
            if (IsJson(toolCall.Result))
            {
                markdown.AppendLine("```json");
                markdown.AppendLine(FormatJson(toolCall.Result));
                markdown.AppendLine("```");
            }
            else
            {
                // 纯文本结果
                markdown.AppendLine("```");
                markdown.AppendLine(toolCall.Result);
                markdown.AppendLine("```");
            }
            markdown.AppendLine();
        }

        // 错误信息
        if (!toolCall.Success && !string.IsNullOrEmpty(toolCall.Error))
        {
            markdown.AppendLine("**Error:**");
            markdown.AppendLine("```");
            markdown.AppendLine(toolCall.Error);
            markdown.AppendLine("```");
            markdown.AppendLine();
        }
    }

    /// <summary>
    /// 获取工具类型对应的图标
    /// </summary>
    private static string GetToolIcon(string toolType)
    {
        return toolType.ToLowerInvariant() switch
        {
            "app" => "📱",
            "mcp" => "🔌",
            "function" => "🔧",
            _ => "🛠️"
        };
    }

    /// <summary>
    /// 判断字符串是否为 JSON
    /// </summary>
    private static bool IsJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        return (text.StartsWith("{") && text.EndsWith("}")) ||
               (text.StartsWith("[") && text.EndsWith("]"));
    }

    /// <summary>
    /// 格式化 JSON 字符串
    /// </summary>
    private static string FormatJson(string json)
    {
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            return JsonSerializer.Serialize(element, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch
        {
            // 如果解析失败,返回原始文本
            return json;
        }
    }

    /// <summary>
    /// 重置收集器
    /// </summary>
    public void Clear()
    {
        _segments.Clear();
    }
}
