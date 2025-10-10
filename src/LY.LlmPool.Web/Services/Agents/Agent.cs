using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 运行时 Agent：绑定提示词、模型配置与工具集。
/// </summary>
public class Agent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string? Role { get; init; }

    public LlmPrompt? Prompt { get; init; }
    public LlmConfig? Config { get; init; }

    public List<IAgentTool> Tools { get; } = new();

    public static Agent FromMember(AgentMember member)
    {
        return new Agent
        {
            Id = member.Id ?? Guid.NewGuid().ToString("N"),
            Name = member.Name ?? string.Empty,
            Role = member.Role,
            Prompt = member.LlmPrompt,
            Config = member.LlmConfig
        };
    }
}
