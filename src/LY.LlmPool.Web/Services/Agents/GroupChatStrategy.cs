using System.Text;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 群聊编排策略：Agent 轮流发言，共享对话历史，直到达到轮次上限或遇到终止条件。
/// </summary>
public class GroupChatStrategy : IOrchestrationStrategy
{
    private const int DefaultMaxRounds = 3;
    private const string TerminationKeyword = "FINAL";

    public async Task<string> ExecuteAsync(
        LlmApp app,
        IEnumerable<ChatMessage> userMessages,
        Func<LlmConfig, List<ChatMessage>, Task<ChatResponse>> sendMessage,
        Func<LlmConfig, List<ChatMessage>, IAsyncEnumerable<string>> sendStreamingMessage,
        Func<string, string?, int, string, bool, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (app.AgentMembers == null || app.AgentMembers.Count == 0)
        {
            return "No agents configured.";
        }

        var members = app.AgentMembers.OrderBy(m => m.Order).ToList();
        var conversationHistory = new List<ChatMessage>(userMessages);

        // 读取配置：最大轮次（可从 app.OrchestrationMode 或其他配置扩展）
        var maxRounds = DefaultMaxRounds;
        var round = 0;

        while (round < maxRounds)
        {
            round++;
            var hasNewMessage = false;

            foreach (var member in members)
            {
                if (member.LlmConfig == null)
                {
                    continue;
                }

                var messages = new List<ChatMessage>();

                // 添加系统提示
                if (member.LlmPrompt != null && !string.IsNullOrWhiteSpace(member.LlmPrompt.Content))
                {
                    messages.Add(new ChatMessage { Role = "system", Content = member.LlmPrompt.Content });
                }

                // 添加对话历史
                messages.AddRange(conversationHistory);

                var resp = await sendMessage(member.LlmConfig, messages);
                if (!string.Equals(resp.Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var agentMessage = resp.Message ?? string.Empty;
                if (string.IsNullOrWhiteSpace(agentMessage))
                {
                    continue;
                }

                // 检查终止条件
                if (IsFinalAnswer(agentMessage, out var extracted))
                {
                    if (onProgress != null)
                    {
                        try { await onProgress(member.Name ?? string.Empty, member.Role, round, extracted!, true); } catch { }
                    }
                    return extracted;
                }

                // 添加到历史
                conversationHistory.Add(new ChatMessage { Role = "assistant", Content = $"[{member.Name ?? "Agent"}] {agentMessage}" });
                hasNewMessage = true;

                if (onProgress != null)
                {
                    try { await onProgress(member.Name ?? string.Empty, member.Role, round, agentMessage, false); } catch { }
                }
            }

            // 如果一轮没有新消息，提前结束
            if (!hasNewMessage)
            {
                break;
            }
        }

        // 返回最后一条消息
        return conversationHistory.LastOrDefault()?.Content ?? "";
    }

    private static bool IsFinalAnswer(string text, out string extracted)
    {
        extracted = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 常见标记：FINAL: / Final Answer: / 最终结论: / 最终答案:
        var markers = new[] { "FINAL:", "Final Answer:", "FINAL ANSWER:", "最终结论:", "最终答案:" };
        foreach (var mk in markers)
        {
            var idx = text.IndexOf(mk, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx + mk.Length;
                extracted = text.Substring(start).Trim();
                if (string.IsNullOrWhiteSpace(extracted)) extracted = text.Trim();
                return true;
            }
        }
        return false;
    }
}