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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace LY.LlmPool.Web.Tests;

/// <summary>
/// Prompt 更新缓存测试
/// 验证 Prompt 更新后，关联的 Tool App 缓存会自动刷新
/// </summary>
public class PromptUpdateCacheTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private ServiceProvider _serviceProvider = null!;
    private LlmDbContext _dbContext = null!;
    private PromptService _promptService = null!;
    private ToolMetadataService _toolMetadataService = null!;

    public PromptUpdateCacheTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
        
        // 注册 McpClientsFactory
        services.AddSingleton<McpClientsFactory>();
        
        // 注册服务
        services.AddTransient<PromptParameterService>();
        services.AddTransient<ToolMetadataService>();
        services.AddSingleton<LlmPoolCacheService>();
        services.AddTransient<LoadBalancerService>();
        services.AddTransient<ConfigService>();
        services.AddTransient<EndpointService>();
        services.AddTransient<PromptService>();
        
        // Mock IChatClientService - PromptService 只在某些方法中使用，测试不需要它
        services.AddTransient<IChatClientService>(sp => null!);
        
        services.AddLogging();
        
        _serviceProvider = services.BuildServiceProvider();
        
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<LlmDbContext>>();
        _dbContext = await factory.CreateDbContextAsync();
        
        _promptService = _serviceProvider.GetRequiredService<PromptService>();
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
    public async Task UpdatePrompt_ShouldRefreshRelatedToolAppCaches()
    {
        // Arrange - 创建 Prompt 和关联的 Tool App
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "TestPrompt",
            Content = "Execute {{action}} with {{param1}}",
            Description = "Original Prompt",
            Version = 1,
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };
        _dbContext.Prompts.Add(prompt);

        var toolApp1 = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Tool1",
            Description = "First tool",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var toolApp2 = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Tool2",
            Description = "Second tool",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var nonToolApp = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AgentApp",
            Description = "Not a tool",
            AppType = "Agent",
            IsEnabled = true,
            LlmPromptId = prompt.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Apps.AddRange(toolApp1, toolApp2, nonToolApp);
        await _dbContext.SaveChangesAsync();

        // 初始化工具缓存
        await _toolMetadataService.InitializeAsync();

        // 验证初始缓存状态
        var initialTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Equal(2, initialTools.Count); // 只有 Tool1 和 Tool2
        
        var tool1 = await _toolMetadataService.GetToolByNameAsync("Tool1");
        Assert.NotNull(tool1);
        Assert.Contains("action", tool1.ParametersSchema);
        Assert.Contains("param1", tool1.ParametersSchema);
        Assert.DoesNotContain("param2", tool1.ParametersSchema);

        _output.WriteLine($"✅ 初始缓存验证成功: Tool1 参数包含 action 和 param1");

        // Act - 更新 Prompt 内容（修改参数）
        prompt.Content = "Execute {{action}} with {{param2}} and {{param3}}";
        prompt.Description = "Updated Prompt";
        await _promptService.UpdatePromptAsync(prompt);

        _output.WriteLine($"✅ Prompt 已更新: 参数从 param1 改为 param2 和 param3");

        // Assert - 验证工具缓存已自动刷新
        var updatedTool1 = await _toolMetadataService.GetToolByNameAsync("Tool1");
        Assert.NotNull(updatedTool1);
        Assert.Contains("action", updatedTool1.ParametersSchema);
        Assert.Contains("param2", updatedTool1.ParametersSchema);
        Assert.Contains("param3", updatedTool1.ParametersSchema);
        Assert.DoesNotContain("param1", updatedTool1.ParametersSchema);

        _output.WriteLine($"✅ Tool1 缓存已刷新: 参数已更新为 action, param2, param3");

        var updatedTool2 = await _toolMetadataService.GetToolByNameAsync("Tool2");
        Assert.NotNull(updatedTool2);
        Assert.Contains("action", updatedTool2.ParametersSchema);
        Assert.Contains("param2", updatedTool2.ParametersSchema);
        Assert.Contains("param3", updatedTool2.ParametersSchema);
        Assert.DoesNotContain("param1", updatedTool2.ParametersSchema);

        _output.WriteLine($"✅ Tool2 缓存已刷新: 参数已更新为 action, param2, param3");

        // 验证非 Tool 类型的 App 不会出现在工具缓存中
        var allTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.DoesNotContain(allTools, t => t.Name == "AgentApp");

        _output.WriteLine($"✅ 非 Tool 类型的 App (AgentApp) 未出现在工具缓存中");
    }

    [Fact]
    public async Task UpdatePrompt_ShouldOnlyRefreshToolApps_NotAgentApps()
    {
        // Arrange
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "SharedPrompt",
            Content = "Process {{input}}",
            Version = 1,
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };
        _dbContext.Prompts.Add(prompt);

        var toolApp = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "ToolUsingPrompt",
            AppType = "Tool",
            IsEnabled = true,
            LlmPromptId = prompt.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var agentApp = new LlmApp
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AgentUsingPrompt",
            AppType = "Agent",
            IsEnabled = true,
            LlmPromptId = prompt.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Apps.AddRange(toolApp, agentApp);
        await _dbContext.SaveChangesAsync();

        await _toolMetadataService.InitializeAsync();

        // 验证只有 Tool 在缓存中
        var initialTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Single(initialTools);
        Assert.Equal("ToolUsingPrompt", initialTools.First().Name);

        // Act - 更新 Prompt
        prompt.Content = "Process {{newInput}} and {{extraData}}";
        await _promptService.UpdatePromptAsync(prompt);

        // Assert - 验证工具缓存已更新
        var updatedTool = await _toolMetadataService.GetToolByNameAsync("ToolUsingPrompt");
        Assert.NotNull(updatedTool);
        Assert.Contains("newInput", updatedTool.ParametersSchema);
        Assert.Contains("extraData", updatedTool.ParametersSchema);
        Assert.DoesNotContain("input", updatedTool.ParametersSchema);

        // 验证 Agent 仍然不在工具缓存中
        var allTools = await _toolMetadataService.GetAllToolsAsync();
        Assert.Single(allTools);
        Assert.DoesNotContain(allTools, t => t.Name == "AgentUsingPrompt");

        _output.WriteLine($"✅ 只有 Tool App 的缓存被刷新，Agent App 不受影响");
    }

    [Fact]
    public async Task UpdatePrompt_WithNoRelatedToolApps_ShouldNotFail()
    {
        // Arrange - 创建没有关联 Tool App 的 Prompt
        var prompt = new LlmPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "StandalonePrompt",
            Content = "{{message}}",
            Version = 1,
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };
        _dbContext.Prompts.Add(prompt);
        await _dbContext.SaveChangesAsync();

        // Act - 更新 Prompt
        prompt.Content = "Updated {{newMessage}}";
        var exception = await Record.ExceptionAsync(async () =>
        {
            await _promptService.UpdatePromptAsync(prompt);
        });

        // Assert - 应该不会抛出异常
        Assert.Null(exception);
        _output.WriteLine($"✅ 更新没有关联 Tool App 的 Prompt 成功，未抛出异常");
    }
}
