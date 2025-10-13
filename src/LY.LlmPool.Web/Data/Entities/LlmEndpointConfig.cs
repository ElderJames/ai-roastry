using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LY.LlmPool.Web.Data.Entities;

public class LlmEndpointConfig
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string EndpointId { get; set; } = string.Empty;

    [ForeignKey(nameof(EndpointId))]
    public virtual LlmEndpoint? Endpoint { get; set; }

    [Required]
    public string LlmConfigId { get; set; } = string.Empty;

    [ForeignKey(nameof(LlmConfigId))]
    public virtual LlmConfig? LlmConfig { get; set; }

    public int Priority { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
} 
