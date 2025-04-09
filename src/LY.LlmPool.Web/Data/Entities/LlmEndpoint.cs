using System.ComponentModel.DataAnnotations;

namespace LY.LlmPool.Web.Data.Entities;

public class LlmEndpoint
{
    [Key]
    public string Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<LlmEndpointConfig> EndpointConfigs { get; set; } = new List<LlmEndpointConfig>();
} 