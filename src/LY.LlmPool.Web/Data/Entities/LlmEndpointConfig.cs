using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace LY.LlmPool.Web.Data.Entities;

public class LlmEndpointConfig
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string EndpointId { get; set; } = string.Empty;

    [ForeignKey(nameof(EndpointId))]
    [JsonIgnore] // 忽略导航属性,避免序列化时的循环引用
    public virtual LlmEndpoint? Endpoint { get; set; }

    [Required]
    public string LlmConfigId { get; set; } = string.Empty;

    [ForeignKey(nameof(LlmConfigId))]
    [JsonIgnore] // 忽略导航属性,避免序列化时的循环引用
    public virtual LlmConfig? LlmConfig { get; set; }

    public int Priority { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
} 
