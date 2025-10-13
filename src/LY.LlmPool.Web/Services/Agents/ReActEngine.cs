using System.Text.Json;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services.Agents;

public interface IToolExecutor
{
    Task<(bool ok, string output, string? error)> ExecuteAsync(ITool tool, JsonElement? args, CancellationToken ct = default);
}

/// <summary>
/// ReAct 思考-行动循环（最小骨架）：
/// 1) 调用 LLM 获得回复，若包含 tool_calls，则依次执行工具并将结果作为后续消息继续对话；
/// 2) 若无工具或工具不可用，降级为纯文本模式，返回模型输出。
/// </summary>
public class ReActEngine
{
    private readonly IChatClientService _chat;
    private readonly IToolExecutor _executor;

    public ReActEngine(IChatClientService chat, IToolExecutor executor)
    {
        _chat = chat;
        _executor = executor;
    }

    public async Task<string> RunAsync(LlmConfig config, IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<ITool>? tools = null, CancellationToken ct = default)
    {
        var history = messages.ToList();
        var toolMap = (tools ?? Array.Empty<ITool>()).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        // 首次调用
        var response = await _chat.SendMessageAsync(config, history);
        if (response.ToolCalls == null || response.ToolCalls.Count == 0)
        {
            return response.Message ?? string.Empty;
        }

        // 循环执行工具直至没有 tool_calls 或达到步数上限
        int steps = 0; const int maxSteps = 4;
        string lastModelText = response.Message ?? string.Empty;
        while (response.ToolCalls != null && response.ToolCalls.Count > 0 && steps < maxSteps)
        {
            steps++;
            foreach (var tc in response.ToolCalls)
            {
                if (!toolMap.TryGetValue(tc.Function?.Name ?? string.Empty, out var tool))
                {
                    // 工具不可用：降级，直接返回模型文本
                    return lastModelText;
                }

                JsonElement? args = null;
                if (!string.IsNullOrWhiteSpace(tc.Function?.Arguments))
                {
                    try { args = JsonSerializer.Deserialize<JsonElement>(tc.Function!.Arguments)!; } catch { args = null; }
                }

                var (ok, output, error) = await _executor.ExecuteAsync(tool, args, ct);
                var toolResult = ok ? output : ($"ERROR: {error}");

                // 将工具结果追加到对话，作为下一轮用户消息（函数结果注入）
                history.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, $"[Tool:{tool.Name}] {toolResult}"));
            }

            response = await _chat.SendMessageAsync(config, history);
            lastModelText = response.Message ?? string.Empty;
        }

        return lastModelText;
    }
}

