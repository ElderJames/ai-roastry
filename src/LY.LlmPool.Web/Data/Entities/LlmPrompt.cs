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

    /// <summary>
    /// 模型参数配置 (如: temp=0.7,tokens=100)
    /// </summary>
    [Column("model_parameters")]
    public string? ModelParameters { get; set; }

    /// <summary>
    /// 绑定的工具
    /// </summary>
    public virtual ICollection<PromptTool> PromptTools { get; set; } = new List<PromptTool>();
} 
