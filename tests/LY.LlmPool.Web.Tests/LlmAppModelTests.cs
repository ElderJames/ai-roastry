using System;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class LlmAppModelTests
{
    private static LlmDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LlmDbContext(options);
    }

    [Fact]
    public async Task Can_Create_Prompt_App_And_Save()
    {
        using var db = CreateDb();
        var prompt = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "sys", Content = "system" };
        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4o-mini", Model = "gpt-4o-mini", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id, ModelType = mt };
        db.ModelTypes.Add(mt);
        db.Prompts.Add(prompt);
        db.Configs.Add(cfg);
        var app = new LlmApp { Id = Guid.NewGuid().ToString("N"), Name = "app1", AppType = "Prompt", LlmPromptId = prompt.Id, LlmConfigId = cfg.Id, IsEnabled = true };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        var loaded = await db.Apps.Include(a => a.LlmPrompt).Include(a => a.LlmConfig).FirstAsync(a => a.Id == app.Id);
        Assert.Equal("Prompt", loaded.AppType);
        Assert.NotNull(loaded.LlmPrompt);
        Assert.NotNull(loaded.LlmConfig);
    }

    [Fact]
    public async Task Can_Create_AgentGroup_App_With_Members()
    {
        using var db = CreateDb();
        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4o-mini", Model = "gpt-4o-mini", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id, ModelType = mt };
        var p1 = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "agent1", Content = "you are agent1" };
        var p2 = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "agent2", Content = "you are agent2" };
        db.ModelTypes.Add(mt);
        db.Configs.Add(cfg);
        db.Prompts.AddRange(p1, p2);

        var app = new LlmApp { Id = Guid.NewGuid().ToString("N"), Name = "ag-app", AppType = "AgentGroup", OrchestrationMode = OrchestrationMode.Sequential };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        var m1 = new AgentMember { Id = Guid.NewGuid().ToString("N"), Name = "A1", Role = "researcher", Order = 1, LlmAppId = app.Id!, LlmPromptId = p1.Id!, LlmConfigId = cfg.Id! };
        var m2 = new AgentMember { Id = Guid.NewGuid().ToString("N"), Name = "A2", Role = "writer", Order = 2, LlmAppId = app.Id!, LlmPromptId = p2.Id!, LlmConfigId = cfg.Id! };
        db.AgentMembers.AddRange(m1, m2);
        await db.SaveChangesAsync();

        var loaded = await db.Apps.Include(a => a.AgentMembers).FirstAsync(a => a.Id == app.Id);
        Assert.Equal("AgentGroup", loaded.AppType);
        Assert.Equal(2, loaded.AgentMembers.Count);
    }
}
