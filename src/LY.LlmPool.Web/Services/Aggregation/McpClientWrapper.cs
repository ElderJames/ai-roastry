using ModelContextProtocol.Client;

namespace LY.LlmPool.Web.Services.Aggregation
{
    public class McpClientWrapper : IAsyncDisposable
    {
        public string Name { get; private set; }

        private readonly McpServerConfigDto _config;

        private IClientTransport _clientTransport; // 改为可变，以便重新创建
        public McpClient McpClient { get; private set; }
        private readonly ILogger<McpClientWrapper>? _logger;

        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);
        // 添加连接状态追踪
        private DateTime _lastSuccessfulCheck = DateTime.MinValue;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(30);
        private bool _isConnected = false;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="name"></param>
        /// <param name="config"></param>
        /// <param name="loggerFactory"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public McpClientWrapper(string name, McpServerConfigDto config, ILoggerFactory? loggerFactory = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = loggerFactory?.CreateLogger<McpClientWrapper>();

            _clientTransport = McpClientTransportFactory.Create(name, config);

            // _transportClient = McpTransportClientFactory.Create(config, loggerFactory);
            // _transportClient.MessageReceived += OnMessageReceived;



            // var transport = await _clientTransport.ConnectAsync();

            // _mcpClient = await McpClientFactory.CreateAsync(_clientTransport);
        }

        public async Task InitializeAsync()
        {
            if (McpClient != null)
            {
                throw new InvalidOperationException("Client is already initialized.");
            }

            // Connect to the MCP server and create the client
            McpClient = await McpClient.CreateAsync(_clientTransport).ConfigureAwait(false);
            _isConnected = true;
            _lastSuccessfulCheck = DateTime.UtcNow;
            // Optionally, you can perform additional initialization here
            _logger?.LogInformation($"MCP Client '{Name}' initialized successfully.");
        }

        /// <summary>
        /// 检查连接健康状态，如果不健康则自动重连
        /// </summary>
        public async Task<bool> EnsureHealthyAsync(CancellationToken cancellationToken = default)
        {
            // 如果最近检查过且成功，则跳过（避免频繁检查）
            if (_isConnected && DateTime.UtcNow - _lastSuccessfulCheck < _healthCheckInterval)
            {
                return true;
            }
            await _reconnectLock.WaitAsync(cancellationToken);
            try
            {
                // 双重检查：可能在等待锁的过程中已经被其他线程检查过了
                if (_isConnected && DateTime.UtcNow - _lastSuccessfulCheck < _healthCheckInterval)
                {
                    return true;
                }
                // 使用 McpClient 本身进行健康检查
                if (!await PerformHealthCheckAsync(cancellationToken))
                {
                    _logger?.LogWarning($"MCP Client '{Name}' health check failed. Attempting reconnection...");
                    _isConnected = false;
                    return await ReconnectAsync(cancellationToken);
                }
                _isConnected = true;
                _lastSuccessfulCheck = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Error during health check for MCP Client '{Name}'");
                _isConnected = false;
                return false;
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        /// <summary>
        /// 执行健康检查
        /// </summary>
        private async Task<bool> PerformHealthCheckAsync(CancellationToken cancellationToken)
        {
            if (McpClient == null)
            {
                return false;
            }
            try
            {
                // 方式1: 使用 Ping（如果 MCP 协议支持）
                await McpClient.PingAsync(cancellationToken);

                // 方式2: 使用轻量级操作 - ListTools（推荐）
                // 这会真实地测试 McpClient 的连接状态
                //var result = await McpClient.ListToolsAsync();

                _logger?.LogDebug($"Health check (Ping) passed for MCP Client '{Name}'");
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger?.LogWarning($"MCP Client '{Name}' returned 404 - connection is stale, server may have restarted");
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogWarning($"MCP Client '{Name}' HTTP error: {ex.StatusCode} - {ex.Message}");
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogWarning($"MCP Client '{Name}' health check timed out: {ex.Message}");
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug($"Health check cancelled for MCP Client '{Name}'");
                throw; // 重新抛出取消异常
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, $"Health check failed for MCP Client '{Name}': {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// 重新连接 - 完全重建 Transport 和 Client
        /// </summary>
        private async Task<bool> ReconnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger?.LogInformation($"Reconnecting MCP Client '{Name}'...");
                // 1. 销毁旧客户端
                if (McpClient != null)
                {
                    try
                    {
                        await McpClient.DisposeAsync();
                        _logger?.LogDebug($"Disposed old McpClient for '{Name}'");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error disposing old McpClient during reconnection");
                    }
                    McpClient = null;
                }
                // 2. 销毁旧传输层（关键！必须重建 Transport 才能建立新的 HTTP 连接）
                if (_clientTransport is IDisposable disposableTransport)
                {
                    try
                    {
                        disposableTransport.Dispose();
                        _logger?.LogDebug($"Disposed old transport for '{Name}'");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error disposing old transport during reconnection");
                    }
                }
                else if (_clientTransport is IAsyncDisposable asyncDisposableTransport)
                {
                    try
                    {
                        await asyncDisposableTransport.DisposeAsync();
                        _logger?.LogDebug($"Disposed old async transport for '{Name}'");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error disposing old async transport during reconnection");
                    }
                }
                // 3. 重新创建传输层（关键！这样才能建立新的 HTTP 连接）
                _clientTransport = McpClientTransportFactory.Create(Name, _config);
                _logger?.LogDebug($"Created new transport for '{Name}'");
                // 4. 重新创建客户端
                McpClient = await McpClient.CreateAsync(_clientTransport).ConfigureAwait(false);
                _logger?.LogDebug($"Created new McpClient for '{Name}'");

                // 5. 验证新连接
                await McpClient.PingAsync(cancellationToken);

                _isConnected = true;
                _lastSuccessfulCheck = DateTime.UtcNow;
                _logger?.LogInformation($"MCP Client '{Name}' reconnected successfully.");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to reconnect MCP Client '{Name}': {ex.Message}");
                _isConnected = false;
                return false;
            }
        }
        /// <summary>
        /// 标记连接为失效，强制下次调用时重新检查
        /// </summary>
        public void InvalidateHealthCheck()
        {
            _isConnected = false;
            _lastSuccessfulCheck = DateTime.MinValue;
            _logger?.LogDebug($"Health check invalidated for MCP Client '{Name}'");
        }
        /// <summary>
        /// 清理资源
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _reconnectLock.WaitAsync();
            try
            { 
                _isConnected = false;
                if (McpClient != null)
                {
                    try
                    {
                        await McpClient.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, $"Error disposing McpClient for '{Name}'");
                    }
                    McpClient = null;
                }
                if (_clientTransport is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else if (_clientTransport is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                _clientTransport = null;
            }
            finally
            {
                _reconnectLock.Release();
            }
            _reconnectLock.Dispose();
        }
    }
}
