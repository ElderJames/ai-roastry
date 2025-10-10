using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Services
{
    /// <summary>
    /// Backwards-compatible factory that implements the application-level <see cref="IMcpClientFactory"/>.
    /// Delegates to <see cref="McpSdkClientFactory"/> which knows how to adapt the SDK types to IMcpClient.
    /// </summary>
    public class McpClientFactory : IMcpClientFactory
    {
        private readonly ILoggerFactory? _loggerFactory;

        public McpClientFactory(ILoggerFactory? loggerFactory = null)
        {
            _loggerFactory = loggerFactory;
        }

        public Task<IMcpClient> CreateAsync(HttpClient client, CancellationToken ct = default)
        {
            var sdkFactory = new McpSdkClientFactory(_loggerFactory);
            return sdkFactory.CreateAsync(client, ct);
        }
    }
}
