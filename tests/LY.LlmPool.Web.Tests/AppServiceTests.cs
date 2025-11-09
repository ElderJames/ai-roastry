#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using LY.LlmPool.Web.Services.LoadBalancing;
using LY.LlmPool.Web.Services.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class AppServiceTests
{
    private static LlmDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LlmDbContext(options);
    }

    private static AppService CreateService(DbContextOptions<LlmDbContext> options)
    {
        var factory = new TestDbContextFactory(options);
        var toolMetadataService = new MockToolMetadataService();
        var cacheService = new MockCacheService();
        var loadBalancerService = new MockLoadBalancerService();
        return new AppService(factory, toolMetadataService, NullLogger<AppService>.Instance, cacheService, loadBalancerService);
    }

    // Mock classes for testing
    private class TestDbContextFactory : IDbContextFactory<LlmDbContext>
    {
        private readonly DbContextOptions<LlmDbContext> _options;
        public TestDbContextFactory(DbContextOptions<LlmDbContext> options) => _options = options;
        public LlmDbContext CreateDbContext() => new LlmDbContext(_options);
    }

    private class MockToolMetadataService : ToolMetadataService
    {
        public MockToolMetadataService() : base(null!, null!, null!, null!, null!) { }
    }

    private class MockCacheService : LlmPoolCacheService
    {
        public MockCacheService() : base(null!, NullLogger<LlmPoolCacheService>.Instance, null!, null!) { }

        // 重写方法以避免数据库调用
        public override async Task<string?> CheckModelNameUniquenessAsync(string name, string entityType, string? excludeId = null, CancellationToken cancellationToken = default)
        {
            // 对于测试，总是返回 null（表示名称唯一）
            await Task.Yield(); // 让方法真正异步
            return null;
        }
    }

    private class MockLoadBalancerService : LoadBalancerService
    {
        public MockLoadBalancerService() : base(null!, null!) { }
    }

    [Fact]
    public async Task Can_CRUD_LlmApps()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var svc = CreateService(options);

        // Setup dependencies
        using var db = new LlmDbContext(options);
        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4o-mini", Model = "gpt-4o-mini", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id };
        var prompt = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "sys", Content = "system" };
        db.ModelTypes.Add(mt);
        db.Configs.Add(cfg);
        db.Prompts.Add(prompt);
        await db.SaveChangesAsync();

        // Create Prompt App
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "test-app",
            AppType = "Prompt",
            LlmPromptId = prompt.Id,
            LlmConfigId = cfg.Id,
            IsEnabled = true
        };
        var created = await svc.AddAppAsync(app);
        Assert.NotNull(created.Id);

        // Get by ID
        var fetched = await svc.GetAppByIdAsync(created.Id!);
        Assert.NotNull(fetched);
        Assert.Equal("test-app", fetched!.Name);

        // Get by Name
        var fetchedByName = await svc.GetAppByNameAsync("test-app");
        Assert.NotNull(fetchedByName);
        Assert.Equal(created.Id, fetchedByName!.Id);

        // List all
        var list = await svc.GetAppsAsync();
        Assert.Contains(list, a => a.Id == created.Id);

        // Update
        created.Name = "updated-app";
        var updated = await svc.UpdateAppAsync(created);
        Assert.Equal("updated-app", updated.Name);

        // Delete
        await svc.DeleteAppAsync(created.Id!);
        var deleted = await svc.GetAppByIdAsync(created.Id!);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task Can_CRUD_AgentGroup_With_Members()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var svc = CreateService(options);

        // Setup dependencies
        using var db = new LlmDbContext(options);
        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg1 = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4o-mini", Model = "gpt-4o-mini", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id };
        var cfg2 = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4", Model = "gpt-4", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id };
        var prompt1 = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "agent1", Content = "you are agent1" };
        var prompt2 = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "agent2", Content = "you are agent2" };
        db.ModelTypes.Add(mt);
        db.Configs.AddRange(cfg1, cfg2);
        db.Prompts.AddRange(prompt1, prompt2);
        await db.SaveChangesAsync();

        // Create AgentGroup with members
        var app = new LlmApp
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "agent-group",
            AppType = "AgentGroup",
            OrchestrationMode = OrchestrationMode.Sequential
        };

        var members = new List<AgentMember>
        {
            new AgentMember { Id = Guid.NewGuid().ToString("N"), Name = "Researcher", Role = "researcher", Order = 1, LlmPromptId = prompt1.Id, LlmConfigId = cfg1.Id },
            new AgentMember { Id = Guid.NewGuid().ToString("N"), Name = "Writer", Role = "writer", Order = 2, LlmPromptId = prompt2.Id, LlmConfigId = cfg2.Id }
        };

        var created = await svc.CreateAgentGroupWithMembersAsync(app, members);
        Assert.NotNull(created.Id);
        Assert.Equal(2, created.AgentMembers.Count);

        // Get members
        var fetchedMembers = await svc.GetAgentMembersByAppIdAsync(created.Id!);
        Assert.Equal(2, fetchedMembers.Count);
        Assert.Contains(fetchedMembers, m => m.Role == "researcher");
        Assert.Contains(fetchedMembers, m => m.Role == "writer");
    }
}