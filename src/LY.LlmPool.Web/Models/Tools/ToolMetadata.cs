namespace LY.LlmPool.Web.Models.Tools;

/// <summary>
/// 工具元数据 - 统一描述 App Tool 和 MCP Tool
/// </summary>
public class ToolMetadata
{
    /// <summary>
    /// 工具名称 (唯一标识符)
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// 工具描述
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// 工具来源 (App 或 MCP)
    /// </summary>
    public required ToolSource Source { get; set; }

    /// <summary>
    /// 源 ID - AppId (string) 或 McpServerId (string)
    /// </summary>
    public required string SourceId { get; set; }

    /// <summary>
    /// 参数 JSON Schema (OpenAPI 风格)
    /// </summary>
    public required string ParametersSchema { get; set; }

    /// <summary>
    /// 原始配置 (可选) - 用于存储完整的 ConfigJson
    /// </summary>
    public string? ConfigJson { get; set; }
}
