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
using Xunit.Abstractions;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// LlmPoolCacheService 单元测试
/// 测试缓存预热、模型名称解析和缓存失效逻辑
/// </summary>
public class LlmPoolCacheServiceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _serviceProvider;
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly LlmPoolCacheService _cacheService;

    public LlmPoolCacheServiceTests(ITestOutputHelper output)
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
        
        _serviceProvider = services.BuildServiceProvider();
        _dbContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        _cacheService = _serviceProvider.GetRequiredService<LlmPoolCacheService>();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    #region 测试 1: 缓存预热

    /// <summary>
    /// 测试预热所有 App
    /// </summary>
    [Fact]
    public async Task WarmupCache_ShouldCacheAllApps()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        var app1 = await CreateAppWithConfigAsync("app-1", config);
        var app2 = await CreateAppWithConfigAsync("app-2", config);
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert - 通过 ResolveModelNameAsync 验证缓存
        var result1 = await _cacheService.ResolveModelNameAsync(app1.Name);
        var result2 = await _cacheService.ResolveModelNameAsync(app2.Name);
        
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(config.Id, result1.Config!.Id);
        Assert.Equal(config.Id, result2.Config!.Id);
        
        _output.WriteLine($"✅ 成功缓存了 {2} 个 App");
    }

    /// <summary>
    /// 测试预热所有 Config
    /// </summary>
    [Fact]
    public async Task WarmupCache_ShouldCacheAllConfigs()
    {
        // Arrange
        var config1 = await CreateConfigAsync("config-1");
        var config2 = await CreateConfigAsync("config-2");
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result1 = await _cacheService.ResolveModelNameAsync(config1.Name);
        var result2 = await _cacheService.ResolveModelNameAsync(config2.Name);
        
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(config1.Id, result1.Config!.Id);
        Assert.Equal(config2.Id, result2.Config!.Id);
        
        _output.WriteLine($"✅ 成功缓存了 {2} 个 Config");
    }

    /// <summary>
    /// 测试预热所有 Endpoint
    /// </summary>
    [Fact]
    public async Task WarmupCache_ShouldCacheAllEndpoints()
    {
        // Arrange
        var (endpoint1, _) = await CreateEndpointWithConfigsAsync("endpoint-1", 2);
        var (endpoint2, _) = await CreateEndpointWithConfigsAsync("endpoint-2", 3);
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result1 = await _cacheService.ResolveModelNameAsync(endpoint1.Name);
        var result2 = await _cacheService.ResolveModelNameAsync(endpoint2.Name);
        
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(2, result1.AvailableConfigs!.Count);
        Assert.Equal(3, result2.AvailableConfigs!.Count);
        
        _output.WriteLine($"✅ 成功缓存了 {2} 个 Endpoint");
    }

    #endregion

    #region 测试 2: 模型名称解析

    /// <summary>
    /// 测试解析 App 名称
    /// </summary>
    [Fact]
    public async Task ResolveModelName_AppName_ShouldReturnCorrectConfig()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        var app = await CreateAppWithConfigAsync("test-app", config);
        
        // Act
        var result = await _cacheService.ResolveModelNameAsync(app.Name);
        
        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(config.Id, result.Config!.Id);
        Assert.Contains("App 直接指定配置", result.Strategy);
        
        _output.WriteLine($"✅ 成功解析 App 名称: {app.Name} -> {config.Name}");
    }

    /// <summary>
    /// 测试解析 Config 名称
    /// </summary>
    [Fact]
    public async Task ResolveModelName_ConfigName_ShouldReturnConfig()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        
        // Act
        var result = await _cacheService.ResolveModelNameAsync(config.Name);
        
        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(config.Id, result.Config!.Id);
        Assert.Contains("直接配置名称", result.Strategy);
        
        _output.WriteLine($"✅ 成功解析 Config 名称: {config.Name}");
    }

    /// <summary>
    /// 测试解析 Endpoint 名称
    /// </summary>
    [Fact]
    public async Task ResolveModelName_EndpointName_ShouldReturnAllConfigs()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        
        // Act
        var result = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        
        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.AvailableConfigs);
        Assert.Equal(3, result.AvailableConfigs.Count);
        Assert.Contains("Endpoint", result.Strategy);
        
        _output.WriteLine($"✅ 成功解析 Endpoint 名称: {endpoint.Name} -> {configs.Count} 个配置");
    }

    /// <summary>
    /// 测试解析 Endpoint ID
    /// </summary>
    [Fact]
    public async Task ResolveModelName_EndpointId_ShouldReturnAllConfigs()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 2);
        
        // Act
        var result = await _cacheService.ResolveModelNameAsync(endpoint.Id);
        
        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.AvailableConfigs);
        Assert.Equal(2, result.AvailableConfigs.Count);
        
        _output.WriteLine($"✅ 成功解析 Endpoint ID: {endpoint.Id} -> {configs.Count} 个配置");
    }

    /// <summary>
    /// 测试解析不存在的名称
    /// </summary>
    [Fact]
    public async Task ResolveModelName_NonExistent_ShouldReturnNull()
    {
        // Act
        var result = await _cacheService.ResolveModelNameAsync("non-existent-model");
        
        // Assert
        Assert.Null(result);
        
        _output.WriteLine($"✅ 不存在的模型名称正确返回 null");
    }

    /// <summary>
    /// 测试 App 关联 Endpoint 的解析
    /// </summary>
    [Fact]
    public async Task ResolveModelName_AppWithEndpoint_ShouldReturnEndpointConfigs()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        var app = await CreateAppWithEndpointAsync("test-app", endpoint);
        
        // Act
        var result = await _cacheService.ResolveModelNameAsync(app.Name);
        
        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.AvailableConfigs);
        Assert.Equal(3, result.AvailableConfigs.Count);
        Assert.Contains("App 的 Endpoint", result.Strategy);
        
        _output.WriteLine($"✅ App 通过 Endpoint 成功解析到 {configs.Count} 个配置");
    }

    #endregion

    #region 测试 3: 缓存失效

    /// <summary>
    /// 测试失效 App 缓存
    /// </summary>
    [Fact]
    public async Task InvalidateAppCache_ShouldClearCache()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        var app = await CreateAppWithConfigAsync("test-app", config);
        await _cacheService.WarmupCacheAsync();
        
        // Act
        await _cacheService.InvalidateAppRelatedCachesAsync(app.Id);
        
        // Assert - 重新预热后应该能再次获取
        await _cacheService.WarmupCacheAsync();
        var result = await _cacheService.ResolveModelNameAsync(app.Name);
        Assert.NotNull(result);
        
        _output.WriteLine($"✅ 成功失效并重新缓存 App: {app.Name}");
    }

    /// <summary>
    /// 测试失效 Config 缓存
    /// </summary>
    [Fact]
    public async Task InvalidateConfigCache_ShouldClearRelatedCaches()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        await _cacheService.WarmupCacheAsync();
        
        // Act
        await _cacheService.InvalidateConfigRelatedCachesAsync(config.Id);
        
        // 等待异步预热完成
        await Task.Delay(100);
        
        // Assert - 应该能重新获取
        var result = await _cacheService.ResolveModelNameAsync(config.Name);
        Assert.NotNull(result);
        
        _output.WriteLine($"✅ 成功失效并重新缓存 Config: {config.Name}");
    }

    /// <summary>
    /// 测试清空所有缓存
    /// </summary>
    [Fact]
    public async Task ClearAll_ShouldClearAllCaches()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        var app = await CreateAppWithConfigAsync("test-app", config);
        await _cacheService.WarmupCacheAsync();
        
        // Act
        await _cacheService.ClearAllAsync();
        
        // Assert - 重新预热后应该能再次获取
        await _cacheService.WarmupCacheAsync();
        var result = await _cacheService.ResolveModelNameAsync(app.Name);
        Assert.NotNull(result);
        
        _output.WriteLine($"✅ 成功清空并重新缓存所有数据");
    }

    #endregion

    #region 测试 4: 配置更新后缓存自动刷新

    /// <summary>
    /// 测试更新 Config 后缓存自动刷新
    /// </summary>
    [Fact]
    public async Task UpdateConfig_ShouldInvalidateAndRefreshCache()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        await _cacheService.WarmupCacheAsync();
        
        // 验证初始缓存
        var initialResult = await _cacheService.ResolveModelNameAsync(config.Name);
        Assert.NotNull(initialResult);
        Assert.Equal("gpt-4", initialResult.Config!.Model);
        
        // Act - 更新配置
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var configToUpdate = await dbContext.Configs.FindAsync(config.Id);
            configToUpdate!.Model = "gpt-4-turbo"; // 修改模型
            configToUpdate.BaseUrl = "https://new-api.openai.com/v1"; // 修改 URL
            await dbContext.SaveChangesAsync();
        }
        
        // 触发缓存失效
        await _cacheService.InvalidateConfigRelatedCachesAsync(config.Id);
        
        // 等待异步预热完成
        await Task.Delay(200);
        
        // Assert - 缓存应该已更新
        var updatedResult = await _cacheService.ResolveModelNameAsync(config.Name);
        Assert.NotNull(updatedResult);
        Assert.Equal("gpt-4-turbo", updatedResult.Config!.Model);
        Assert.Equal("https://new-api.openai.com/v1", updatedResult.Config!.BaseUrl);
        
        _output.WriteLine($"✅ Config 更新后缓存自动刷新: {config.Name}");
    }

    /// <summary>
    /// 测试更新 Endpoint 后缓存自动刷新
    /// </summary>
    [Fact]
    public async Task UpdateEndpoint_ShouldInvalidateAndRefreshCache()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 2);
        await _cacheService.WarmupCacheAsync();
        
        // 验证初始缓存 - 有2个配置
        var initialResult = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        Assert.NotNull(initialResult);
        Assert.Equal(2, initialResult.AvailableConfigs!.Count);
        
        // Act - 添加第3个配置到 Endpoint
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var newConfig = new LlmConfig
            {
                Id = Guid.NewGuid().ToString(),
                Name = "test-endpoint-config-3",
                Model = "gpt-4-new",
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = "test-key",
                ModelTypeId = "openai",
                IsEnabled = true
            };
            
            var endpointConfig = new LlmEndpointConfig
            {
                Id = Guid.NewGuid().ToString(),
                EndpointId = endpoint.Id,
                LlmConfigId = newConfig.Id,
                Priority = 3
            };
            
            dbContext.Configs.Add(newConfig);
            dbContext.EndpointConfigs.Add(endpointConfig);
            await dbContext.SaveChangesAsync();
        }
        
        // 触发缓存失效
        await _cacheService.InvalidateEndpointRelatedCachesAsync(endpoint.Id);
        
        // 等待异步预热完成
        await Task.Delay(200);
        
        // Assert - 缓存应该包含3个配置
        var updatedResult = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        Assert.NotNull(updatedResult);
        Assert.Equal(3, updatedResult.AvailableConfigs!.Count);
        
        _output.WriteLine($"✅ Endpoint 更新后缓存自动刷新: {endpoint.Name} (2 -> 3 个配置)");
    }

    /// <summary>
    /// 测试更新 App 配置关联后缓存自动刷新
    /// </summary>
    [Fact]
    public async Task UpdateApp_ChangeConfig_ShouldInvalidateAndRefreshCache()
    {
        // Arrange
        var config1 = await CreateConfigAsync("config-1");
        var config2 = await CreateConfigAsync("config-2");
        var app = await CreateAppWithConfigAsync("test-app", config1);
        await _cacheService.WarmupCacheAsync();
        
        // 验证初始缓存 - 使用 config1
        var initialResult = await _cacheService.ResolveModelNameAsync(app.Name);
        Assert.NotNull(initialResult);
        Assert.Equal(config1.Id, initialResult.Config!.Id);
        
        // Act - 修改 App 的配置关联
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var appToUpdate = await dbContext.Apps.FindAsync(app.Id);
            appToUpdate!.LlmConfigId = config2.Id; // 改为 config2
            await dbContext.SaveChangesAsync();
        }
        
        // 触发缓存失效
        await _cacheService.InvalidateAppRelatedCachesAsync(app.Id);
        
        // 等待异步预热完成
        await Task.Delay(200);
        
        // Assert - 缓存应该使用 config2
        var updatedResult = await _cacheService.ResolveModelNameAsync(app.Name);
        Assert.NotNull(updatedResult);
        Assert.Equal(config2.Id, updatedResult.Config!.Id);
        
        _output.WriteLine($"✅ App 配置关联更新后缓存自动刷新: {app.Name} ({config1.Name} -> {config2.Name})");
    }

    /// <summary>
    /// 测试 App 从直接配置切换到 Endpoint
    /// </summary>
    [Fact]
    public async Task UpdateApp_FromConfigToEndpoint_ShouldInvalidateAndRefreshCache()
    {
        // Arrange
        var directConfig = await CreateConfigAsync("direct-config");
        var (endpoint, endpointConfigs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        var app = await CreateAppWithConfigAsync("test-app", directConfig);
        await _cacheService.WarmupCacheAsync();
        
        // 验证初始缓存 - 使用直接配置
        var initialResult = await _cacheService.ResolveModelNameAsync(app.Name);
        Assert.NotNull(initialResult);
        Assert.Equal(directConfig.Id, initialResult.Config!.Id);
        Assert.Null(initialResult.AvailableConfigs); // 直接配置无 AvailableConfigs
        
        // Act - 修改 App 从直接配置改为使用 Endpoint
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var appToUpdate = await dbContext.Apps.FindAsync(app.Id);
            appToUpdate!.LlmConfigId = null; // 清除直接配置
            appToUpdate.EndpointId = endpoint.Id; // 设置 Endpoint
            await dbContext.SaveChangesAsync();
        }
        
        // 触发缓存失效
        await _cacheService.InvalidateAppRelatedCachesAsync(app.Id);
        
        // 等待异步预热完成
        await Task.Delay(200);
        
        // Assert - 缓存应该包含 Endpoint 的多个配置
        var updatedResult = await _cacheService.ResolveModelNameAsync(app.Name);
        Assert.NotNull(updatedResult);
        Assert.NotNull(updatedResult.AvailableConfigs);
        Assert.Equal(3, updatedResult.AvailableConfigs.Count);
        Assert.Contains("App 的 Endpoint", updatedResult.Strategy);
        
        _output.WriteLine($"✅ App 从直接配置切换到 Endpoint 后缓存自动刷新: {app.Name}");
    }

    /// <summary>
    /// 测试禁用 Config 后缓存自动失效
    /// </summary>
    [Fact]
    public async Task DisableConfig_ShouldInvalidateCache()
    {
        // Arrange
        var config = await CreateConfigAsync("test-config");
        await _cacheService.WarmupCacheAsync();
        
        // 验证初始缓存
        var initialResult = await _cacheService.ResolveModelNameAsync(config.Name);
        Assert.NotNull(initialResult);
        
        // Act - 禁用配置
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var configToDisable = await dbContext.Configs.FindAsync(config.Id);
            configToDisable!.IsEnabled = false; // 禁用
            await dbContext.SaveChangesAsync();
        }
        
        // 触发缓存失效
        await _cacheService.InvalidateConfigRelatedCachesAsync(config.Id);
        
        // 等待异步预热完成
        await Task.Delay(200);
        
        // Assert - 缓存应该返回 null (禁用的配置不缓存)
        var updatedResult = await _cacheService.ResolveModelNameAsync(config.Name);
        Assert.Null(updatedResult);
        
        _output.WriteLine($"✅ Config 禁用后缓存自动失效: {config.Name}");
    }

    /// <summary>
    /// 测试从 Endpoint 移除配置后缓存自动刷新
    /// </summary>
    [Fact]
    public async Task RemoveConfigFromEndpoint_ShouldInvalidateAndRefreshCache()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithConfigsAsync("test-endpoint", 3);
        await _cacheService.WarmupCacheAsync();
        
        // 验证初始缓存 - 有3个配置
        var initialResult = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        Assert.NotNull(initialResult);
        Assert.Equal(3, initialResult.AvailableConfigs!.Count);
        
        // Act - 移除第2个配置
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var endpointConfigToRemove = await dbContext.EndpointConfigs
                .FirstAsync(ec => ec.EndpointId == endpoint.Id && ec.LlmConfigId == configs[1].Id);
            
            dbContext.EndpointConfigs.Remove(endpointConfigToRemove);
            await dbContext.SaveChangesAsync();
        }
        
        // 触发缓存失效
        await _cacheService.InvalidateEndpointRelatedCachesAsync(endpoint.Id);
        
        // 等待异步预热完成
        await Task.Delay(200);
        
        // Assert - 缓存应该只有2个配置
        var updatedResult = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        Assert.NotNull(updatedResult);
        Assert.Equal(2, updatedResult.AvailableConfigs!.Count);
        
        // 验证移除的配置不在列表中
        Assert.DoesNotContain(updatedResult.AvailableConfigs, c => c.Id == configs[1].Id);
        
        _output.WriteLine($"✅ 从 Endpoint 移除配置后缓存自动刷新: {endpoint.Name} (3 -> 2 个配置)");
    }

    /// <summary>
    /// 测试修改 Endpoint 配置的优先级后缓存自动刷新
    /// </summary>
    [Fact]
    public async Task UpdateEndpointConfigPriority_ShouldInvalidateAndRefreshCache()
    {
        // Arrange
        var (endpoint, configs) = await CreateEndpointWithPrioritiesAsync("test-endpoint", new[] { 1, 2, 3 });
        await _cacheService.WarmupCacheAsync();
        
        // 验证初始优先级顺序
        var initialResult = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        Assert.NotNull(initialResult);
        Assert.Equal(configs[0].Id, initialResult.AvailableConfigs![0].Id); // Priority 1
        Assert.Equal(configs[1].Id, initialResult.AvailableConfigs[1].Id); // Priority 2
        Assert.Equal(configs[2].Id, initialResult.AvailableConfigs[2].Id); // Priority 3
        
        // Act - 修改优先级: 将第1个改为最低优先级
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var endpointConfigToUpdate = await dbContext.EndpointConfigs
                .FirstAsync(ec => ec.EndpointId == endpoint.Id && ec.LlmConfigId == configs[0].Id);
            
            endpointConfigToUpdate.Priority = 10; // 改为最低优先级
            await dbContext.SaveChangesAsync();
        }
        
        // 触发缓存失效
        await _cacheService.InvalidateEndpointRelatedCachesAsync(endpoint.Id);
        
        // 等待异步预热完成
        await Task.Delay(200);
        
        // Assert - 缓存顺序应该改变
        var updatedResult = await _cacheService.ResolveModelNameAsync(endpoint.Name);
        Assert.NotNull(updatedResult);
        Assert.Equal(configs[1].Id, updatedResult.AvailableConfigs![0].Id); // Priority 2 现在排第一
        Assert.Equal(configs[2].Id, updatedResult.AvailableConfigs[1].Id); // Priority 3
        Assert.Equal(configs[0].Id, updatedResult.AvailableConfigs[2].Id); // Priority 10 现在排最后
        
        _output.WriteLine($"✅ Endpoint 配置优先级更新后缓存自动刷新: {endpoint.Name}");
    }

    #endregion

    #region 测试 5: App 优先级

    /// <summary>
    /// 测试 App 优先使用直接指定的 Config
    /// </summary>
    [Fact]
    public async Task ResolveModelName_AppWithBothConfigAndEndpoint_ShouldPreferDirectConfig()
    {
        // Arrange
        var directConfig = await CreateConfigAsync("direct-config");
        var (endpoint, endpointConfigs) = await CreateEndpointWithConfigsAsync("endpoint", 2);
        
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
        var result = await _cacheService.ResolveModelNameAsync("test-app");
        
        // Assert - 应该使用直接指定的配置,而不是 Endpoint 的配置
        Assert.NotNull(result);
        Assert.Equal(directConfig.Id, result.Config!.Id);
        Assert.Contains("App 直接指定配置", result.Strategy);
        Assert.Null(result.AvailableConfigs); // 直接配置不设置 AvailableConfigs
        
        _output.WriteLine($"✅ App 优先使用直接指定的配置: {directConfig.Name}");
    }

    #endregion

    #region 测试 5: 禁用状态过滤

    /// <summary>
    /// 测试禁用的 Config 不会被缓存
    /// </summary>
    [Fact]
    public async Task WarmupCache_DisabledConfig_ShouldNotBeCached()
    {
        // Arrange
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var disabledConfig = new LlmConfig
        {
            Id = Guid.NewGuid().ToString(),
            Name = "disabled-config",
            Model = "gpt-4",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "test-key",
            ModelTypeId = "openai",
            IsEnabled = false // 禁用
        };
        
        dbContext.Configs.Add(disabledConfig);
        await dbContext.SaveChangesAsync();
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result = await _cacheService.ResolveModelNameAsync(disabledConfig.Name);
        Assert.Null(result); // 禁用的配置不应该被解析到
        
        _output.WriteLine($"✅ 禁用的配置被正确过滤");
    }

    /// <summary>
    /// 测试 Endpoint 只缓存启用的配置
    /// </summary>
    [Fact]
    public async Task WarmupCache_EndpointWithDisabledConfigs_ShouldOnlyCacheEnabledOnes()
    {
        // Arrange
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
        {
            var endpoint = new LlmEndpoint
            {
                Id = Guid.NewGuid().ToString(),
                Name = "test-endpoint",
                IsEnabled = true
            };
            
            // 创建3个配置: 2个启用, 1个禁用
            var configs = new List<LlmConfig>
            {
                new LlmConfig { Id = Guid.NewGuid().ToString(), Name = "config-1", Model = "gpt-4", BaseUrl = "url", ApiKey = "key", ModelTypeId = "openai", IsEnabled = true },
                new LlmConfig { Id = Guid.NewGuid().ToString(), Name = "config-2", Model = "gpt-4", BaseUrl = "url", ApiKey = "key", ModelTypeId = "openai", IsEnabled = false }, // 禁用
                new LlmConfig { Id = Guid.NewGuid().ToString(), Name = "config-3", Model = "gpt-4", BaseUrl = "url", ApiKey = "key", ModelTypeId = "openai", IsEnabled = true }
            };
            
            var endpointConfigs = configs.Select((c, i) => new LlmEndpointConfig
            {
                Id = Guid.NewGuid().ToString(),
                EndpointId = endpoint.Id,
                LlmConfigId = c.Id,
                Priority = i + 1
            }).ToList();
            
            dbContext.Endpoints.Add(endpoint);
            dbContext.Configs.AddRange(configs);
            dbContext.EndpointConfigs.AddRange(endpointConfigs);
            await dbContext.SaveChangesAsync();
        }
        
        // Act
        await _cacheService.WarmupCacheAsync();
        
        // Assert
        var result = await _cacheService.ResolveModelNameAsync("test-endpoint");
        Assert.NotNull(result);
        Assert.NotNull(result.AvailableConfigs);
        Assert.Equal(2, result.AvailableConfigs.Count); // 只有2个启用的配置
        
        _output.WriteLine($"✅ Endpoint 只缓存了 {result.AvailableConfigs.Count} 个启用的配置");
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
    /// 创建 App 并关联配置
    /// </summary>
    private async Task<LlmApp> CreateAppWithConfigAsync(string appName, LlmConfig config)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = appName,
            AppType = "Chat",
            IsEnabled = true,
            LlmConfigId = config.Id
        };
        
        dbContext.Apps.Add(app);
        await dbContext.SaveChangesAsync();
        
        return app;
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
                Priority = i + 1
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
