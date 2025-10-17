using System.Diagnostics;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// 用于在异步流程中传递 Chat Activity 上下文
/// 解决 Microsoft.Extensions.AI 在流式场景下 Activity.Current 丢失的问题
/// </summary>
public static class ChatActivityContext
{
    private static readonly AsyncLocal<Activity?> _chatActivity = new AsyncLocal<Activity?>();

    /// <summary>
    /// 设置当前的 Chat Activity
    /// </summary>
    public static void SetChatActivity(Activity? activity)
    {
        _chatActivity.Value = activity;
        Console.WriteLine($"📌 ChatActivityContext.Set: {activity?.OperationName ?? "NULL"} (SpanId: {activity?.SpanId.ToString() ?? "NULL"})");
    }

    /// <summary>
    /// 获取当前的 Chat Activity
    /// </summary>
    public static Activity? GetChatActivity()
    {
        var activity = _chatActivity.Value;
        Console.WriteLine($"📍 ChatActivityContext.Get: {activity?.OperationName ?? "NULL"} (SpanId: {activity?.SpanId.ToString() ?? "NULL"})");
        return activity;
    }

    /// <summary>
    /// 清除当前的 Chat Activity
    /// </summary>
    public static void ClearChatActivity()
    {
        Console.WriteLine($"🧹 ChatActivityContext.Clear: {_chatActivity.Value?.OperationName ?? "NULL"}");
        _chatActivity.Value = null;
    }
}
