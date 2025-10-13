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

    [Fact(Skip = "AgentTool entity has been removed from the data model")]
    public async Task Can_Assign_Tools_To_AgentMember_And_Save()
    {
        // AgentTool entity has been removed from the data model
        // This test is skipped until the test is updated or removed
        await Task.CompletedTask;
    }
}
