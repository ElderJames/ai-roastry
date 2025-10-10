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
        public MockChatClientService() : base(null!, null!, null!, null!) { }
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
        var fetchedMembers = await svc.GetAgentMembersAsync(created.Id!);
        Assert.Equal(2, fetchedMembers.Count);
        Assert.Contains(fetchedMembers, m => m.Role == "researcher");
        Assert.Contains(fetchedMembers, m => m.Role == "writer");
    }

    [Fact]
    public async Task Can_CRUD_AgentTools()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var svc = CreateService(options);

        // Setup dependencies
        using var db = new LlmDbContext(options);
        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4o-mini", Model = "gpt-4o-mini", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id };
        var prompt = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "agent", Content = "you are agent" };
        var mcpServer = new McpServerConfig { Id = Guid.NewGuid().ToString("N"), Name = "fileserver", Url = "http://localhost:8000", Description = "local mcp server" };
        db.ModelTypes.Add(mt);
        db.Configs.Add(cfg);
        db.Prompts.Add(prompt);
        db.McpServerConfigs.Add(mcpServer);
        await db.SaveChangesAsync();

        // Create agent member
        var app = new LlmApp { Id = Guid.NewGuid().ToString("N"), Name = "agent-app", AppType = "AgentGroup" };
        var member = new AgentMember { Id = Guid.NewGuid().ToString("N"), Name = "Agent", Role = "agent", Order = 1, LlmAppId = app.Id, LlmPromptId = prompt.Id, LlmConfigId = cfg.Id };
        db.Apps.Add(app);
        db.AgentMembers.Add(member);
        await db.SaveChangesAsync();

        // Add Internal Tool
        var internalTool = new AgentTool
        {
            AgentMemberId = member.Id,
            ToolId = "context_extractor",
            ToolType = ToolType.Internal
        };
        var addedInternal = await svc.AddAgentToolAsync(internalTool);
        Assert.Equal("context_extractor", addedInternal.ToolId);

        // Add MCP Tool
        var mcpTool = new AgentTool
        {
            AgentMemberId = member.Id,
            ToolId = "read_file",
            ToolType = ToolType.Mcp
        };
        var addedMcp = await svc.AddAgentToolAsync(mcpTool);
        Assert.Equal("read_file", addedMcp.ToolId);

        // Get tools
        var tools = await svc.GetAgentToolsAsync(member.Id);
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.ToolType == ToolType.Internal);
        Assert.Contains(tools, t => t.ToolType == ToolType.Mcp);

        // Set tools (replace all)
        var newTools = new List<AgentTool>
        {
            new AgentTool { AgentMemberId = member.Id, ToolId = "memory_query", ToolType = ToolType.Internal }
        };
        await svc.SetAgentToolsAsync(member.Id, newTools);

        var updatedTools = await svc.GetAgentToolsAsync(member.Id);
        Assert.Single(updatedTools);
        Assert.Equal("memory_query", updatedTools[0].ToolId);

        // Delete tool
        await svc.DeleteAgentToolAsync(member.Id, "memory_query", ToolType.Internal);
        var finalTools = await svc.GetAgentToolsAsync(member.Id);
        Assert.Empty(finalTools);
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