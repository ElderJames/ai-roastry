using System.ComponentModel.DataAnnotations;

namespace LY.LlmPool.Web.Data.Entities
{
    public class AgentMember
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int Order { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string? ConfigJson { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string LlmAppId { get; set; } = string.Empty;
        public virtual LlmApp LlmApp { get; set; } = null!;

        public string? LlmPromptId { get; set; }
        public virtual LlmPrompt? LlmPrompt { get; set; }

        public string? LlmConfigId { get; set; }
        public virtual LlmConfig? LlmConfig { get; set; }
    }
}
