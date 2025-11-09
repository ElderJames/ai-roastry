using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LY.LlmPool.Web.Data.Entities
{
    public enum OrchestrationMode { Sequential, GroupChat, DAG }

    [Table("llm_apps")]
    public class LlmApp
    {
        [Key]
        [Column("id")]
        public string? Id { get; set; }

        [Required]
        [Column("name")]
        [MaxLength(50)]
        public string Name { get; set; } = string.Empty;

        [Column("description")]
        [MaxLength(200)]
        public string? Description { get; set; }

        [Required]
        [Column("app_type")]
        public string AppType { get; set; } = "Prompt";

        [Column("orchestration_mode")]
        public OrchestrationMode? OrchestrationMode { get; set; }

        [Column("llm_prompt_id")]
        public string? LlmPromptId { get; set; }
        public virtual LlmPrompt? LlmPrompt { get; set; }

        [Column("llm_config_id")]
        public string? LlmConfigId { get; set; }
        public virtual LlmConfig? LlmConfig { get; set; }

        [Column("endpoint_id")]
        public string? EndpointId { get; set; }
        public virtual LlmEndpoint? Endpoint { get; set; }

        [Column("is_enabled")]
        public bool IsEnabled { get; set; } = true;

        [Column("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Column("config_json", TypeName = "jsonb")]
        public string? ConfigJson { get; set; }

        [NotMapped]
        public Dictionary<string, object> Config
        {
            get => string.IsNullOrEmpty(ConfigJson)
                ? new Dictionary<string, object>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(ConfigJson) ?? new Dictionary<string, object>();
            set => ConfigJson = System.Text.Json.JsonSerializer.Serialize(value);
        }

        // Navigation properties
        public virtual ICollection<AgentMember> AgentMembers { get; set; } = new List<AgentMember>();
    }
}
