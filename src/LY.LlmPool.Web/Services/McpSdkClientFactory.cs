using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol;

namespace LY.LlmPool.Web.Services
{
    public class McpSdkClientFactory : IMcpClientFactory
    {
        private readonly ILoggerFactory? _loggerFactory;

        public McpSdkClientFactory(ILoggerFactory? loggerFactory = null)
        {
            _loggerFactory = loggerFactory;
        }

        public async Task<IMcpClient> CreateAsync(HttpClient client, CancellationToken ct = default)
        {
            var transportOptions = new HttpClientTransportOptions()
            {
                Endpoint = client.BaseAddress ?? throw new InvalidOperationException("HttpClient must have BaseAddress"),
                TransportMode = HttpTransportMode.AutoDetect
            };

            var transport = new HttpClientTransport(transportOptions, client, _loggerFactory, false);

            var sdkClient = await McpClient.CreateAsync(transport, new McpClientOptions()
            {
                ClientInfo = new ModelContextProtocol.Protocol.Implementation { Name = "LY.LlmPool.Client", Version = "0.1" },
                Capabilities = new ModelContextProtocol.Protocol.ClientCapabilities()
            }, _loggerFactory, ct).ConfigureAwait(false);

            return new McpClientAdapter(sdkClient);
        }

        private class McpClientAdapter : IMcpClient
        {
            private readonly McpClient _inner;
            public McpClientAdapter(McpClient inner) { _inner = inner; }

            public async ValueTask DisposeAsync()
            {
                try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            public async IAsyncEnumerable<ToolDescriptor> EnumerateToolsAsync(JsonSerializerOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
            {
                await foreach (var t in _inner.EnumerateToolsAsync(McpJsonUtilities.DefaultOptions, ct))
                {
                    string? schema = null;
                    try { schema = t.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : t.JsonSchema.GetRawText(); } catch { schema = null; }
                    yield return new ToolDescriptor(t.Name ?? t.ProtocolTool?.Name ?? string.Empty, t.Title, t.Description, schema, t.ProtocolTool);
                }
            }

            public async Task<List<PromptDescriptor>> ListPromptsAsync(CancellationToken ct = default)
            {
                var res = new List<PromptDescriptor>();
                var ps = await _inner.ListPromptsAsync(ct).ConfigureAwait(false);
                foreach (var p in ps)
                {
                    res.Add(new PromptDescriptor(p.Name, p.Title, p.Description));
                }
                return res;
            }
        }
    }
}
