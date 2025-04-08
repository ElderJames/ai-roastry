using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LY.LlmPool.Web.Data.Entities;

public class LlmConfig
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(200)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    [NotMapped]
    public bool IsBusy { get; set; }

    [Required]
    public string ModelTypeId { get; set; } = string.Empty;

    [ForeignKey(nameof(ModelTypeId))]
    public virtual LlmModelType? ModelType { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

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
} 