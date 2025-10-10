using System.Text.Json;

namespace LY.LlmPool.Web.Components.ChatHelpers;

/// <summary>
/// 工具调用处理工具类
/// </summary>
public static class ToolCallUtils
{
    /// <summary>
    /// 工具调用视图模型
    /// </summary>
    public class ToolCallView
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string? Result { get; set; }
        public string? Error { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        public List<string> Logs { get; set; } = new();
    }

    /// <summary>
    /// 创建工具调用事件
    /// </summary>
    public static ToolCallView StartToolEvent(string name, string argumentsJson)
    {
        return new ToolCallView
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Arguments = argumentsJson,
            Time = DateTime.Now
        };
    }

    /// <summary>
    /// 完成工具调用事件
    /// </summary>
    public static void CompleteToolEvent(ToolCallView ev, string result)
    {
        ev.Result = result;
    }

    /// <summary>
    /// 失败工具调用事件
    /// </summary>
    public static void FailToolEvent(ToolCallView ev, string error)
    {
        ev.Error = error;
    }

    /// <summary>
    /// 添加工具日志
    /// </summary>
    public static void AppendToolLog(ToolCallView ev, string log)
    {
        ev.Logs.Add(log);
    }

    /// <summary>
    /// 检查工具内容是否为错误信息
    /// </summary>
    public static bool IsErrorContent(string content)
    {
        return content.Contains("Error:") ||
               content.Contains("Exception:") ||
               content.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 处理工具结果，根据内容类型自动设置结果或错误
    /// </summary>
    public static void HandleToolResult(ToolCallView ev, string content)
    {
        if (IsErrorContent(content))
        {
            FailToolEvent(ev, content);
        }
        else
        {
            CompleteToolEvent(ev, content);
        }
    }
}