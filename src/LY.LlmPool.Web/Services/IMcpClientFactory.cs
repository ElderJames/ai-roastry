using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LY.LlmPool.Web.Services
{
    /// <summary>
    /// Factory abstraction to create an <see cref="IMcpClient"/> given a configured <see cref="HttpClient"/>.
    /// Implementations should wrap the official SDK (ModelContextProtocol) and adapt to IMcpClient.
    /// </summary>
    public interface IMcpClientFactory
    {
        Task<IMcpClient> CreateAsync(HttpClient client, CancellationToken ct = default);
    }
}
