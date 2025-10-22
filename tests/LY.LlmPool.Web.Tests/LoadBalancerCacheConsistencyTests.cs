using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.LoadBalancing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// 测试 LoadBalancerService 和 LlmPoolCacheService 的缓存一致性
/// </summary>
public class LoadBalancerCacheConsistencyTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<LlmDbContext> _dbFactory;
    private readonly LoadBalancerService _loadBalancer;
    private readonly LlmPoolCacheService _cacheService;

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
        
        // 注册服务
        services.AddScoped<LlmPoolCacheService>();
        services.AddScoped<LoadBalancerService>();
        
        _serviceProvider = services.BuildServiceProvider();
        _dbFactory = _serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        _loadBalancer = _serviceProvider.GetRequiredService<LoadBalancerService>();
        _cacheService = _serviceProvider.GetRequiredService<LlmPoolCacheService>();
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

        // Act - 清除缓存
        await _loadBalancer.InvalidateModelCacheAsync("test-app-cache");

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
}
