using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LY.LlmPool.Web.Data.Entities;

public class LlmConfig
{
    [Key]
    public string? Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Description { get; set; }

    [Required]
    public string ModelTypeId { get; set; } = string.Empty;

    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string Model { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    [NotMapped]
    public bool IsBusy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual LlmModelType ModelType { get; set; } = null!;

    public virtual ICollection<LlmEndpointConfig> EndpointConfigs { get; set; } = new List<LlmEndpointConfig>();

    [Column(TypeName = "jsonb")]
    public string? AdditionalHeadersJson { get; set; }

    [NotMapped]
    public Dictionary<string, string> AdditionalHeaders
    {
        get => string.IsNullOrEmpty(AdditionalHeadersJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(AdditionalHeadersJson) ?? new Dictionary<string, string>();
        set => AdditionalHeadersJson = JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// 额外参数配置(JSON格式),支持配置 max_tokens, thinking_enabled 等参数
    /// 示例: {"max_tokens": 2000, "thinking_enabled": true, "top_p": 0.9}
    /// </summary>
    [Column("additional_parameters", TypeName = "jsonb")]
    public string? AdditionalParameters { get; set; }
} 
