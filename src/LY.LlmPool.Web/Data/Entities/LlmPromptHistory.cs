using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LY.LlmPool.Web.Data.Entities;

[Table("llm_prompt_history")]
public class LlmPromptHistory
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = null!;

    [Required]
    [Column("prompt_id")]
    public string PromptId { get; set; } = null!;

    [Required]
    [Column("content")]
    public string Content { get; set; } = null!;

    [Column("version")]
    public int Version { get; set; }

    [Column("based_on_version")]
    public int? BasedOnVersion { get; set; }

    [Column("model_parameters")]
    public string? ModelParameters { get; set; }

    [Column("test_configs")]
    public string? TestConfigsJson { get; set; }

    [NotMapped]
    public List<TestConfigRecord> TestConfigs
    {
        get => TestConfigsJson != null ? JsonSerializer.Deserialize<List<TestConfigRecord>>(TestConfigsJson) ?? new() : new();
        set => TestConfigsJson = JsonSerializer.Serialize(value);
    }

    [Column("create_time")]
    public DateTime CreateTime { get; set; }

    [ForeignKey(nameof(PromptId))]
    [JsonIgnore]
    public LlmPrompt? Prompt { get; set; }
}

public class TestConfigRecord
{
    public string Type { get; set; } = "Model";
    public string? ConfigId { get; set; }
    public string? ConfigName { get; set; }
    public string? ModelType { get; set; }
    public string? Parameters { get; set; }
    public Dictionary<string, string> ParameterValues { get; set; } = new();
    public bool Success { get; set; }
    
    /// <summary>
    /// 测试输入内容（用户输入的文本）
    /// </summary>
    public string? TestInput { get; set; }
    
    /// <summary>
    /// 响应文本（保留向后兼容）
    /// </summary>
    public string? Response { get; set; }
    
    /// <summary>
    /// 完整的响应片段（包含文本和工具调用）- 已弃用，使用 ChatHistory
    /// </summary>
    public List<Models.ResponseSegment>? ResponseSegments { get; set; }
    
    /// <summary>
    /// 完整的聊天历史（包含用户消息、助手响应、工具调用等）
    /// </summary>
    public List<Microsoft.Extensions.AI.ChatMessage>? ChatHistory { get; set; }
    
    public string? Error { get; set; }
} 
