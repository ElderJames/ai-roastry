using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Net.Http;
using LY.LlmPool.Client.Telemetry;

namespace LY.LlmPool.Client.Extensions;

/// <summary>
/// LlmPool Client 服务注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加 LlmPool Client 和 OpenTelemetry 追踪配置
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="baseUrl">LlmPool API 基础 URL (例如: http://localhost:5071/v1)</param>
    /// <param name="apiKey">LlmPool API 密钥 (默认: test-key)</param>
    /// <param name="serviceName">服务名称 (用于 OpenTelemetry,默认: MCP Server)</param>
    /// <param name="serviceVersion">服务版本 (默认: 1.0.0)</param>
    /// <returns></returns>
    public static IServiceCollection AddLlmPoolClient(
        this IServiceCollection services,
        string baseUrl,
        string? apiKey = null,
        string? serviceName = null,
        string? serviceVersion = null)
    {
        apiKey ??= "test-key";
        serviceName ??= "MCP Server";
        serviceVersion ??= "1.0.0";

        // 确保 baseUrl 以 /v1 结尾
        if (!baseUrl.TrimEnd('/').EndsWith("/v1"))
        {
            baseUrl = baseUrl.TrimEnd('/') + "/v1";
        }

        var traceEndpoint = baseUrl.TrimEnd('/').Replace("/v1", "/v1/traces/json");

        // 注册 HttpClient
        services.AddHttpClient();

        // 配置 OpenTelemetry
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["service.version"] = serviceVersion,
                    ["mcp.server.name"] = serviceName
                }))
            .WithTracing(tracing =>
            {
                tracing
                    // 监听所有相关的 ActivitySource
                    .AddSource("Experimental.ModelContextProtocol") // MCP SDK
                    .AddSource("ModelContextProtocol")              // MCP SDK (备用名称)
                    .AddSource("Microsoft.Extensions.AI")
                    .AddSource("*")  // 监听所有 ActivitySource
                    
                    .AddHttpClientInstrumentation(options =>
                    {
                        // 过滤 HTTP 请求 - 只记录调用 LlmPool 的请求
                        options.FilterHttpRequestMessage = (httpRequestMessage) =>
                        {
                            var url = httpRequestMessage.RequestUri?.ToString() ?? string.Empty;
                            
                            // 过滤掉 trace exporter 的请求（避免循环追踪）
                            if (url.Contains("/v1/traces/json"))
                            {
                                return false;
                            }
                            
                            // 只记录调用 LlmPool API 的请求
                            return url.Contains("/v1/chat/completions");
                        };
                        
                        // 自定义 Activity 名称
                        options.EnrichWithHttpRequestMessage = (activity, httpRequestMessage) =>
                        {
                            if (httpRequestMessage.RequestUri?.PathAndQuery.Contains("/chat/completions") == true)
                            {
                                activity.DisplayName = "llmpool.client.chat";
                            }
                        };
                    })
                    
                    // 使用自定义 Exporter 导出到 LlmPool
                    .AddProcessor(sp =>
                    {
                        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                        var logger = sp.GetRequiredService<ILogger<LlmPoolActivityExporter>>();
                        var exporter = new LlmPoolActivityExporter(
                            httpClient,
                            traceEndpoint,
                            logger);
                        return new OpenTelemetry.BatchActivityExportProcessor(exporter);
                    });
            });

        // 注册 LlmPoolClient
        services.AddSingleton<LlmPoolClient>(sp =>
        {
            // 使用 IHttpClientFactory 创建 HttpClient，它已配置了 OpenTelemetry instrumentation
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("LlmPoolClient");
            httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            
            return new LlmPoolClient(httpClient, apiKey);
        });

        return services;
    }
}
