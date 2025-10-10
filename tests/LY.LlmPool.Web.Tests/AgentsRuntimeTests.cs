using System.Text.Json;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.Agents;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class AgentsRuntimeTests
{
    [Fact]
    public void Agent_FromMember_Maps_Basic_Fields()
    {
        var m = new AgentMember
        {
            Id = "m1",
            Name = "Planner",
            Role = "planner",
            LlmPrompt = new LlmPrompt { Id = "p1", Name = "P", Content = "You are a planner" },
            LlmConfig = new LlmConfig { Id = "c1", Name = "C", Model = "gpt-4o-mini", ApiKey = "k" }
        };
        var agent = Agent.FromMember(m);
        Assert.Equal("m1", agent.Id);
        Assert.Equal("Planner", agent.Name);
        Assert.Equal("planner", agent.Role);
        Assert.NotNull(agent.Prompt);
        Assert.NotNull(agent.Config);
    }

    [Fact]
    public async Task InternalTool_Validate_And_Execute()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}");
        var tool = new InternalPluginTool("t-internal", "Echo", schema: doc.RootElement);
        var ok = tool.Validate(JsonDocument.Parse("{\"q\":\"hi\"}").RootElement);
        Assert.True(ok.ok);
        var result = await tool.ExecuteAsync(JsonDocument.Parse("{\"q\":\"hi\"}").RootElement);
        Assert.Contains("Echo executed", result);
    }
}
