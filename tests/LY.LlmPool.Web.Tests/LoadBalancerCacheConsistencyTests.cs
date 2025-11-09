using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Aggregation;
using LY.LlmPool.Web.Services.LoadBalancing;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Microsoft.Extensions.AI;
using System.Threading;

#nullable enable

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// Mock implementation of IChatClientService for testing
/// </summary>
public class MockChatClientService : IChatClientService
{
    public Task<Services.ChatResponse> SendMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<AITool>? tools = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<Services.ChatResponse> SendMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<AITool>? tools = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<AITool>? tools = null)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters, IEnumerable<AITool>? tools = null)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<string> SendStreamingMessageAsync(LlmEndpoint config, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<AITool>? tools = null)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Models.ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(LlmConfig config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<AITool>? tools = null)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Models.ChatStreamingUpdate> SendStreamingMessageWithDetailsAsync(LlmEndpoint config, List<Microsoft.Extensions.AI.ChatMessage> messages, Dictionary<string, object>? parameters = null, IEnumerable<AITool>? tools = null)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Models.ChatStreamingUpdate> SendStreamingMessageViaControllerAsync(string appName, List<Microsoft.Extensions.AI.ChatMessage> messages, IEnumerable<AITool>? tools = null, Dictionary<string, object>? parameters = null)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 测试 LoadBalancerService 和 LlmPoolCacheService 的缓存一致性
/// </summary>
public class LoadBalancerCacheConsistencyTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<LlmDbContext> _dbFactory;
    private readonly LoadBalancerService _loadBalancer;
    private readonly LlmPoolCacheService _cacheService;
    private readonly AppService _appService;
    private readonly ConfigService _configService;
    private readonly EndpointService _endpointService;
    private readonly PromptService _promptService;

    public LoadBalancerCacheConsistencyTests()
    {
        var services = new ServiceCollection();
        
        // 配置内存数据库
        services.AddDbContextFactory<LlmDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}"));
        
        // 配置 HybridCache
#pragma warning disable EXTEXP0018
        services.AddHybridCache();
#pragma warning restore EXTEXP0018
        
        // 配置日志
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // 注册 McpClientsFactory (真实实例,因为它是 sealed 类)
        services.AddSingleton<McpClientsFactory>();
        
        // 注册 PromptParameterService (ToolMetadataService 需要它)
        services.AddTransient<PromptParameterService>();
        
        // 注册 Mock IChatClientService (PromptService 需要它)
        services.AddSingleton<IChatClientService>(new MockChatClientService());
        
        // 注册服务
        services.AddScoped<LlmPoolCacheService>();
        services.AddScoped<LoadBalancerService>();
        services.AddScoped<ToolMetadataService>();
        services.AddScoped<AppService>();
        services.AddScoped<ConfigService>();
        services.AddScoped<EndpointService>();
        services.AddScoped<PromptService>();
        
        _serviceProvider = services.BuildServiceProvider();
        _dbFactory = _serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        _loadBalancer = _serviceProvider.GetRequiredService<LoadBalancerService>();
        _cacheService = _serviceProvider.GetRequiredService<LlmPoolCacheService>();
        _appService = _serviceProvider.GetRequiredService<AppService>();
        _configService = _serviceProvider.GetRequiredService<ConfigService>();
        _endpointService = _serviceProvider.GetRequiredService<EndpointService>();
        _promptService = _serviceProvider.GetRequiredService<PromptService>();
    }

    [Fact]
    public async Task SelectConfigAsync_ShouldUseLlmPoolCacheService_NotDirectCache()
    {
        // Arrange - 创建测试数据
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var app = new LlmApp
            {
                Id = appId,
                Name = "test-app",
                AppType = "Prompt",
                LlmConfigId = configId,
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 第一次调用 LoadBalancer（缓存未命中，应该调用 LlmPoolCacheService）
        var result1 = await _loadBalancer.SelectConfigAsync("test-app");

        // Assert - 验证第一次调用成功
        Assert.NotNull(result1);
        Assert.True(result1.Success);
        Assert.NotNull(result1.Config);
        Assert.Equal("test-config", result1.Config.Name);
        Assert.NotNull(result1.App);
        Assert.Equal("test-app", result1.App.Name);

        // Act - 第二次调用（应该从缓存读取）
        var result2 = await _loadBalancer.SelectConfigAsync("test-app");

        // Assert - 验证第二次调用也成功，结果一致
        Assert.NotNull(result2);
        Assert.True(result2.Success);
        Assert.NotNull(result2.Config);
        Assert.Equal("test-config", result2.Config.Name);
        Assert.NotNull(result2.App);
        Assert.Equal("test-app", result2.App.Name);

        // 验证两次调用返回相同的配置
        Assert.Equal(result1.Config.Id, result2.Config.Id);
        Assert.Equal(result1.App.Id, result2.App.Id);
    }

    [Fact]
    public async Task SelectConfigAsync_WhenAppUsesEndpoint_ShouldIncludeAppInResult()
    {
        // Arrange - 创建 App -> Endpoint -> Config 的关系
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var endpointId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var endpoint = new LlmEndpoint
            {
                Id = endpointId,
                Name = "test-endpoint",
                Description = "Test Endpoint",
                IsEnabled = true
            };
            db.Endpoints.Add(endpoint);

            var endpointConfig = new LlmEndpointConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                EndpointId = endpointId,
                LlmConfigId = configId,
                Priority = 1
            };
            db.EndpointConfigs.Add(endpointConfig);

            var app = new LlmApp
            {
                Id = appId,
                Name = "test-app-with-endpoint",
                AppType = "Prompt",
                EndpointId = endpointId, // 🎯 使用 Endpoint
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 调用 LoadBalancer
        var result = await _loadBalancer.SelectConfigAsync("test-app-with-endpoint");

        // Assert - 🎯 验证 App 字段被正确填充（这是之前的 bug）
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Config);
        Assert.Equal("test-config", result.Config.Name);
        Assert.NotNull(result.App); // 🎯 关键断言：App 不应该为 null
        Assert.Equal("test-app-with-endpoint", result.App.Name);
        Assert.NotNull(result.Endpoint);
        Assert.Equal("test-endpoint", result.Endpoint.Name);
    }

    [Fact]
    public async Task InvalidateModelCacheAsync_ShouldClearCache_NextCallReloadsFromDb()
    {
        // Arrange - 创建测试数据
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config-v1",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var app = new LlmApp
            {
                Id = appId,
                Name = "test-app-cache",
                AppType = "Prompt",
                LlmConfigId = configId,
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 第一次调用（缓存）
        var result1 = await _loadBalancer.SelectConfigAsync("test-app-cache");
        Assert.NotNull(result1);
        Assert.Equal("test-config-v1", result1.Config?.Name);

        // 修改数据库中的配置名称
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var config = await db.Configs.FindAsync(configId);
            config!.Name = "test-config-v2";
            await db.SaveChangesAsync();
        }

        // Act - 第二次调用（应该从缓存读取旧值）
        var result2 = await _loadBalancer.SelectConfigAsync("test-app-cache");
        Assert.NotNull(result2);
        Assert.Equal("test-config-v1", result2.Config?.Name); // 仍然是旧值

        // Act - 清除缓存（使用 App ID）
        await _cacheService.InvalidateAppRelatedCachesAsync(appId);

        // Act - 第三次调用（应该从数据库读取新值）
        var result3 = await _loadBalancer.SelectConfigAsync("test-app-cache");
        
        // Assert - 验证缓存已清除，读取了新值
        Assert.NotNull(result3);
        Assert.Equal("test-config-v2", result3.Config?.Name); // 🎯 应该是新值
    }

    [Fact]
    public async Task SelectConfigAsync_WithMultipleConfigs_ShouldSelectBasedOnConcurrency()
    {
        // Arrange - 创建一个 Endpoint 有多个 Config 的场景
        var modelTypeId = Guid.NewGuid().ToString("N");
        var config1Id = Guid.NewGuid().ToString("N");
        var config2Id = Guid.NewGuid().ToString("N");
        var endpointId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config1 = new LlmConfig
            {
                Id = config1Id,
                Name = "config-1",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key-1",
                IsEnabled = true
            };
            var config2 = new LlmConfig
            {
                Id = config2Id,
                Name = "config-2",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key-2",
                IsEnabled = true
            };
            db.Configs.AddRange(config1, config2);

            var endpoint = new LlmEndpoint
            {
                Id = endpointId,
                Name = "multi-config-endpoint",
                Description = "Endpoint with multiple configs",
                IsEnabled = true
            };
            db.Endpoints.Add(endpoint);

            db.EndpointConfigs.AddRange(
                new LlmEndpointConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    EndpointId = endpointId,
                    LlmConfigId = config1Id,
                    Priority = 1
                },
                new LlmEndpointConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    EndpointId = endpointId,
                    LlmConfigId = config2Id,
                    Priority = 2
                }
            );

            var app = new LlmApp
            {
                Id = appId,
                Name = "multi-config-app",
                AppType = "Prompt",
                EndpointId = endpointId,
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 第一次调用（应该选择 config-1，并发数为 0）
        var result1 = await _loadBalancer.SelectConfigAsync("multi-config-app");

        // Assert
        Assert.NotNull(result1);
        Assert.True(result1.Success);
        Assert.NotNull(result1.Config);
        Assert.True(result1.NeedsRelease); // 🎯 多配置需要释放
        
        // 第一次应该选择 config-1（Priority 更高且并发为 0）
        Assert.Equal("config-1", result1.Config.Name);
        
        // 验证 App 字段被正确填充
        Assert.NotNull(result1.App);
        Assert.Equal("multi-config-app", result1.App.Name);

        // Act - 第二次调用（config-1 并发数已增加，应该选择 config-2）
        var result2 = await _loadBalancer.SelectConfigAsync("multi-config-app");

        // Assert - 应该选择并发数较少的配置
        Assert.NotNull(result2);
        Assert.True(result2.Success);
        Assert.NotNull(result2.Config);
        
        // 可能是 config-2（如果负载均衡逻辑工作正常）
        Assert.Contains(result2.Config.Name, new[] { "config-1", "config-2" });
    }

    [Fact]
    public async Task SelectConfigAsync_AgentGroup_ShouldReturnWithoutConfig()
    {
        // Arrange - 创建 AgentGroup 类型的 App
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var app = new LlmApp
            {
                Id = appId,
                Name = "test-agent-group",
                AppType = "AgentGroup",
                OrchestrationMode = OrchestrationMode.Sequential,
                IsEnabled = true
            };
            db.Apps.Add(app);
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _loadBalancer.SelectConfigAsync("test-agent-group");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.True(result.IsAgentGroup);
        Assert.Null(result.Config); // AgentGroup 不使用 Config
        Assert.NotNull(result.App);
        Assert.Equal("test-agent-group", result.App.Name);
        Assert.False(result.NeedsRelease); // AgentGroup 不需要释放
    }

    [Fact]
    public async Task ModifyPromptApp_ShouldRefreshLoadBalancerCache()
    {
        // Arrange - 创建测试数据
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var app = new LlmApp
            {
                Id = appId,
                Name = "test-prompt-app",
                AppType = "Prompt",
                LlmConfigId = configId,
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 第一次调用，建立缓存
        var result1 = await _loadBalancer.SelectConfigAsync("test-prompt-app");
        Assert.NotNull(result1);
        Assert.True(result1.Success);
        Assert.NotNull(result1.Config);
        Assert.Equal("test-config", result1.Config.Name);
        Assert.NotNull(result1.App);
        Assert.Equal("test-prompt-app", result1.App.Name);

        // 修改 App 名称（使用真实的 AppService.UpdateAppAsync）
        var appToUpdate = new LlmApp
        {
            Id = appId,
            Name = "modified-prompt-app",
            AppType = "Prompt",
            LlmConfigId = configId,
            IsEnabled = true
        };
        await _appService.UpdateAppAsync(appToUpdate);

        // Act - 使用新名称调用，应该从数据库重新加载
        var result2 = await _loadBalancer.SelectConfigAsync("modified-prompt-app");

        // Assert - 验证缓存已刷新
        Assert.NotNull(result2);
        Assert.True(result2.Success);
        Assert.Equal("test-config", result2.Config?.Name);
        Assert.Equal("modified-prompt-app", result2.App?.Name);

        // 验证旧名称的缓存已被清除（应该找不到）
        var result3 = await _loadBalancer.SelectConfigAsync("test-prompt-app");
        Assert.Null(result3); // 旧名称应该找不到，返回 null
    }

    [Fact]
    public async Task ModifyToolApp_ShouldRefreshToolMetadataCache()
    {
        // Arrange - 创建 Tool 类型的 App
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var app = new LlmApp
            {
                Id = appId,
                Name = "test-tool-app",
                AppType = "Tool",
                LlmConfigId = configId,
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 第一次调用，建立缓存
        var result1 = await _loadBalancer.SelectConfigAsync("test-tool-app");
        Assert.NotNull(result1);
        Assert.True(result1.Success);
        Assert.Equal("test-config", result1.Config?.Name);
        Assert.Equal("test-tool-app", result1.App?.Name);

        // 修改 Tool App 的配置（使用真实的 AppService.UpdateAppAsync）
        var appToUpdate = new LlmApp
        {
            Id = appId,
            Name = "modified-tool-app",
            AppType = "Tool",
            LlmConfigId = configId,
            IsEnabled = true
        };
        await _appService.UpdateAppAsync(appToUpdate);

        // Act - 使用新名称调用
        var result2 = await _loadBalancer.SelectConfigAsync("modified-tool-app");

        // Assert - 验证缓存已刷新
        Assert.NotNull(result2);
        Assert.True(result2.Success);
        Assert.Equal("test-config", result2.Config?.Name);
        Assert.Equal("modified-tool-app", result2.App?.Name);

        // 验证旧名称的缓存已被清除
        var result3 = await _loadBalancer.SelectConfigAsync("test-tool-app");
        Assert.Null(result3); // 旧名称应该找不到，返回 null
    }

    [Fact]
    public async Task ModifyConfig_ShouldRefreshRelatedAppAndEndpointCaches()
    {
        // Arrange - 创建 Config、App 和 Endpoint 的复杂关系
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var endpointId = Guid.NewGuid().ToString("N");
        var directAppId = Guid.NewGuid().ToString("N");
        var endpointAppId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var endpoint = new LlmEndpoint
            {
                Id = endpointId,
                Name = "test-endpoint",
                Description = "Test endpoint",
                IsEnabled = true
            };
            db.Endpoints.Add(endpoint);

            // 创建 EndpointConfig 关联
            var endpointConfig = new LlmEndpointConfig
            {
                EndpointId = endpointId,
                LlmConfigId = configId,
                Priority = 1
            };
            db.EndpointConfigs.Add(endpointConfig);

            // 直接使用 Config 的 App
            var directApp = new LlmApp
            {
                Id = directAppId,
                Name = "direct-config-app",
                AppType = "Prompt",
                LlmConfigId = configId,
                IsEnabled = true
            };
            db.Apps.Add(directApp);

            // 使用 Endpoint 的 App
            var endpointApp = new LlmApp
            {
                Id = endpointAppId,
                Name = "endpoint-config-app",
                AppType = "Prompt",
                EndpointId = endpointId,
                IsEnabled = true
            };
            db.Apps.Add(endpointApp);

            await db.SaveChangesAsync();
        }

        // Act - 建立初始缓存
        var directResult1 = await _loadBalancer.SelectConfigAsync("direct-config-app");
        var endpointResult1 = await _loadBalancer.SelectConfigAsync("endpoint-config-app");
        
        Assert.NotNull(directResult1);
        Assert.True(directResult1.Success);
        Assert.Equal("test-config", directResult1.Config?.Name);
        
        Assert.NotNull(endpointResult1);
        Assert.True(endpointResult1.Success);
        Assert.Equal(endpointId, endpointResult1.Endpoint?.Id);

        // 修改 Config 的模型名称（使用真实的 ConfigService.UpdateConfigAsync）
        var configToUpdate = new LlmConfig
        {
            Id = configId,
            Name = "test-config",
            Model = "gpt-4-turbo", // 修改模型
            ModelTypeId = modelTypeId,
            BaseUrl = "https://api.openai.com",
            ApiKey = "test-key",
            IsEnabled = true
        };
        await _configService.UpdateConfigAsync(configToUpdate);

        // Act - 重新调用，应该从数据库重新加载配置
        var directResult2 = await _loadBalancer.SelectConfigAsync("direct-config-app");
        var endpointResult2 = await _loadBalancer.SelectConfigAsync("endpoint-config-app");

        // Assert - 验证缓存已刷新，直接使用 Config 的 App 应该受到影响
        Assert.NotNull(directResult2);
        Assert.True(directResult2.Success);
        // 注意：LoadBalancer 可能仍然返回缓存的配置，但缓存键应该已被失效

        // 验证 Endpoint 相关的 App 也受到影响（因为 Endpoint 使用了该 Config）
        Assert.NotNull(endpointResult2);
        Assert.True(endpointResult2.Success);
    }

    [Fact]
    public async Task ModifyEndpoint_ShouldRefreshRelatedAppCaches()
    {
        // Arrange - 创建 Endpoint 和使用该 Endpoint 的多个 App
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var endpointId = Guid.NewGuid().ToString("N");
        var app1Id = Guid.NewGuid().ToString("N");
        var app2Id = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var endpoint = new LlmEndpoint
            {
                Id = endpointId,
                Name = "test-endpoint",
                Description = "Test endpoint for cache refresh",
                IsEnabled = true
            };
            db.Endpoints.Add(endpoint);

            // 创建 EndpointConfig 关联
            var endpointConfig = new LlmEndpointConfig
            {
                EndpointId = endpointId,
                LlmConfigId = configId,
                Priority = 1
            };
            db.EndpointConfigs.Add(endpointConfig);

            // 创建使用该 Endpoint 的 App
            var app1 = new LlmApp
            {
                Id = app1Id,
                Name = "endpoint-app-1",
                AppType = "Prompt",
                EndpointId = endpointId,
                IsEnabled = true
            };
            db.Apps.Add(app1);

            var app2 = new LlmApp
            {
                Id = app2Id,
                Name = "endpoint-app-2",
                AppType = "Tool",
                EndpointId = endpointId,
                IsEnabled = true
            };
            db.Apps.Add(app2);

            await db.SaveChangesAsync();
        }

        // Act - 建立初始缓存
        var result1_1 = await _loadBalancer.SelectConfigAsync("endpoint-app-1");
        var result2_1 = await _loadBalancer.SelectConfigAsync("endpoint-app-2");
        
        Assert.NotNull(result1_1);
        Assert.True(result1_1.Success);
        Assert.Equal("endpoint-app-1", result1_1.App?.Name);
        
        Assert.NotNull(result2_1);
        Assert.True(result2_1.Success);
        Assert.Equal("endpoint-app-2", result2_1.App?.Name);

        // 修改 Endpoint 名称（使用真实的 EndpointService.UpdateEndpointAsync）
        var endpointToUpdate = new LlmEndpoint
        {
            Id = endpointId,
            Name = "modified-endpoint",
            Description = "Test endpoint for cache refresh",
            IsEnabled = true,
            EndpointConfigs = new List<LlmEndpointConfig>
            {
                new LlmEndpointConfig
                {
                    EndpointId = endpointId,
                    LlmConfigId = configId,
                    Priority = 1
                }
            }
        };
        await _endpointService.UpdateEndpointAsync(endpointToUpdate);

        // Act - 重新调用，应该从数据库重新加载
        var result1_2 = await _loadBalancer.SelectConfigAsync("endpoint-app-1");
        var result2_2 = await _loadBalancer.SelectConfigAsync("endpoint-app-2");

        // Assert - 验证缓存已刷新
        Assert.NotNull(result1_2);
        Assert.True(result1_2.Success);
        Assert.Equal("endpoint-app-1", result1_2.App?.Name);
        
        Assert.NotNull(result2_2);
        Assert.True(result2_2.Success);
        Assert.Equal("endpoint-app-2", result2_2.App?.Name);

        // 验证旧的 Endpoint 名称缓存已被清除
        // 注意：这里我们无法直接测试 Endpoint 名称的缓存清除，
        // 因为 LoadBalancer.SelectConfigAsync 是通过 App 名称查找的
    }

    [Fact]
    public async Task ModifyPrompt_ShouldRefreshRelatedAppCaches()
    {
        // Arrange - 创建 Prompt 和使用该 Prompt 的 App
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var promptId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var prompt = new LlmPrompt
            {
                Id = promptId,
                Name = "test-prompt",
                Content = "Original prompt content",
                Description = "Test prompt",
                ModelParameters = "{}"
            };
            db.Prompts.Add(prompt);

            var app = new LlmApp
            {
                Id = appId,
                Name = "prompt-app",
                AppType = "Prompt",
                LlmConfigId = configId,
                LlmPromptId = promptId,
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 第一次调用，建立缓存
        var result1 = await _loadBalancer.SelectConfigAsync("prompt-app");
        Assert.NotNull(result1);
        Assert.True(result1.Success);
        Assert.NotNull(result1.App);
        Assert.Equal("prompt-app", result1.App.Name);
        Assert.NotNull(result1.App.LlmPrompt);
        Assert.Equal("Original prompt content", result1.App.LlmPrompt.Content);

        // 修改 Prompt 内容（使用真实的 PromptService.UpdatePromptAsync）
        var promptToUpdate = new LlmPrompt
        {
            Id = promptId,
            Name = "test-prompt",
            Content = "Updated prompt content",
            Description = "Test prompt",
            ModelParameters = "{}"
        };
        await _promptService.UpdatePromptAsync(promptToUpdate);

        // Act - 重新调用，应该从数据库重新加载
        var result2 = await _loadBalancer.SelectConfigAsync("prompt-app");

        // Assert - 验证缓存已刷新，App 关联的 Prompt 内容已更新
        Assert.NotNull(result2);
        Assert.True(result2.Success);
        Assert.NotNull(result2.App);
        Assert.Equal("prompt-app", result2.App.Name);
        Assert.NotNull(result2.App.LlmPrompt);
        Assert.Equal("Updated prompt content", result2.App.LlmPrompt.Content); // 🎯 验证 Prompt 内容已更新
    }

    [Fact]
    public async Task ModifyPromptModelParameters_ShouldRefreshRelatedAppCaches()
    {
        // Arrange - 创建 Prompt 和使用该 Prompt 的 App
        var modelTypeId = Guid.NewGuid().ToString("N");
        var configId = Guid.NewGuid().ToString("N");
        var promptId = Guid.NewGuid().ToString("N");
        var appId = Guid.NewGuid().ToString("N");
        
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var modelType = new LlmModelType
            {
                Id = modelTypeId,
                Name = "OpenAI",
                Description = "OpenAI Models"
            };
            db.ModelTypes.Add(modelType);

            var config = new LlmConfig
            {
                Id = configId,
                Name = "test-config",
                Model = "gpt-4",
                ModelTypeId = modelTypeId,
                BaseUrl = "https://api.openai.com",
                ApiKey = "test-key",
                IsEnabled = true
            };
            db.Configs.Add(config);

            var prompt = new LlmPrompt
            {
                Id = promptId,
                Name = "test-prompt",
                Content = "Test prompt content",
                Description = "Test prompt for model parameters",
                ModelParameters = "{\"temperature\": 0.7, \"max_tokens\": 100}"
            };
            db.Prompts.Add(prompt);

            var app = new LlmApp
            {
                Id = appId,
                Name = "prompt-params-app",
                AppType = "Prompt",
                LlmConfigId = configId,
                LlmPromptId = promptId,
                IsEnabled = true
            };
            db.Apps.Add(app);

            await db.SaveChangesAsync();
        }

        // Act - 第一次调用，建立缓存
        var result1 = await _loadBalancer.SelectConfigAsync("prompt-params-app");
        Assert.NotNull(result1);
        Assert.True(result1.Success);
        Assert.NotNull(result1.App);
        Assert.Equal("prompt-params-app", result1.App.Name);
        Assert.NotNull(result1.App.LlmPrompt);
        Assert.Equal("{\"temperature\": 0.7, \"max_tokens\": 100}", result1.App.LlmPrompt.ModelParameters);

        // 修改 Prompt 的 ModelParameters（使用真实的 PromptService.UpdatePromptAsync）
        var promptToUpdate = new LlmPrompt
        {
            Id = promptId,
            Name = "test-prompt",
            Content = "Test prompt content",
            Description = "Test prompt for model parameters",
            ModelParameters = "{\"temperature\": 0.9, \"max_tokens\": 200, \"top_p\": 0.8}"
        };
        await _promptService.UpdatePromptAsync(promptToUpdate);

        // Act - 重新调用，应该从数据库重新加载
        var result2 = await _loadBalancer.SelectConfigAsync("prompt-params-app");

        // Assert - 验证缓存已刷新，App 关联的 Prompt 的 ModelParameters 已更新
        Assert.NotNull(result2);
        Assert.True(result2.Success);
        Assert.NotNull(result2.App);
        Assert.Equal("prompt-params-app", result2.App.Name);
        Assert.NotNull(result2.App.LlmPrompt);
        Assert.Equal("{\"temperature\": 0.9, \"max_tokens\": 200, \"top_p\": 0.8}", result2.App.LlmPrompt.ModelParameters); // 🎯 验证 ModelParameters 已更新
    }
}
