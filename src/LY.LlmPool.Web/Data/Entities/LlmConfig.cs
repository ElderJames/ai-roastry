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

    public int MaxTokens { get; set; } = 2000;
    public float Temperature { get; set; } = 0.7f;
} 