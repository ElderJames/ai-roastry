using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LY.LlmPool.Web.Services
{
    public record ToolDescriptor(string Name, string? Title, string? Description, string? JsonSchema, object? ProtocolTool = null);
    public record PromptDescriptor(string Name, string? Title, string? Description);

    public interface IMcpClient : IAsyncDisposable
    {
        IAsyncEnumerable<ToolDescriptor> EnumerateToolsAsync(JsonSerializerOptions? options = null, CancellationToken ct = default);
        Task<List<PromptDescriptor>> ListPromptsAsync(CancellationToken ct = default);
    }
}
