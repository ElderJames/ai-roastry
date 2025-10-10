using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LY.LlmPool.Web.Data.Entities
{
    public class McpServerConfig
    {
        [Key]
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Command { get; set; }
        public string? Args { get; set; }
        public string? Env { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string? ConfigJson { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Column(TypeName = "jsonb")]
        public string? SchemaCacheJson { get; set; }
    }
}
