using System.ComponentModel.DataAnnotations;

namespace LY.LlmPool.Web.Data.Entities;

public class LlmEndpoint
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public string Path { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<LlmEndpointConfig> EndpointConfigs { get; set; } = new List<LlmEndpointConfig>();
} 