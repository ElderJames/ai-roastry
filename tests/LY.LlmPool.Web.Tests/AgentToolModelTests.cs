using System;
using System.Linq;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class AgentToolModelTests
{
    private static LlmDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LlmDbContext(options);
    }

    [Fact]
    public async Task Can_Assign_Tools_To_AgentMember_And_Save()
    {
        using var db = CreateDb();

        var mt = new LlmModelType { Id = Guid.NewGuid().ToString("N"), Name = "openai" };
        var cfg = new LlmConfig { Id = Guid.NewGuid().ToString("N"), Name = "gpt-4o-mini", Model = "gpt-4o-mini", BaseUrl = "https://api.openai.com/v1", ApiKey = "x", ModelTypeId = mt.Id, ModelType = mt };
        var p1 = new LlmPrompt { Id = Guid.NewGuid().ToString("N"), Name = "agent1", Content = "you are agent1" };
        db.ModelTypes.Add(mt);
        db.Configs.Add(cfg);
        db.Prompts.Add(p1);

        var app = new LlmApp { Id = Guid.NewGuid().ToString("N"), Name = "ag-app", AppType = "AgentGroup", OrchestrationMode = OrchestrationMode.Sequential };
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        var member = new AgentMember { Id = Guid.NewGuid().ToString("N"), Name = "A1", Role = "worker", Order = 1, LlmAppId = app.Id!, LlmPromptId = p1.Id!, LlmConfigId = cfg.Id! };
        db.AgentMembers.Add(member);
        await db.SaveChangesAsync();

        var tInternal = new AgentTool { AgentMemberId = member.Id, ToolId = "sum", ToolType = ToolType.Internal };
        var tMcp = new AgentTool { AgentMemberId = member.Id, ToolId = "mcp://files/list", ToolType = ToolType.Mcp };
        db.AgentTools.AddRange(tInternal, tMcp);
        await db.SaveChangesAsync();

        var loaded = await db.AgentMembers.Include(m => m.AgentTools).FirstAsync(m => m.Id == member.Id);
        Assert.Equal(2, loaded.AgentTools.Count);
        Assert.Contains(loaded.AgentTools, t => t.ToolType == ToolType.Internal && t.ToolId == "sum");
        Assert.Contains(loaded.AgentTools, t => t.ToolType == ToolType.Mcp && t.ToolId.StartsWith("mcp://"));

        // verify EF prevents duplicate composite key entries within the same context
        Assert.Throws<InvalidOperationException>(() =>
        {
            db.AgentTools.Add(new AgentTool { AgentMemberId = member.Id, ToolId = "sum", ToolType = ToolType.Internal });
            db.ChangeTracker.DetectChanges();
        });
    }
}
