using System.Text.Json;

namespace LY.LlmPool.Web.Services.Agents;

public class ToolExecutor : IToolExecutor
{
    public async Task<(bool ok, string output, string? error)> ExecuteAsync(IAgentTool tool, JsonElement? args, CancellationToken ct = default)
    {
        // Validate arguments first
        var (ok, err) = tool.Validate(args);
        if (!ok) return (false, string.Empty, err);
        
        // Execute the tool
        var result = await tool.ExecuteAsync(args, ct);
        return (true, result, null);
    }
}
