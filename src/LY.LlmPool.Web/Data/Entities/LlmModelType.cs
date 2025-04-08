using System.ComponentModel.DataAnnotations;

namespace LY.LlmPool.Web.Data.Entities;

public class LlmModelType
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(50)]
    public string Icon { get; set; } = "api";

    [MaxLength(200)]
    public string DefaultEndpoint { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<LlmConfig> Configs { get; set; } = new List<LlmConfig>();
}
