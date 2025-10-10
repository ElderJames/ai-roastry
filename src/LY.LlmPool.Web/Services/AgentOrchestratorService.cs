using System.Text;
using System.Text.Json;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;

namespace LY.LlmPool.Web.Services;

public class AgentOrchestratorService
{
    private readonly ChatClientService _chatClientService;
    private readonly ILogger<AgentOrchestratorService> _logger;

    public AgentOrchestratorService(ChatClientService chatClientService, ILogger<AgentOrchestratorService> logger)
    {
        _chatClientService = chatClientService;
        _logger = logger;
    }

    public async Task<string> RunSequentialAsync(
        LlmApp app,
        IEnumerable<ChatMessage> userMessages,
        Func<LlmConfig, List<ChatMessage>, Task<ChatResponse>>? sendOverride = null,
        Func<string, string?, int, string, bool, Task>? onProgress = null)
    {
        if (app.AgentMembers == null || app.AgentMembers.Count == 0)
        {
            return "No agents configured.";
        }

        var ordered = app.AgentMembers.OrderBy(m => m.Order).ToList();
        string sharedContext = string.Empty;
        var sender = sendOverride ?? (async (cfg, msgs) => await _chatClientService.SendMessageAsync(cfg, msgs));

        var step = 0;
        foreach (var member in ordered)
        {
            step++;
            if (member.LlmConfig == null)
            {
                _logger.LogWarning("Agent {Name} missing LlmConfig, skipping", member.Name);
                continue;
            }

            var messages = new List<ChatMessage>();

            if (member.LlmPrompt != null && !string.IsNullOrWhiteSpace(member.LlmPrompt.Content))
            {
                messages.Add(new ChatMessage { Role = "system", Content = member.LlmPrompt.Content });
            }

            // 将用户请求与上一轮的共享上下文拼接给该 Agent
            var userText = new StringBuilder();
            foreach (var um in userMessages)
            {
                var content = um.Content;
                if (!string.IsNullOrEmpty(content))
                {
                    userText.AppendLine(content);
                }
            }
            if (!string.IsNullOrWhiteSpace(sharedContext))
            {
                userText.AppendLine("\n[Context]");
                userText.AppendLine(sharedContext);
            }
            messages.Add(new ChatMessage { Role = "user", Content = userText.ToString() });

            var resp = await sender(member.LlmConfig, messages);
            if (!string.Equals(resp.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Agent {Name} returned error: {Msg}", member.Name, resp.Message);
                continue;
            }

            sharedContext = resp.Message ?? string.Empty;
            if (onProgress != null)
            {
                try { await onProgress(member.Name ?? string.Empty, member.Role, step, sharedContext, false); } catch { }
            }
        }

        return string.IsNullOrWhiteSpace(sharedContext) ? "" : sharedContext;
    }

    public async Task<string> RunGroupChatAsync(
        LlmApp app,
        IEnumerable<ChatMessage> userMessages,
        Func<LlmConfig, List<ChatMessage>, Task<ChatResponse>>? sendOverride = null,
        Func<string, string?, int, string, bool, Task>? onProgress = null)
    {
        if (app.AgentMembers == null || app.AgentMembers.Count == 0)
        {
            return "No agents configured.";
        }

        var sender = sendOverride ?? (async (cfg, msgs) => await _chatClientService.SendMessageAsync(cfg, msgs));
        var members = app.AgentMembers.OrderBy(m => m.Order).ToList();

        // 读取配置：最大轮次，是否遇到 FINAL 提前结束
        var maxRounds = 1;
        var stopOnFinal = true;
        var roleOrder = new List<string> { "planner", "researcher", "moderator" };
        try
        {
            if (app.Config.TryGetValue("GroupChatMaxRounds", out var maxObj) && maxObj is JsonElement jeMax && jeMax.ValueKind == JsonValueKind.Number)
            {
                if (jeMax.TryGetInt32(out var mv)) maxRounds = Math.Max(1, Math.Min(10, mv));
            }
            else if (app.Config.TryGetValue("groupchat.maxRounds", out var maxObj2))
            {
                if (maxObj2 is JsonElement jeMax2 && jeMax2.ValueKind == JsonValueKind.Number && jeMax2.TryGetInt32(out var mv2))
                    maxRounds = Math.Max(1, Math.Min(10, mv2));
            }
            if (app.Config.TryGetValue("GroupChatStopOnFinal", out var stopObj) && stopObj is JsonElement jeStop)
            {
                if (jeStop.ValueKind == JsonValueKind.True) stopOnFinal = true;
                else if (jeStop.ValueKind == JsonValueKind.False) stopOnFinal = false;
            }

            // 角色顺序可配置：例如 "planner,researcher,moderator"
            if (app.Config.TryGetValue("GroupChatRoleOrder", out var roleObj) && roleObj is JsonElement jeRole)
            {
                if (jeRole.ValueKind == JsonValueKind.String)
                {
                    var val = jeRole.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        var parsed = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                        .Select(s => s.ToLowerInvariant())
                                        .Where(s => s.Length > 0)
                                        .ToList();
                        if (parsed.Count > 0) roleOrder = parsed;
                    }
                }
                else if (jeRole.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var el in jeRole.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.ToLowerInvariant());
                        }
                    }
                    if (list.Count > 0) roleOrder = list;
                }
            }
        }
        catch { }

        var sharedTranscript = new List<ChatMessage>();
        foreach (var m in userMessages)
        {
            sharedTranscript.Add(new ChatMessage { Role = m.Role, Content = m.Content });
        }

        var allRoundOutputs = new List<List<(string name, string text)>>();
        string? finalAnswer = null;

        for (int round = 1; round <= maxRounds; round++)
        {
            var roundOutputs = new List<(string name, string text)>();

            // 根据角色优先级（planner→researcher→moderator，或配置）决定本轮发言顺序
            var prioritized = members
                .OrderBy(m =>
                {
                    if (string.IsNullOrWhiteSpace(m.Role)) return int.MaxValue - 1;
                    var idx = roleOrder.FindIndex(r => string.Equals(r, m.Role, StringComparison.OrdinalIgnoreCase));
                    return idx >= 0 ? idx : int.MaxValue - 1;
                })
                .ThenBy(m => m.Order)
                .ToList();

            foreach (var member in prioritized)
            {
                if (member.LlmConfig == null)
                {
                    _logger.LogWarning("Agent {Name} missing LlmConfig, skipping", member.Name);
                    continue;
                }

                var msgs = new List<ChatMessage>();
                var systemText = new StringBuilder();
                systemText.Append($"[Round {round}] [Agent:{member.Name}; Role:{member.Role}] ");
                if (member.LlmPrompt != null && !string.IsNullOrWhiteSpace(member.LlmPrompt.Content))
                {
                    systemText.Append(member.LlmPrompt.Content);
                }
                // 为 Moderator/Planner 添加聚合指令提示
                if (!string.IsNullOrWhiteSpace(member.Role) && member.Role.Equals("moderator", StringComparison.OrdinalIgnoreCase))
                {
                    systemText.Append("\nYou are the moderator. Summarize prior messages and provide a concise decision. If the discussion is sufficient, output a final answer prefixed with 'FINAL:'.");
                }
                msgs.Add(new ChatMessage { Role = "system", Content = systemText.ToString() });

                // 提供当前共享的讨论历史
                msgs.AddRange(sharedTranscript.Select(x => new ChatMessage { Role = x.Role, Content = x.Content }));

                var resp = await sender(member.LlmConfig, msgs);
                if (!string.Equals(resp.Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Agent {Name} returned error in GroupChat: {Msg}", member.Name, resp.Message);
                    continue;
                }

                var text = resp.Message ?? string.Empty;
                roundOutputs.Add((member.Name, text));
                sharedTranscript.Add(new ChatMessage { Role = "assistant", Content = $"[{member.Name}] {text}" });

                if (onProgress != null)
                {
                    try { await onProgress(member.Name ?? string.Empty, member.Role, round, text, false); } catch { }
                }

                if (stopOnFinal && IsFinalAnswer(text, out var extracted))
                {
                    finalAnswer = extracted;
                    if (onProgress != null)
                    {
                        try { await onProgress(member.Name ?? string.Empty, member.Role, round, extracted!, true); } catch { }
                    }
                    break;
                }
            }

            if (roundOutputs.Count > 0)
            {
                allRoundOutputs.Add(roundOutputs);
            }

            if (!string.IsNullOrWhiteSpace(finalAnswer))
            {
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(finalAnswer))
        {
            return finalAnswer!;
        }

        if (allRoundOutputs.Count == 0) return string.Empty;

        // Moderator 兜底总结：找角色为 moderator 的成员做最终总结
        var moderator = members.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Role) && x.Role.Equals("moderator", StringComparison.OrdinalIgnoreCase));
        if (moderator?.LlmConfig != null)
        {
            var limit = 50; // 上下文消息条数限制
            try
            {
                if (app.Config.TryGetValue("GroupChatContextLimit", out var limObj) && limObj is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var v))
                {
                    limit = Math.Max(10, Math.Min(200, v));
                }
            }
            catch { }

            var take = Math.Min(limit, sharedTranscript.Count);
            var summaryMessages = new List<ChatMessage>();
            var sys = new StringBuilder();
            sys.Append("You are the moderator. Read the following transcript and produce a concise final answer. Use bullet points only if necessary. Respond in the user's language. Start with 'FINAL:' prefix.");
            if (moderator.LlmPrompt != null && !string.IsNullOrWhiteSpace(moderator.LlmPrompt.Content))
            {
                sys.Append("\n");
                sys.Append(moderator.LlmPrompt.Content);
            }
            summaryMessages.Add(new ChatMessage { Role = "system", Content = sys.ToString() });
            summaryMessages.AddRange(sharedTranscript.TakeLast(take).Select(x => new ChatMessage { Role = x.Role, Content = x.Content }));

            var resp = await sender(moderator.LlmConfig, summaryMessages);
            if (string.Equals(resp.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var text = resp.Message ?? string.Empty;
                if (IsFinalAnswer(text, out var extractedMod))
                {
                    if (onProgress != null)
                    {
                        try { await onProgress(moderator.Name ?? string.Empty, moderator.Role, maxRounds, extractedMod!, true); } catch { }
                    }
                    return extractedMod;
                }
                if (onProgress != null)
                {
                    try { await onProgress(moderator.Name ?? string.Empty, moderator.Role, maxRounds, text, true); } catch { }
                }
                return text;
            }
        }

        // 生成简易汇总输出
        var sb = new StringBuilder();
        for (int r = 0; r < allRoundOutputs.Count; r++)
        {
            if (r > 0) sb.AppendLine("\n======\n");
            sb.AppendLine($"Round {r + 1}:");
            var list = allRoundOutputs[r];
            for (int i = 0; i < list.Count; i++)
            {
                sb.AppendLine($"- {list[i].name}: {list[i].text}");
            }
        }
        return sb.ToString();
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
