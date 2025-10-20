using System;
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
using Xunit.Abstractions;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// 测试 App、Config、Endpoint 名称的全局唯一性检查
/// 确保 modelName 在三种实体中不会产生冲突
/// </summary>
public class ModelNameUniquenessTests
{
    private readonly ITestOutputHelper _output;

    public ModelNameUniquenessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 创建测试用的服务
    /// </summary>
    private async Task<(IDbContextFactory<LlmDbContext>, AppService, ConfigService, EndpointService, LoadBalancerCacheWarmupService)> CreateTestServicesAsync()
    {
        var services = new ServiceCollection();
        
        // 使用内存数据库
        services.AddDbContextFactory<LlmDbContext>(options =>
            options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}"));
        
        // 添加日志
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Warning);
        });
        
        // 添加 HybridCache
        services.AddHybridCache();
        
        // 添加服务
        services.AddSingleton<McpClientsFactory>();
        services.AddSingleton<LoadBalancerService>();
        services.AddSingleton<LoadBalancerCacheWarmupService>();
        services.AddTransient<AppService>();
        services.AddTransient<ConfigService>();
        services.AddTransient<EndpointService>();
        services.AddTransient<PromptParameterService>();
        services.AddTransient<ToolMetadataService>();
        
        var serviceProvider = services.BuildServiceProvider();
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        var appService = serviceProvider.GetRequiredService<AppService>();
        var configService = serviceProvider.GetRequiredService<ConfigService>();
        var endpointService = serviceProvider.GetRequiredService<EndpointService>();
        var cacheWarmup = serviceProvider.GetRequiredService<LoadBalancerCacheWarmupService>();
        
        // 初始化数据库
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
        {
            await dbContext.Database.EnsureCreatedAsync();
            
            // 添加 ModelType
            var modelType = new LlmModelType
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "TestModel",
                Description = "Test Model Type"
            };
            dbContext.ModelTypes.Add(modelType);
            await dbContext.SaveChangesAsync();
        }
        
        return (dbContextFactory, appService, configService, endpointService, cacheWarmup);
    }

    [Fact]
    public async Task CheckModelNameUniqueness_SameName_DifferentEntities_ShouldFail()
    {
        // Arrange
        var (dbContextFactory, appService, configService, endpointService, cacheWarmup) = await CreateTestServicesAsync();
        const string duplicateName = "duplicate-model";

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var modelTypeId = dbContext.ModelTypes.First().Id;

        // 1. 创建一个 Config
        var config = new LlmConfig
        {
            Name = duplicateName,
            Model = "test-model",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ModelTypeId = modelTypeId,
            IsEnabled = true
        };
        await configService.AddConfigAsync(config);

        // Act & Assert - 尝试创建同名的 App
        var app = new LlmApp
        {
            Name = duplicateName,
            AppType = "Prompt",
            LlmConfigId = config.Id,
            IsEnabled = true
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => appService.AddAppAsync(app));
        
        Assert.Contains("已被一个 Config 使用", exception.Message);
        Assert.Contains("全局唯一", exception.Message);
        _output.WriteLine($"✅ App 冲突检测成功: {exception.Message}");

        // Act & Assert - 尝试创建同名的 Endpoint
        var endpoint = new LlmEndpoint
        {
            Name = duplicateName,
            IsEnabled = true
        };

        exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpointService.AddEndpointAsync(endpoint));
        
        Assert.Contains("已被一个 Config 使用", exception.Message);
        Assert.Contains("全局唯一", exception.Message);
        _output.WriteLine($"✅ Endpoint 冲突检测成功: {exception.Message}");
    }

    [Fact]
    public async Task CheckModelNameUniqueness_AppToConfig_ShouldFail()
    {
        // Arrange
        var (dbContextFactory, appService, configService, _, _) = await CreateTestServicesAsync();
        const string duplicateName = "app-config-clash";

        var app = new LlmApp
        {
            Name = duplicateName,
            AppType = "AgentGroup",
            IsEnabled = true
        };
        await appService.AddAppAsync(app);

        // Act & Assert
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var modelTypeId = dbContext.ModelTypes.First().Id;
        
        var config = new LlmConfig
        {
            Name = duplicateName,
            Model = "test-model",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ModelTypeId = modelTypeId,
            IsEnabled = true
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => configService.AddConfigAsync(config));
        
        Assert.Contains("已被一个 App 使用", exception.Message);
        _output.WriteLine($"✅ Config 冲突检测成功: {exception.Message}");
    }

    [Fact]
    public async Task CheckModelNameUniqueness_AppToEndpoint_ShouldFail()
    {
        // Arrange
        var (_, appService, _, endpointService, _) = await CreateTestServicesAsync();
        const string duplicateName = "app-endpoint-clash";

        var app = new LlmApp
        {
            Name = duplicateName,
            AppType = "AgentGroup",
            IsEnabled = true
        };
        await appService.AddAppAsync(app);

        // Act & Assert
        var endpoint = new LlmEndpoint
        {
            Name = duplicateName,
            IsEnabled = true
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpointService.AddEndpointAsync(endpoint));
        
        Assert.Contains("已被一个 App 使用", exception.Message);
        _output.WriteLine($"✅ Endpoint 冲突检测成功: {exception.Message}");
    }

    [Fact]
    public async Task CheckModelNameUniqueness_ConfigToEndpoint_ShouldFail()
    {
        // Arrange
        var (dbContextFactory, _, configService, endpointService, _) = await CreateTestServicesAsync();
        const string duplicateName = "config-endpoint-clash";

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var modelTypeId = dbContext.ModelTypes.First().Id;
        
        var config = new LlmConfig
        {
            Name = duplicateName,
            Model = "test-model",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ModelTypeId = modelTypeId,
            IsEnabled = true
        };
        await configService.AddConfigAsync(config);

        // Act & Assert
        var endpoint = new LlmEndpoint
        {
            Name = duplicateName,
            IsEnabled = true
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpointService.AddEndpointAsync(endpoint));
        
        Assert.Contains("已被一个 Config 使用", exception.Message);
        _output.WriteLine($"✅ Endpoint 冲突检测成功: {exception.Message}");
    }

    [Fact]
    public async Task UpdateEntity_ChangeName_ToExistingName_ShouldFail()
    {
        // Arrange
        var (dbContextFactory, appService, configService, _, _) = await CreateTestServicesAsync();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var modelTypeId = dbContext.ModelTypes.First().Id;
        
        var config = new LlmConfig
        {
            Name = "original-config",
            Model = "test-model",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ModelTypeId = modelTypeId,
            IsEnabled = true
        };
        await configService.AddConfigAsync(config);

        var app = new LlmApp
        {
            Name = "original-app",
            AppType = "AgentGroup",
            IsEnabled = true
        };
        await appService.AddAppAsync(app);

        // Act & Assert - 尝试将 App 名称改为 Config 的名称
        app.Name = "original-config";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => appService.UpdateAppAsync(app));
        
        Assert.Contains("已被一个 Config 使用", exception.Message);
        _output.WriteLine($"✅ 更新冲突检测成功: {exception.Message}");
    }

    [Fact]
    public async Task UpdateEntity_KeepSameName_ShouldSucceed()
    {
        // Arrange
        var (dbContextFactory, _, configService, _, _) = await CreateTestServicesAsync();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var modelTypeId = dbContext.ModelTypes.First().Id;
        
        var config = new LlmConfig
        {
            Name = "keep-same-name",
            Model = "test-model",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ModelTypeId = modelTypeId,
            IsEnabled = true
        };
        await configService.AddConfigAsync(config);

        // Act - 更新其他字段但保持名称不变
        config.Description = "Updated description";
        var updated = await configService.UpdateConfigAsync(config);

        // Assert
        Assert.Equal("keep-same-name", updated.Name);
        Assert.Equal("Updated description", updated.Description);
        _output.WriteLine("✅ 保持相同名称的更新成功");
    }

    [Fact]
    public async Task CreateEntities_DifferentNames_ShouldSucceed()
    {
        // Arrange & Act
        var (dbContextFactory, appService, configService, endpointService, _) = await CreateTestServicesAsync();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var modelTypeId = dbContext.ModelTypes.First().Id;
        
        var config = new LlmConfig
        {
            Name = "unique-config",
            Model = "test-model",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ModelTypeId = modelTypeId,
            IsEnabled = true
        };
        await configService.AddConfigAsync(config);

        var app = new LlmApp
        {
            Name = "unique-app",
            AppType = "AgentGroup",
            IsEnabled = true
        };
        await appService.AddAppAsync(app);

        var endpoint = new LlmEndpoint
        {
            Name = "unique-endpoint",
            IsEnabled = true
        };
        await endpointService.AddEndpointAsync(endpoint);

        // Assert
        Assert.NotNull(config.Id);
        Assert.NotNull(app.Id);
        Assert.NotNull(endpoint.Id);
        _output.WriteLine("✅ 创建不同名称的实体成功");
    }

    [Fact]
    public async Task CheckModelNameUniqueness_DirectMethod_ShouldDetectConflicts()
    {
        // Arrange
        var (dbContextFactory, _, configService, _, cacheWarmup) = await CreateTestServicesAsync();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var modelTypeId = dbContext.ModelTypes.First().Id;
        
        var config = new LlmConfig
        {
            Name = "direct-test",
            Model = "test-model",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ModelTypeId = modelTypeId,
            IsEnabled = true
        };
        await configService.AddConfigAsync(config);

        // Act & Assert
        var error = await cacheWarmup.CheckModelNameUniquenessAsync("direct-test", "App");
        Assert.NotNull(error);
        Assert.Contains("Config", error);
        _output.WriteLine($"✅ 直接方法检测冲突成功: {error}");

        error = await cacheWarmup.CheckModelNameUniquenessAsync("non-existent", "App");
        Assert.Null(error);
        _output.WriteLine("✅ 直接方法检测唯一名称成功");
    }
}
