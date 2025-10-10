#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LY.LlmPool.Web.Tests;

public class MigrationTests : IAsyncLifetime
{
    private ServiceProvider? _serviceProvider;
    private LlmDbContext? _dbContext;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        // Use a temporary file database instead of in-memory for migrations to work
        var dbPath = System.IO.Path.GetTempFileName();
        services.AddDbContext<LlmDbContext>(options =>
            options.UseSqlite($"DataSource={dbPath}")
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<LlmDbContext>();

        // Ensure database is created and all migrations are applied
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    [Fact]
    public void Database_Can_Be_Created_With_All_Tables()
    {
        Assert.NotNull(_dbContext);

        // Verify that all expected tables exist
        var tableNames = _dbContext.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .ToList();

        Assert.Contains("llm_apps", tableNames);
        Assert.Contains("AgentMembers", tableNames);
        Assert.Contains("AgentTools", tableNames);
        Assert.Contains("McpServerConfigs", tableNames);
    }

    [Fact]
    public async Task LlmApp_Default_AppType_Is_Prompt()
    {
        var app = new LlmApp { Id = Guid.NewGuid().ToString("N"), Name = "Test App" };
        _dbContext!.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        // Reload from database
        var savedApp = await _dbContext.Apps.FindAsync(app.Id);
        Assert.NotNull(savedApp);
        Assert.Equal("Prompt", savedApp.AppType);
    }

    [Fact]
    public async Task AgentMember_Can_Be_Created_And_Queried()
    {
        // Create an app first
        var app = new LlmApp { Id = Guid.NewGuid().ToString("N"), Name = "Test AgentGroup", AppType = "AgentGroup" };
        _dbContext!.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        // Create agent member
        var member = new AgentMember
        {
            Id = Guid.NewGuid().ToString("N"),
            LlmAppId = app.Id!,
            Name = "Test Agent",
            Role = "assistant",
            Order = 1
        };
        _dbContext.AgentMembers.Add(member);
        await _dbContext.SaveChangesAsync();

        // Query it back
        var savedMember = await _dbContext.AgentMembers
            .FirstOrDefaultAsync(m => m.Id == member.Id);

        Assert.NotNull(savedMember);
        Assert.Equal(app.Id, savedMember.LlmAppId);
        Assert.Equal("Test Agent", savedMember.Name);
        Assert.Equal(1, savedMember.Order);
    }

    [Fact]
    public async Task AgentTool_Can_Be_Created_And_Queried()
    {
        // Create an app and member first
        var app = new LlmApp { Id = Guid.NewGuid().ToString("N"), Name = "Test AgentGroup", AppType = "AgentGroup" };
        _dbContext!.Apps.Add(app);
        await _dbContext.SaveChangesAsync();

        var member = new AgentMember
        {
            Id = Guid.NewGuid().ToString("N"),
            LlmAppId = app.Id!,
            Name = "Test Agent",
            Order = 1
        };
        _dbContext.AgentMembers.Add(member);
        await _dbContext.SaveChangesAsync();

        // Create agent tool
        var tool = new AgentTool
        {
            AgentMemberId = member.Id,
            ToolId = "test-tool",
            ToolType = ToolType.Internal
        };
        _dbContext.AgentTools.Add(tool);
        await _dbContext.SaveChangesAsync();

        // Query it back
        var savedTool = await _dbContext.AgentTools
            .FirstOrDefaultAsync(t => t.AgentMemberId == member.Id && t.ToolId == "test-tool");

        Assert.NotNull(savedTool);
        Assert.Equal(ToolType.Internal, savedTool.ToolType);
    }

    [Fact]
    public async Task McpServerConfig_Can_Be_Created_And_Queried()
    {
        var config = new McpServerConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Test MCP Server",
            Url = "http://localhost:3000",
            Description = "Test server"
        };
        _dbContext!.McpServerConfigs.Add(config);
        await _dbContext.SaveChangesAsync();

        // Query it back
        var savedConfig = await _dbContext.McpServerConfigs
            .FirstOrDefaultAsync(c => c.Name == "Test MCP Server");

        Assert.NotNull(savedConfig);
        Assert.Equal("http://localhost:3000", savedConfig.Url);
        Assert.Equal("Test server", savedConfig.Description);
    }
}