using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.Monitoring;
using LY.LlmPool.Web.Services.PromptCache;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// 工厂服务，用于根据 LlmConfig 创建 IChatClient 实例
/// </summary>
public class ChatClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChatClientFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LoggingHttpHandler>? _loggingLogger;
    private readonly ILogger<ParameterInjectingHandler>? _parameterLogger;
    private readonly ILogger<MonitoringDelegatingChatClient>? _monitoringLogger;
    private readonly ChatExecutionPersistenceService? _persistenceService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly PromptCacheService? _promptCacheService;

    public ChatClientFactory(
        IHttpClientFactory httpClientFactory,
        ILogger<ChatClientFactory> logger,
        ILoggerFactory loggerFactory,
        ILogger<LoggingHttpHandler>? loggingLogger = null,
        ILogger<ParameterInjectingHandler>? parameterLogger = null,
        ILogger<MonitoringDelegatingChatClient>? monitoringLogger = null,
        ChatExecutionPersistenceService? persistenceService = null,
        IHttpContextAccessor? httpContextAccessor = null,
        PromptCacheService? promptCacheService = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _loggingLogger = loggingLogger;
        _parameterLogger = parameterLogger;
        _monitoringLogger = monitoringLogger;
        _persistenceService = persistenceService;
        _httpContextAccessor = httpContextAccessor;
        _promptCacheService = promptCacheService;
    }

    /// <summary>
    /// 根据 LlmConfig 创建 IChatClient
    /// </summary>
    /// <param name="config">LLM 配置</param>
    /// <param name="enableFunctionInvocation">是否启用自动工具调用（默认 true）</param>
    /// <param name="parameters">要注入到请求中的额外参数（如 temperature, max_tokens）</param>
    /// <param name="enableLogging">是否启用 HTTP 请求/响应日志（默认 false）</param>
    /// <param name="enablePromptCache">是否启用 Prompt 缓存（默认 true）</param>
    /// <param name="enableLogging">是否启用 HTTP 请求/响应日志（默认 false）</param>
    public IChatClient CreateClient(
        LlmConfig config,
        bool enableFunctionInvocation = true,
        Dictionary<string, object>? parameters = null,
        bool enableLogging = false,
        bool enablePromptCache = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.BaseUrl, nameof(config.BaseUrl));
        ArgumentException.ThrowIfNullOrWhiteSpace(config.ApiKey, nameof(config.ApiKey));

        _logger.LogDebug("为配置 {ConfigName} 创建 ChatClient，BaseUrl: {BaseUrl}, Model: {Model}, FunctionInvocation: {FunctionInvocation}, PromptCache: {PromptCache}", 
            config.Name, config.BaseUrl, config.Model, enableFunctionInvocation, enablePromptCache);

        // 创建 HttpClient 和 DelegatingHandler 链
        HttpClient httpClient;
        if (parameters != null && parameters.Count > 0 || enableLogging)
        {
            httpClient = CreateHttpClientWithHandlers(parameters, enableLogging);
        }
        else
        {
            httpClient = new HttpClient();
        }

        // 创建 OpenAI 客户端
        var credential = new ApiKeyCredential(config.ApiKey);
        var openAiClient = new OpenAIClient(credential, new OpenAIClientOptions
        {
            Endpoint = new Uri(config.BaseUrl),
            Transport = new HttpClientPipelineTransport(httpClient)
        });

        // 获取 ChatClient 并转换为 Microsoft.Extensions.AI.IChatClient
        var chatClient = openAiClient.GetChatClient(config.Model).AsIChatClient();

        // 如果启用函数调用，使用 ChatClientBuilder 添加 FunctionInvokingChatClient
        if (enableFunctionInvocation)
        {
            // 🎯 构建 ChatClient 装饰器链:
            // 1. ConversationIdInjectingChatClient - 在最开始注入 ConversationId
            // 2. OpenTelemetry - 自动追踪
            // 3. FunctionInvokingChatClient - 自动工具调用
            // 4. ToolCallEventRecordingChatClient - 记录工具调用事件
            chatClient = new ChatClientBuilder(chatClient)
                .UseOpenTelemetry() // 🎯 启用 OpenTelemetry 自动追踪
                .UseFunctionInvocation(configure: functionClient =>
                {
                    // 🔑 启用并行工具调用（提升性能）
                    functionClient.AllowConcurrentInvocation = true;

                    _logger.LogDebug("启用了自动工具调用功能（并行执行: {Concurrent}）",
                        functionClient.AllowConcurrentInvocation);
                })
                .Use(innerClient => new Telemetry.ToolCallEventRecordingChatClient(
                    innerClient, 
                    _loggerFactory.CreateLogger<Telemetry.ToolCallEventRecordingChatClient>()))
                .Build();

            // 🎯 在最外层添加 ConversationId 注入（这样会在整个调用链开始时执行）
            if (_httpContextAccessor != null)
            {
                chatClient = new Decorators.ConversationIdInjectingChatClient(
                    chatClient,
                    _loggerFactory.CreateLogger<Decorators.ConversationIdInjectingChatClient>(),
                    _httpContextAccessor);
            }
            else
            {
                _logger.LogWarning("⚠️ IHttpContextAccessor not available, ConversationId injection disabled");
            }
        }
        else
        {
            // 🎯 即使不启用函数调用，也要添加 ConversationId 注入
            chatClient = new ChatClientBuilder(chatClient)
                .UseOpenTelemetry()
                .Build();

            if (_httpContextAccessor != null)
            {
                chatClient = new Decorators.ConversationIdInjectingChatClient(
                    chatClient,
                    _loggerFactory.CreateLogger<Decorators.ConversationIdInjectingChatClient>(),
                    _httpContextAccessor);
            }
        }

        // 🎯 添加 Prompt 缓存支持（在最外层，优先检查缓存）
        if (enablePromptCache && _promptCacheService != null)
        {
            chatClient = chatClient.WithPromptCache(
                _promptCacheService,
                _loggerFactory.CreateLogger<CachingChatClient>(),
                config.Id,
                config.Model);
            
            _logger.LogDebug("已为 ChatClient 启用 Prompt 缓存");
        }

        return chatClient;
    }

    /// <summary>
    /// 创建带有自定义 HttpClient 的 IChatClient（用于测试或特殊场景）
    /// </summary>
    /// <param name="config">LLM 配置</param>
    /// <param name="httpClientName">HttpClient 名称</param>
    /// <param name="enableFunctionInvocation">是否启用自动工具调用（默认 true）</param>
    /// <param name="parameters">要注入到请求中的额外参数（如 temperature, max_tokens）</param>
    /// <param name="enableLogging">是否启用 HTTP 请求/响应日志（默认 false）</param>
    public IChatClient CreateClientWithHttpClient(
        LlmConfig config,
        string httpClientName = "UpstreamLlm",
        bool enableFunctionInvocation = true,
        Dictionary<string, object>? parameters = null,
        bool enableLogging = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.ApiKey, nameof(config.ApiKey));

        var httpClient = _httpClientFactory.CreateClient(httpClientName);

        _logger.LogDebug("使用 HttpClient '{ClientName}' 为配置 {ConfigName} 创建 ChatClient", 
            httpClientName, config.Name);

        // 如果需要注入参数或日志，包装 HttpClient
        if (parameters != null && parameters.Count > 0 || enableLogging)
        {
            httpClient = WrapHttpClientWithHandlers(httpClient, parameters, enableLogging);
        }

        // 🔑 优先使用 HttpClient 的 BaseAddress，如果没有则使用 config.BaseUrl
        Uri endpoint;
        if (httpClient.BaseAddress != null)
        {
            endpoint = httpClient.BaseAddress;
            _logger.LogDebug("使用 HttpClient BaseAddress: {BaseAddress}", endpoint);
        }
        else if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            endpoint = new Uri(config.BaseUrl);
            _logger.LogDebug("使用 Config BaseUrl: {BaseUrl}", endpoint);
        }
        else
        {
            throw new ArgumentException("HttpClient.BaseAddress 和 Config.BaseUrl 都未设置");
        }

        // 使用自定义 HttpClient 创建 OpenAI 客户端
        var credential = new ApiKeyCredential(config.ApiKey);
        var openAiClient = new OpenAIClient(credential, new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(httpClient)
        });

        var chatClient = openAiClient.GetChatClient(config.Model).AsIChatClient();

        // 🎯 始终使用 ChatClientBuilder 添加中间件
        var builder = new ChatClientBuilder(chatClient);
        
        // 🎯 始终启用 OpenTelemetry（保持完整的追踪链路）
        builder = builder.UseOpenTelemetry();
        _logger.LogDebug("启用了 OpenTelemetry 自动追踪");
        
        // 根据参数决定是否启用自动工具调用
        if (enableFunctionInvocation)
        {
            builder = builder.UseFunctionInvocation(configure: functionClient =>
            {
                // 🔑 启用并行工具调用（提升性能）
                functionClient.AllowConcurrentInvocation = true;

                _logger.LogDebug("启用了自动工具调用功能（并行执行: {Concurrent}）",
                    functionClient.AllowConcurrentInvocation);
            });
        }
        
        chatClient = builder
            .Use(innerClient => new Telemetry.ToolCallEventRecordingChatClient(
                innerClient, 
                _loggerFactory.CreateLogger<Telemetry.ToolCallEventRecordingChatClient>()))
            .Build();

        // 🎯 在最外层添加 ConversationId 注入
        if (_httpContextAccessor != null)
        {
            chatClient = new Decorators.ConversationIdInjectingChatClient(
                chatClient,
                _loggerFactory.CreateLogger<Decorators.ConversationIdInjectingChatClient>(),
                _httpContextAccessor);
        }

        return chatClient;
    }

    /// <summary>
    /// 创建带有 DelegatingHandler 链的 HttpClient
    /// </summary>
    private HttpClient CreateHttpClientWithHandlers(
        Dictionary<string, object>? parameters,
        bool enableLogging)
    {
        // 构建处理器链：Logging -> Parameter -> HttpClientHandler
        HttpMessageHandler handler = new HttpClientHandler();

        // 从最内层开始构建链
        if (parameters != null && parameters.Count > 0)
        {
            handler = new ParameterInjectingHandler(parameters, _parameterLogger)
            {
                InnerHandler = handler
            };
            _logger.LogDebug("添加 ParameterInjectingHandler，参数数量: {Count}", parameters.Count);
        }

        if (enableLogging && _loggingLogger != null)
        {
            handler = new LoggingHttpHandler(_loggingLogger)
            {
                InnerHandler = handler
            };
            _logger.LogDebug("添加 LoggingHttpHandler");
        }

        return new HttpClient(handler);
    }

    /// <summary>
    /// 为已有的 HttpClient 包装 DelegatingHandler 链
    /// </summary>
    private HttpClient WrapHttpClientWithHandlers(
        HttpClient existingClient,
        Dictionary<string, object>? parameters,
        bool enableLogging)
    {
        // 注意: HttpClient 一旦创建就不能修改其 Handler
        // 这里我们创建新的带有处理器链的 HttpClient
        // 并从原 HttpClient 复制配置

        HttpMessageHandler handler = new HttpClientHandler();

        // 从最内层开始构建链
        if (parameters != null && parameters.Count > 0)
        {
            handler = new ParameterInjectingHandler(parameters, _parameterLogger)
            {
                InnerHandler = handler
            };
            _logger.LogDebug("添加 ParameterInjectingHandler，参数数量: {Count}", parameters.Count);
        }

        if (enableLogging && _loggingLogger != null)
        {
            handler = new LoggingHttpHandler(_loggingLogger)
            {
                InnerHandler = handler
            };
            _logger.LogDebug("添加 LoggingHttpHandler");
        }

        var newClient = new HttpClient(handler);
        
        // 复制原 HttpClient 的配置
        newClient.BaseAddress = existingClient.BaseAddress;
        newClient.Timeout = existingClient.Timeout;
        
        foreach (var header in existingClient.DefaultRequestHeaders)
        {
            newClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        return newClient;
    }

    /// <summary>
    /// 创建带有监控功能的 IChatClient
    /// </summary>
    /// <param name="config">LLM 配置</param>
    /// <param name="monitor">执行监控器</param>
    /// <param name="httpClientName">HttpClient 名称</param>
    /// <param name="enableFunctionInvocation">是否启用自动工具调用（默认 true）</param>
    /// <param name="parameters">要注入到请求中的额外参数</param>
    /// <param name="enableLogging">是否启用 HTTP 请求/响应日志（默认 false）</param>
    public IChatClient CreateClientWithMonitoring(
        LlmConfig config,
        ChatExecutionMonitor monitor,
        string httpClientName = "UpstreamLlm",
        bool enableFunctionInvocation = true,
        Dictionary<string, object>? parameters = null,
        bool enableLogging = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(monitor);

        // 设置监控器的模型名称
        monitor.ModelName = config.Model ?? config.Name;

        // 创建基础客户端
        var baseClient = CreateClientWithHttpClient(
            config,
            httpClientName,
            enableFunctionInvocation,
            parameters,
            enableLogging);

        // 如果没有监控日志记录器，直接返回基础客户端
        if (_monitoringLogger == null)
        {
            _logger.LogWarning("未配置监控日志记录器，跳过监控包装");
            return baseClient;
        }

        // 使用 MonitoringDelegatingChatClient 包装，并传入持久化服务
        var monitoringClient = new MonitoringDelegatingChatClient(
            baseClient,
            monitor,
            _monitoringLogger,
            _persistenceService);

        _logger.LogDebug("为配置 {ConfigName} 创建带监控的 ChatClient，RequestId: {RequestId}，持久化: {PersistenceEnabled}",
            config.Name, monitor.RequestId, _persistenceService != null);

        return monitoringClient;
    }
}
