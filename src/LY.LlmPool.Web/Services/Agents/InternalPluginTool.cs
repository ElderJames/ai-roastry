using System.Text.Json;

namespace LY.LlmPool.Web.Services.Agents;

/// <summary>
/// 内部插件型工具的占位实现：校验参数并回显结果。
/// </summary>
public class InternalPluginTool : IAgentTool
{
    public string Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public JsonElement? ParametersSchema { get; }

    public InternalPluginTool(string id, string name, string? description = null, JsonElement? schema = null)
    {
        Id = id; Name = name; Description = description; ParametersSchema = schema;
    }

    public (bool ok, string? error) Validate(JsonElement? args)
    {
        // 最小实现：如存在 schema 且 schema 为 object，则要求 args 也是 object
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
        // 占位逻辑：返回一个简单字符串，后续可注入真实实现
        var payload = args is JsonElement a ? a.ToString() : "";
        return Task.FromResult($"{Name} executed. args={payload}");
    }
}
