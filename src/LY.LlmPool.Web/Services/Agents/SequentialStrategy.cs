using System.Text;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.SemanticKernel;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 顺序编排策略：Agent 依次执行，后一个接收前一个的输出作为上下文。
/// </summary>
public class SequentialStrategy : IOrchestrationStrategy
{
    public async Task<string> ExecuteAsync(
        LlmApp app,
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> userMessages,
        ToolProviderService toolProviderService,
        Func<LlmConfig, List<Microsoft.Extensions.AI.ChatMessage>, IEnumerable<KernelFunction>?, Task<ChatResponse>> sendMessage,
        Func<LlmConfig, List<Microsoft.Extensions.AI.ChatMessage>, IEnumerable<KernelFunction>?, IAsyncEnumerable<string>> sendStreamingMessage,
        Func<string, string?, int, string, bool, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (app.AgentMembers == null || app.AgentMembers.Count == 0)
        {
            return "No agents configured.";
        }

        var ordered = app.AgentMembers.OrderBy(m => m.Order).ToList();
        string sharedContext = string.Empty;

        var step = 0;
        foreach (var member in ordered)
        {
            step++;
            if (member.LlmConfig == null)
            {
                continue; // 跳过无配置的成员
            }

            var messages = new List<Microsoft.Extensions.AI.ChatMessage>();

            // 添加系统提示
            if (member.LlmPrompt != null && !string.IsNullOrWhiteSpace(member.LlmPrompt.Content))
            {
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, member.LlmPrompt.Content));
            }

            // 构建用户消息：原始用户输入 + 共享上下文
            var userText = new StringBuilder();
            foreach (var um in userMessages)
            {
                var content = um.Text;
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
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, userText.ToString()));

            // 获取该成员关联的工具（通过 Prompt）
            var aiTools = await toolProviderService.GetToolsForPromptAsync(member.LlmPrompt);
            
            // TODO: Agent系统仍使用SemanticKernel的IChatClientService，暂时不支持AITool
            // 需要将整个Agent系统迁移到Microsoft.Extensions.AI才能使用工具
            IEnumerable<KernelFunction>? tools = null;

            // 如果有 onProgress 回调，使用流式调用
            if (onProgress != null)
            {
                var memberResponse = new StringBuilder();
                
                try
                {
                    await foreach (var chunk in sendStreamingMessage(member.LlmConfig, messages, tools).WithCancellation(ct))
                    {
                        memberResponse.Append(chunk);
                        // 直接传递原始 chunk,不添加前缀。前缀由 UI 层处理
                        try { await onProgress(member.Name ?? string.Empty, member.Role, step, chunk, false); } catch { }
                    }
                }
                catch (Exception)
                {
                    // 流式调用失败，跳过这个成员
                    continue;
                }
                
                sharedContext = memberResponse.ToString();
            }
            else
            {
                // 非流式：直接调用
                var resp = await sendMessage(member.LlmConfig, messages, tools);
                if (!string.Equals(resp.Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 跳过失败的成员
                }
                sharedContext = resp.Message ?? string.Empty;
            }
        }

        return string.IsNullOrWhiteSpace(sharedContext) ? "" : sharedContext;
    }
}


