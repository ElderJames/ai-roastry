using System.Text.Json;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// MCP 工具占位实现：描述远端工具元信息；执行时返回占位结果，后续接入 MCP 客户端。
/// </summary>
public class McpTool : ITool
{
    public string Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public JsonElement? ParametersSchema { get; }

    public string McpServerId { get; }
    public string RemoteToolName { get; }

    public McpTool(string id, string name, string mcpServerId, string remoteToolName, string? description = null, JsonElement? schema = null)
    {
        Id = id; Name = name; McpServerId = mcpServerId; RemoteToolName = remoteToolName; Description = description; ParametersSchema = schema;
    }

    public (bool ok, string? error) Validate(JsonElement? args)
    {
        // 与 Internal 相同的最小校验
        if (ParametersSchema is JsonElement s && s.ValueKind == JsonValueKind.Object)
        {
            if (args is not JsonElement a || a.ValueKind != JsonValueKind.Object)
            {
                return (false, "args must be JSON object");
            }
        }
        return (true, null);
    }

    public Task<string> ExecuteAsync(JsonElement? args, CancellationToken ct = default)
    {
        // TODO: 后续通过 MCP 客户端执行远端工具
        return Task.FromResult($"MCP:{RemoteToolName} on {McpServerId} executed (stub)");
    }
}
