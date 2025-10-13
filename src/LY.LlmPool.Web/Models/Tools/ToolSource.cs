namespace LY.LlmPool.Web.Models.Tools;

/// <summary>
/// 工具来源类型
/// </summary>
public enum ToolSource
{
    /// <summary>
    /// App Tool - 来自 LlmApp (AppType = "Tool")
    /// </summary>
    App,

    /// <summary>
    /// MCP Tool - 来自 MCP Server
    /// </summary>
    MCP
}
