using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LY.LlmPool.Web.Data.Entities;

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
    [MaxLength(20)]
    public string AppType { get; set; } = string.Empty; // "Prompt", "Agent"

    [Column("prompt_id")]
    public string? PromptId { get; set; }

    [Column("llm_config_id")]
    public string? LlmConfigId { get; set; }

    [Column("endpoint_id")]
    public string? EndpointId { get; set; }

    [Column("is_enabled")]
    public bool IsEnabled { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Column("config_json", TypeName = "jsonb")]
    public string? ConfigJson { get; set; }

    // Navigation properties
    public virtual LlmPrompt? Prompt { get; set; }
    public virtual LlmConfig? LlmConfig { get; set; }
    public virtual LlmEndpoint? Endpoint { get; set; }

    // Additional configuration for the app
    [NotMapped]
    public Dictionary<string, object> Config
    {
        get => string.IsNullOrEmpty(ConfigJson)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(ConfigJson) ?? new Dictionary<string, object>();
        set => ConfigJson = JsonSerializer.Serialize(value);
    }
}

public static class LlmAppTypes
{
    public const string Prompt = "Prompt";
    public const string Agent = "Agent";
    
    public static readonly string[] All = { Prompt, Agent };
}
