#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class LlmPoolServiceTests
{
    private static LlmDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LlmDbContext(options);
    }

    private static LlmPoolService CreateService(DbContextOptions<LlmDbContext> options)
    {
        var factory = new TestDbContextFactory(options);
        var chatClient = new MockChatClientService();
        return new LlmPoolService(factory, NullLogger<LlmPoolService>.Instance, chatClient);
    }

    // Mock classes for testing
    private class TestDbContextFactory : IDbContextFactory<LlmDbContext>
    {
        private readonly DbContextOptions<LlmDbContext> _options;
        public TestDbContextFactory(DbContextOptions<LlmDbContext> options) => _options = options;
        public LlmDbContext CreateDbContext() => new LlmDbContext(_options);
    }

    private class MockChatClientService : ChatClientService
    {
        public MockChatClientService() : base(null!, null!, null!, null!, null!) { }
    }

    [Fact(Skip = "App CRUD operations have been moved to AppService")]
    public Task Can_CRUD_LlmApps()
    {
        // This test has been moved to AppServiceTests.cs
        return Task.CompletedTask;
    }

    [Fact(Skip = "AgentGroup CRUD operations have been moved to AppService")]
    public Task Can_CRUD_AgentGroup_With_Members()
    {
        // This test has been moved to AppServiceTests.cs
        return Task.CompletedTask;
    }

    [Fact(Skip = "AgentTool entity and related methods have been removed")]
    public async Task Can_CRUD_AgentTools()
    {
        // AgentTool entity has been removed from the data model
        // This test is skipped until the test is updated or removed
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetConfigByNameAsync_Returns_Config_When_Available()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var svc = CreateService(options);

        // Setup
        using var db = new LlmDbContext(options);
        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg = new LlmConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "gpt-4o-mini",
            Model = "gpt-4o-mini",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "test-key",
            ModelTypeId = mt.Id,
            IsEnabled = true
        };
        db.ModelTypes.Add(mt);
        db.Configs.Add(cfg);
        await db.SaveChangesAsync();

        // Test
        var result = await svc.GetConfigByNameAsync("gpt-4o-mini");
        Assert.NotNull(result);
        Assert.Equal("gpt-4o-mini", result!.Name);
    }

    [Fact]
    public async Task Data_Validation_Works()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // Test: Basic entity validation
        using var db = new LlmDbContext(options);
        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4o-mini", Model = "gpt-4o-mini", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id };
        var prompt = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "agent", Content = "content" };
        db.ModelTypes.Add(mt);
        db.Configs.Add(cfg);
        db.Prompts.Add(prompt);
        await db.SaveChangesAsync();

        // Verify entities were saved
        var savedMt = await db.ModelTypes.FindAsync(mt.Id);
        var savedCfg = await db.Configs.FindAsync(cfg.Id);
        var savedPrompt = await db.Prompts.FindAsync(prompt.Id);

        Assert.NotNull(savedMt);
        Assert.NotNull(savedCfg);
        Assert.NotNull(savedPrompt);
        Assert.Equal("openai", savedMt!.Name);
        Assert.Equal("gpt-4o-mini", savedCfg!.Name);
        Assert.Equal("agent", savedPrompt!.Name);
    }
}
