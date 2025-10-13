using System.ComponentModel.DataAnnotations.Schema;

namespace LY.LlmPool.Web.Data.Entities
{
    /// <summary>
    /// Prompt 和 Tool 的关联表
    /// 一个 Prompt 可以绑定多个工具（App Tool 或 MCP Tool）
    /// </summary>
    [Table("prompt_tools")]
    public class PromptTool
    {
        /// <summary>
        /// Prompt ID
        /// </summary>
        [Column("prompt_id")]
        public string PromptId { get; set; } = string.Empty;
        
        public virtual LlmPrompt? Prompt { get; set; }

        /// <summary>
        /// Tool ID - 对于 App Tool 是 LlmApp.Id，对于 MCP Tool 是 MCP Server ID
        /// </summary>
        [Column("tool_id")]
        public string ToolId { get; set; } = string.Empty;

        /// <summary>
        /// Tool 类型：Internal (App Tool) 或 Mcp (MCP Tool)
        /// </summary>
        [Column("tool_type")]
        public ToolType ToolType { get; set; }
    }

    public enum ToolType { Internal, Mcp }
}
