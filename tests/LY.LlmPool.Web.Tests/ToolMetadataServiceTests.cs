using System;
using System.Linq;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models.Tools;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Aggregation;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// ToolMetadataService 集成测试
/// 使用真实的 HybridCache 和内存数据库
/// </summary>
public class ToolMetadataServiceTests : IAsyncLifetime
{
    private ServiceProvider _serviceProvider = null!;
    private LlmDbContext _dbContext = null!;
    private ToolMetadataService _service = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        
        // 配置内存数据库
        services.AddDbContextFactory<LlmDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));
        
        // 配置 HybridCache
        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 1024 * 1024; // 1MB
            options.MaximumKeyLength = 1024;
        });
        
        // 注册 McpClientsFactory (真实实例,因为它是 sealed 类)
        services.AddSingleton<McpClientsFactory>();
        
        // 注册服务
        // PromptParameterExtractor has been merged into PromptParameterService
        services.AddTransient<PromptParameterService>();
        services.AddTransient<ToolMetadataService>();
        services.AddLogging();
        
        _serviceProvider = services.BuildServiceProvider();
        
        // 创建 DbContext 并初始化测试数据
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        _dbContext = await factory.CreateDbContextAsync();
        
        _service = _serviceProvider.GetRequiredService<ToolMetadataService>();
    }

    public Task DisposeAsync()
    {
        _dbContext?.Dispose();
        _serviceProvider?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ScanAppToolsAsync_ShouldReturnEmptyList_WhenNoToolAppsExist()
    {
        // Act
        var result = await _service.ScanAppToolsAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAppToolsAsync_ShouldReturnToolMetadata_WhenToolAppExists()
    {
        // Arrange
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "WeatherPrompt",
            Content = "Get weather for city {{city}} on date {{date}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "WeatherTool",
            Description = "Get weather information",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id,
            ConfigJson = "{\"api_key\":\"test123\"}"
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.ScanAppToolsAsync();

        // Assert
        Assert.Single(result);
        var tool = result.First();
        Assert.Equal("WeatherTool", tool.Name);
        Assert.Equal("Get weather information", tool.Description);
        Assert.Equal(ToolSource.App, tool.Source);
        Assert.Equal(app.Id, tool.SourceId);
        Assert.Contains("city", tool.ParametersSchema);
        Assert.Contains("date", tool.ParametersSchema);
        Assert.Equal("{\"api_key\":\"test123\"}", tool.ConfigJson);
    }

    [Fact]
    public async Task ScanAppToolsAsync_ShouldFilterDisabledApps()
    {
        // Arrange
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "DisabledPrompt",
            Content = "Test {{param}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "DisabledTool",
            AppType = "Tool",
            IsEnabled = false,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.ScanAppToolsAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAppToolsAsync_ShouldFilterNonToolApps()
    {
        // Arrange
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AgentPrompt",
            Content = "Test"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AgentApp",
            AppType = "Agent",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.ScanAppToolsAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAppToolsAsync_ShouldHandleAppWithoutPrompt()
    {
        // Arrange
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "ToolWithoutPrompt",
            Description = "No parameters",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = null
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.ScanAppToolsAsync();

        // Assert
        Assert.Single(result);
        var tool = result.First();
        Assert.Equal("ToolWithoutPrompt", tool.Name);
        // Empty parameters should still have valid JSON Schema structure
        Assert.Contains("\"type\":\"object\"", tool.ParametersSchema);
    }

    [Fact]
    public async Task InitializeAsync_AndGetAllToolsAsync_ShouldCacheResults()
    {
        // Arrange - 添加测试数据
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "InitPrompt",
            Content = "Process {{input}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "InitTool",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        // Act - 初始化缓存
        await _service.InitializeAsync();

        // 获取缓存的工具列表
        var result = await _service.GetAllToolsAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("InitTool", result.First().Name);
        
        // 验证是从缓存读取(再次调用应该返回相同引用)
        var result2 = await _service.GetAllToolsAsync();
        Assert.Equal(result.Count, result2.Count);
    }

    [Fact]
    public async Task GetToolByNameAsync_ShouldReturnMatchingTool()
    {
        // Arrange
        var prompt1 = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Prompt1",
            Content = "{{x}}"
        };
        var prompt2 = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Prompt2",
            Content = "{{y}}"
        };
        _dbContext.Prompts.AddRange(prompt1, prompt2);

        var app1 = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Tool1",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt1.Id
        };
        var app2 = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Tool2",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt2.Id
        };
        _dbContext.Apps.AddRange(app1, app2);
        await _dbContext.SaveChangesAsync();

        await _service.InitializeAsync();

        // Act
        var result = await _service.GetToolByNameAsync("Tool2");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Tool2", result.Name);
        Assert.Equal(ToolSource.App, result.Source);
    }

    [Fact]
    public async Task GetToolByNameAsync_ShouldReturnNull_WhenToolNotFound()
    {
        // Arrange
        await _service.InitializeAsync();

        // Act
        var result = await _service.GetToolByNameAsync("NonExistentTool");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshAppToolAsync_ShouldUpdateCache_WhenAppUpdated()
    {
        // Arrange - 创建初始工具
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "OriginalPrompt",
            Content = "Process {{input}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "UpdatedTool",
            Description = "Original Description",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        await _service.InitializeAsync();

        // 验证初始状态
        var originalTool = await _service.GetToolByNameAsync("UpdatedTool");
        Assert.NotNull(originalTool);
        Assert.Equal("Original Description", originalTool.Description);
        Assert.Contains("input", originalTool.ParametersSchema);

        // Act - 更新 App
        app.Description = "Updated Description";
        var newPrompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "UpdatedPrompt",
            Content = "New process with {{param1}} and {{param2}}"
        };
        _dbContext.Prompts.Add(newPrompt);
        app.LlmPromptId = newPrompt.Id;
        await _dbContext.SaveChangesAsync();

        // 刷新缓存
        await _service.RefreshAppToolAsync(app.Id!);

        // Assert - 验证缓存已更新
        var updatedTool = await _service.GetToolByNameAsync("UpdatedTool");
        Assert.NotNull(updatedTool);
        Assert.Equal("Updated Description", updatedTool.Description);
        Assert.Contains("param1", updatedTool.ParametersSchema);
        Assert.Contains("param2", updatedTool.ParametersSchema);
        Assert.DoesNotContain("input", updatedTool.ParametersSchema);
    }

    [Fact]
    public async Task RefreshAppToolAsync_ShouldRemoveFromCache_WhenAppDisabled()
    {
        // Arrange
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "DisablePrompt",
            Content = "{{test}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "DisableTool",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        await _service.InitializeAsync();

        // 验证工具存在
        var tools = await _service.GetAllToolsAsync();
        Assert.Contains(tools, t => t.Name == "DisableTool");

        // Act - 禁用 App
        app.IsEnabled = false;
        await _dbContext.SaveChangesAsync();
        await _service.RefreshAppToolAsync(app.Id!);

        // Assert - 验证已从缓存移除
        var updatedTools = await _service.GetAllToolsAsync();
        Assert.DoesNotContain(updatedTools, t => t.Name == "DisableTool");
    }

    [Fact]
    public async Task RefreshAppToolAsync_ShouldRemoveFromCache_WhenAppDeleted()
    {
        // Arrange
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "DeletePrompt",
            Content = "{{test}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "DeleteTool",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        await _service.InitializeAsync();

        // 验证工具存在
        var initialTool = await _service.GetToolByNameAsync("DeleteTool");
        Assert.NotNull(initialTool);

        // Act - 删除 App
        var appId = app.Id!;
        _dbContext.Apps.Remove(app);
        await _dbContext.SaveChangesAsync();
        await _service.RefreshAppToolAsync(appId);

        // Assert - 验证已从缓存移除
        var tools = await _service.GetAllToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name == "DeleteTool");
    }

    [Fact]
    public async Task RefreshAppToolAsync_ShouldRemoveFromCache_WhenAppTypeChangedFromTool()
    {
        // Arrange
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "TypeChangePrompt",
            Content = "{{test}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "TypeChangeTool",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        await _service.InitializeAsync();

        // 验证工具存在
        var initialTools = await _service.GetAllToolsAsync();
        Assert.Contains(initialTools, t => t.Name == "TypeChangeTool");

        // Act - 改变 AppType 为 Agent
        app.AppType = "Agent";
        await _dbContext.SaveChangesAsync();
        await _service.RefreshAppToolAsync(app.Id!);

        // Assert - 验证已从缓存移除
        var updatedTools = await _service.GetAllToolsAsync();
        Assert.DoesNotContain(updatedTools, t => t.Name == "TypeChangeTool");
    }
}
