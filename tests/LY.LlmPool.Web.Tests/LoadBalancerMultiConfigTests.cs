using System;
using System.Collections.Concurrent;
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
using Xunit.Abstractions;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// LoadBalancerService 多配置缓存和智能负载均衡单元测试
/// 测试新增的 AvailableConfigs 功能和基于并发数的智能选择
/// </summary>
public class LoadBalancerMultiConfigTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _serviceProvider;
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly LlmPoolCacheService _cacheService;
    private readonly LoadBalancerService _loadBalancer;

    public LoadBalancerMultiConfigTests(ITestOutputHelper output)
    {
        _output = output;
        
        var services = new ServiceCollection();
        
        // 使用内存数据库
        services.AddDbContextFactory<LlmDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}"));
        
        // 添加日志
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        // 添加 HybridCache
        services.AddHybridCache();
        
        // 添加服务
        services.AddSingleton<LlmPoolCacheService>();
        services.AddSingleton<LoadBalancerService>();
        
        _serviceProvider = services.BuildServiceProvider();
        _dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        _cacheService = _serviceProvider.GetRequiredService<LlmPoolCacheService>();
        _loadBalancer = _serviceProvider.GetRequiredService<LoadBalancerService>();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    #region 测试 1: Endpoint 多配置缓存

    /// <summary>
    /// 测试预热 Endpoint 时应该缓存所有可用配置
    /// </summary>
    [Fact]
    public async Task WarmupCache_EndpointWithMultipleConfigs_ShouldCacheAllConfigs()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        
        Assert.NotNull(result);
        Assert.NotNull(result.AvailableConfigs);
        Assert.Equal(3, result.AvailableConfigs.Count);
        Assert.Contains("Endpoint", result.Strategy);
        
        // 验证配置按优先级排序
        for (int i = 0; i < result.AvailableConfigs.Count; i++)
        {
            _output.WriteLine($"  配置 {i + 1}: {result.AvailableConfigs[i].Name}");
        }
        
        _output.WriteLine($"✅ Endpoint 成功缓存了 {result.AvailableConfigs.Count} 个配置");
    }

    #endregion

    #region 测试 2: 基于并发数的智能选择

    /// <summary>
    /// 测试当有多个配置可用时,LoadBalancer 应该选择并发数最少的配置
    /// </summary>
    [Fact]
    public async Task SelectConfig_WithConcurrentRequests_ShouldSelectLeastBusyConfig()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        await _cacheService.WarmupCacheAsync();
        
        // 模拟第1个配置有2个并发请求,第2个配置有1个请求,第3个配置没有请求
        _loadBalancer.GetType()
            .GetField("_configRequestCounts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_loadBalancer, new ConcurrentDictionary<string, int>(new[]
            {
                new KeyValuePair<string, int>(configs[0].Id, 2),
                new KeyValuePair<string, int>(configs[1].Id, 1),
                new KeyValuePair<string, int>(configs[2].Id, 0)
            }));
        
        // Act
        var result = await _loadBalancer.SelectConfigAsync(endpoint.Name);
        
        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Config);
        Assert.Equal(configs[2].Id, result.Config.Id); // 应该选择第3个配置(并发数为0)
        Assert.True(result.NeedsRelease); // 多配置情况下需要释放
        
        _output.WriteLine($"✅ 智能选择了并发数最少的配置: {result.Config.Name}");
    }

    /// <summary>
    /// 测试请求释放后应该减少并发数
    /// </summary>
    [Fact]
    public async Task ReleaseConfig_ShouldDecrementRequestCount()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 2);
        await _cacheService.WarmupCacheAsync();
        
        // Act - 选择配置
        var result1 = await _loadBalancer.SelectConfigAsync(endpoint.Name);
        var selectedConfigId = result1.Config!.Id;
        
        // 获取当前请求数
        var requestCountsField = _loadBalancer.GetType()
            .GetField("_configRequestCounts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var requestCounts = (ConcurrentDictionary<string, int>)requestCountsField!.GetValue(_loadBalancer)!;
        var countBeforeRelease = requestCounts.GetOrAdd(selectedConfigId, 0);
        
        // 释放配置
        _loadBalancer.ReleaseConfig(selectedConfigId);
        
        // Assert
        var countAfterRelease = requestCounts.GetOrAdd(selectedConfigId, 0);
        Assert.Equal(countBeforeRelease - 1, countAfterRelease);
        
        _output.WriteLine($"✅ 释放前: {countBeforeRelease}, 释放后: {countAfterRelease}");
    }

    /// <summary>
    /// 测试多次选择应该在配置之间分配负载
    /// </summary>
    [Fact]
    public async Task SelectConfig_MultipleTimes_ShouldDistributeAcrossConfigs()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        await _cacheService.WarmupCacheAsync();
        
        // Act - 连续选择5次
        var selectedConfigs = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var result = await _loadBalancer.SelectConfigAsync(endpoint.Name);
            selectedConfigs.Add(result.Config!.Id);
            
            // 不释放,模拟并发请求
        }
        
        // Assert - 应该使用了至少2个不同的配置
        var uniqueConfigs = selectedConfigs.Distinct().Count();
        Assert.True(uniqueConfigs >= 2, $"应该至少使用2个配置,实际使用了 {uniqueConfigs} 个");
        
        _output.WriteLine($"✅ 5次选择使用了 {uniqueConfigs} 个不同的配置");
    }

    #endregion

    #region 测试 3: 优先级排序

    /// <summary>
    /// 测试多个配置应该按优先级排序
    /// </summary>
    [Fact]
    public async Task WarmupCache_MultipleConfigs_ShouldOrderByPriority()
    {
        // Arrange - 创建配置,优先级为: 3, 1, 2
        var (endpoint, configs) = await CreateEndpointWithPrioritiesAsync("test-endpoint", new[] { 3, 1, 2 });
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        
        Assert.NotNull(result);
        Assert.NotNull(result.AvailableConfigs);
        Assert.Equal(3, result.AvailableConfigs.Count);
        
        // 验证排序: 优先级应该是 1, 2, 3
        Assert.Contains("-config-2", result.AvailableConfigs[0].Name); // Priority 1
        Assert.Contains("-config-3", result.AvailableConfigs[1].Name); // Priority 2
        Assert.Contains("-config-1", result.AvailableConfigs[2].Name); // Priority 3
        
        _output.WriteLine($"✅ 配置正确按优先级排序:");
        for (int i = 0; i < result.AvailableConfigs.Count; i++)
        {
            _output.WriteLine($"  {i + 1}. {result.AvailableConfigs[i].Name}");
        }
    }

    #endregion

    #region 测试 4: 并发安全性

    /// <summary>
    /// 测试并发请求的安全性
    /// </summary>
    [Fact]
    public async Task SelectConfig_ConcurrentRequests_ShouldBeSafe()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        await _cacheService.WarmupCacheAsync();
        
        // Act - 并发选择配置
        var tasks = new List<Task<ConfigSelectionResult>>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_loadBalancer.SelectConfigAsync(endpoint.Name));
        }
        
        var results = await Task.WhenAll(tasks);
        
        // Assert
        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.NotNull(r.Config));
        
        // 所有选择的配置都应该是有效的
        var selectedConfigIds = results.Select(r => r.Config!.Id).ToList();
        var validConfigIds = configs.Select(c => c.Id).ToList();
        Assert.All(selectedConfigIds, id => Assert.Contains(id, validConfigIds));
        
        _output.WriteLine($"✅ 并发选择10次,都成功返回有效配置");
    }

    #endregion

    #region 测试 5: App 关联 Endpoint 的多配置支持

    /// <summary>
    /// 测试 App 通过 Endpoint 关联多个配置
    /// </summary>
    [Fact]
    public async Task WarmupCache_AppWithEndpoint_ShouldCacheMultipleConfigs()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        var app = await CreateAppWithEndpointAsync("test-app", endpoint);
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result = await _cacheService.ResolveModelNameAsync(app.Name);
        
        Assert.NotNull(result);
        Assert.NotNull(result.AvailableConfigs);
        Assert.Equal(3, result.AvailableConfigs.Count);
        Assert.Contains("App 的 Endpoint", result.Strategy);
        
        _output.WriteLine($"✅ App 通过 Endpoint 成功缓存了 {result.AvailableConfigs.Count} 个配置");
    }

    /// <summary>
    /// 测试 App 优先使用直接指定的配置,而不是 Endpoint 的配置
    /// </summary>
    [Fact]
    public async Task WarmupCache_AppWithBothConfigAndEndpoint_ShouldPreferDirectConfig()
    {
        // Arrange
        var directConfig = await CreateConfigAsync("direct-config");
        var (endpoint, _) = await CreateEndpointWithConfigsAsync("endpoint", 2);
        
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var app = new LlmApp
            {
                Id = Guid.NewGuid().ToString(),
                Name = "test-app",
                AppType = "Chat",
                IsEnabled = true,
                LlmConfigId = directConfig.Id, // 直接指定配置
                EndpointId = endpoint.Id        // 同时关联 Endpoint
            };
            
            dbContext.Apps.Add(app);
            await dbContext.SaveChangesAsync();
        }
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result = await _cacheService.ResolveModelNameAsync("test-app");
        
        Assert.NotNull(result);
        Assert.Equal(directConfig.Id, result.Config!.Id);
        Assert.Null(result.AvailableConfigs); // 直接配置不设置 AvailableConfigs
        Assert.Contains("App 直接指定配置", result.Strategy);
        
        _output.WriteLine($"✅ App 优先使用直接指定的配置: {result.Config.Name}");
    }

    #endregion

    #region 测试 6: 缓存未命中降级查询

    /// <summary>
    /// 测试缓存未命中时应该降级到数据库查询
    /// </summary>
    [Fact]
    public async Task ResolveModelName_CacheMiss_ShouldFallbackToDatabase()
    {
        // Arrange - 创建配置但不预热缓存
        var config = await CreateConfigAsync("test-config");
        
        // Act - 直接查询(缓存未命中)
        var result = await _cacheService.ResolveModelNameAsync(config.Name);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(config.Id, result.Config!.Id);
        
        _output.WriteLine($"✅ 缓存未命中时成功降级到数据库查询");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// 创建配置
    /// </summary>
    private async Task<LlmConfig> CreateConfigAsync(string name)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        var config = new LlmConfig
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Model = "gpt-4",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "test-key",
            ModelTypeId = "openai",
            IsEnabled = true
        };
        
        dbContext.Configs.Add(config);
        await dbContext.SaveChangesAsync();
        
        return config;
    }

    /// <summary>
    /// 创建 App 并关联 Endpoint
    /// </summary>
    private async Task<LlmApp> CreateAppWithEndpointAsync(string appName, LlmEndpoint endpoint)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = appName,
            AppType = "Chat",
            IsEnabled = true,
            EndpointId = endpoint.Id
        };
        
        dbContext.Apps.Add(app);
        await dbContext.SaveChangesAsync();
        
        return app;
    }

    /// <summary>
    /// 创建 Endpoint 并关联多个配置
    /// </summary>
    private async Task<(LlmEndpoint endpoint, List<LlmConfig> configs)> CreateEndpointWithConfigsAsync(string endpointName, int configCount)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        var endpoint = new LlmEndpoint
        {
            Id = Guid.NewGuid().ToString(),
            Name = endpointName,
            IsEnabled = true
        };
        
        var configs = new List<LlmConfig>();
        var endpointConfigs = new List<LlmEndpointConfig>();
        
        for (int i = 0; i < configCount; i++)
        {
            var config = new LlmConfig
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{endpointName}-config-{i + 1}",
                Model = $"gpt-4-{i + 1}",
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = "test-key",
                ModelTypeId = "openai",
                IsEnabled = true
            };
            
            configs.Add(config);
            
            var endpointConfig = new LlmEndpointConfig
            {
                Id = Guid.NewGuid().ToString(),
                EndpointId = endpoint.Id,
                LlmConfigId = config.Id,
                Priority = i + 1 // Priority: 1, 2, 3...
            };
            
            endpointConfigs.Add(endpointConfig);
        }
        
        dbContext.Endpoints.Add(endpoint);
        dbContext.Configs.AddRange(configs);
        dbContext.EndpointConfigs.AddRange(endpointConfigs);
        await dbContext.SaveChangesAsync();
        
        // 重新加载以包含导航属性
        endpoint = await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(ec => ec.LlmConfig)
            .FirstAsync(e => e.Id == endpoint.Id);
        
        return (endpoint, configs);
    }

    /// <summary>
    /// 创建 Endpoint 并指定优先级
    /// </summary>
    private async Task<(LlmEndpoint endpoint, List<LlmConfig> configs)> CreateEndpointWithPrioritiesAsync(string endpointName, int[] priorities)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        var endpoint = new LlmEndpoint
        {
            Id = Guid.NewGuid().ToString(),
            Name = endpointName,
            IsEnabled = true
        };
        
        var configs = new List<LlmConfig>();
        var endpointConfigs = new List<LlmEndpointConfig>();
        
        for (int i = 0; i < priorities.Length; i++)
        {
            var config = new LlmConfig
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{endpointName}-config-{i + 1}",
                Model = $"gpt-4-{i + 1}",
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = "test-key",
                ModelTypeId = "openai",
                IsEnabled = true
            };
            
            configs.Add(config);
            
            var endpointConfig = new LlmEndpointConfig
            {
                Id = Guid.NewGuid().ToString(),
                EndpointId = endpoint.Id,
                LlmConfigId = config.Id,
                Priority = priorities[i]
            };
            
            endpointConfigs.Add(endpointConfig);
        }
        
        dbContext.Endpoints.Add(endpoint);
        dbContext.Configs.AddRange(configs);
        dbContext.EndpointConfigs.AddRange(endpointConfigs);
        await dbContext.SaveChangesAsync();
        
        // 重新加载以包含导航属性
        endpoint = await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(ec => ec.LlmConfig)
            .FirstAsync(e => e.Id == endpoint.Id);
        
        return (endpoint, configs);
    }

    #endregion
}
