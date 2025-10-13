using System;
using System.Linq;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models.Tools;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// AppService 和 ToolMetadataService 集成测试
/// 验证 App CRUD 操作自动刷新工具缓存
/// </summary>
public class AppToolIntegrationTests : IAsyncLifetime
{
    private ServiceProvider _serviceProvider = null!;
    private LlmDbContext _dbContext = null!;
    private AppService _appService = null!;
    private ToolMetadataService _toolMetadataService = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        
        // 配置内存数据库
        services.AddDbContextFactory<LlmDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));
        
        // 配置 HybridCache
        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 1024 * 1024;
            options.MaximumKeyLength = 1024;
        });
        
        // 注册服务
        services.AddTransient<PromptParameterExtractor>();
        services.AddTransient<ToolMetadataService>();
        services.AddTransient<AppService>();
        services.AddLogging();
        
        _serviceProvider = services.BuildServiceProvider();
        
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        _dbContext = await factory.CreateDbContextAsync();
        
        _appService = _serviceProvider.GetRequiredService<AppService>();
        _toolMetadataService = _serviceProvider.GetRequiredService<ToolMetadataService>();
        
        // 初始化工具缓存
        await _toolMetadataService.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _dbContext?.Dispose();
        _serviceProvider?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AddAppAsync_ShouldRefreshCache_WhenToolAppAdded()
    {
        // Arrange - 创建 Prompt
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "NewToolPrompt",
            Content = "Execute {{action}} with {{params}}"
        };
        _dbContext.Prompts.Add(prompt);
        await _dbContext.SaveChangesAsync();

        // 验证初始缓存为空
        var initialTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Empty(initialTools);

        // Act - 添加 Tool App
        var newApp = new LlmApp
        {
            Name = "NewTool",
            Description = "A new tool",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        var addedApp = await _appService.AddAppAsync(newApp);

        // Assert - 验证缓存已自动刷新
        var updatedTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Single(updatedTools);
        
        var cachedTool = updatedTools.First();
        Assert.Equal("NewTool", cachedTool.Name);
        Assert.Equal("A new tool", cachedTool.Description);
        Assert.Contains("action", cachedTool.ParametersSchema);
        Assert.Contains("params", cachedTool.ParametersSchema);
    }

    [Fact]
    public async Task UpdateAppAsync_ShouldRefreshCache_WhenToolAppUpdated()
    {
        // Arrange - 创建初始 Tool App
        var prompt1 = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "OriginalPrompt",
            Content = "Old {{param1}}"
        };
        _dbContext.Prompts.Add(prompt1);
        
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "UpdateTestTool",
            Description = "Original",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt1.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        await _toolMetadataService.InitializeAsync();

        // 验证初始状态
        var originalTool = await _toolMetadataService.GetToolByNameAsync("UpdateTestTool");
        Assert.NotNull(originalTool);
        Assert.Equal("Original", originalTool.Description);

        // Act - 更新 App (改变描述和 Prompt)
        var prompt2 = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "UpdatedPrompt",
            Content = "New {{param2}} and {{param3}}"
        };
        _dbContext.Prompts.Add(prompt2);
        await _dbContext.SaveChangesAsync();

        app.Description = "Updated Description";
        app.LlmPromptId = prompt2.Id;
        await _appService.UpdateAppAsync(app);

        // Assert - 验证缓存已自动刷新
        var updatedTool = await _toolMetadataService.GetToolByNameAsync("UpdateTestTool");
        Assert.NotNull(updatedTool);
        Assert.Equal("Updated Description", updatedTool.Description);
        Assert.Contains("param2", updatedTool.ParametersSchema);
        Assert.Contains("param3", updatedTool.ParametersSchema);
        Assert.DoesNotContain("param1", updatedTool.ParametersSchema);
    }

    [Fact]
    public async Task UpdateAppAsync_ShouldRemoveFromCache_WhenToolAppDisabled()
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

        await _toolMetadataService.InitializeAsync();

        // 验证工具存在
        var tools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Contains(tools, t => t.Name == "DisableTool");

        // Act - 禁用 App
        app.IsEnabled = false;
        await _appService.UpdateAppAsync(app);

        // Assert - 验证已从缓存移除
        var updatedTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.DoesNotContain(updatedTools, t => t.Name == "DisableTool");
    }

    [Fact]
    public async Task UpdateAppAsync_ShouldRemoveFromCache_WhenAppTypeChangedFromTool()
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

        await _toolMetadataService.InitializeAsync();

        // 验证工具存在
        var initialTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Contains(initialTools, t => t.Name == "TypeChangeTool");

        // Act - 改变 AppType 为 Agent
        app.AppType = "Agent";
        await _appService.UpdateAppAsync(app);

        // Assert - 验证已从缓存移除
        var updatedTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.DoesNotContain(updatedTools, t => t.Name == "TypeChangeTool");
    }

    [Fact]
    public async Task UpdateAppAsync_ShouldAddToCache_WhenAppTypeChangedToTool()
    {
        // Arrange - 创建 Agent 类型的 App
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AgentPrompt",
            Content = "Agent with {{input}}"
        };
        _dbContext.Prompts.Add(prompt);

        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "ConvertToTool",
            AppType = "Agent",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        _dbContext.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        await _toolMetadataService.InitializeAsync();

        // 验证工具不存在
        var initialTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.DoesNotContain(initialTools, t => t.Name == "ConvertToTool");

        // Act - 改变 AppType 为 Tool
        app.AppType = "Tool";
        await _appService.UpdateAppAsync(app);

        // Assert - 验证已添加到缓存
        var updatedTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Contains(updatedTools, t => t.Name == "ConvertToTool");
        
        var tool = updatedTools.First(t => t.Name == "ConvertToTool");
        Assert.Contains("input", tool.ParametersSchema);
    }

    [Fact]
    public async Task DeleteAppAsync_ShouldRemoveFromCache_WhenToolAppDeleted()
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

        await _toolMetadataService.InitializeAsync();

        // 验证工具存在
        var initialTool = await _toolMetadataService.GetToolByNameAsync("DeleteTool");
        Assert.NotNull(initialTool);

        // Act - 删除 App
        await _appService.DeleteAppAsync(app.Id!);

        // Assert - 验证已从缓存移除
        var tools = await _toolMetadataService.GetAllToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name == "DeleteTool");
        
        var deletedTool = await _toolMetadataService.GetToolByNameAsync("DeleteTool");
        Assert.Null(deletedTool);
    }

    [Fact]
    public async Task AddAppAsync_ShouldNotRefreshCache_WhenNonToolAppAdded()
    {
        // Arrange - 创建 Prompt
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AgentPrompt",
            Content = "Agent {{task}}"
        };
        _dbContext.Prompts.Add(prompt);
        await _dbContext.SaveChangesAsync();

        var initialTools = await _toolMetadataService.GetAllToolsAsync();
        var initialCount = initialTools.Count;

        // Act - 添加 Agent App (非 Tool)
        var agentApp = new LlmApp
        {
            Name = "TestAgent",
            AppType = "Agent",
            IsEnabled = true,
            LlmPromptId = prompt.Id
        };
        await _appService.AddAppAsync(agentApp);

        // Assert - 缓存应该保持不变
        var finalTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Equal(initialCount, finalTools.Count);
        Assert.DoesNotContain(finalTools, t => t.Name == "TestAgent");
    }
}
