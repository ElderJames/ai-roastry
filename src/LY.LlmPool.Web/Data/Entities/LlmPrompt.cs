using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LY.LlmPool.Web.Data.Entities;

[Table("llm_prompts")]
public class LlmPrompt
{
    [Key]
    [Column("id")]
    public string? Id { get; set; }

    [Required]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Required]
    [Column("content")]
    public string Content { get; set; } = string.Empty;

    [Column("create_time")]
    public DateTime CreateTime { get; set; }

    [Column("update_time")]
    public DateTime UpdateTime { get; set; }

    [Column("version")]
    public int Version { get; set; }
} 