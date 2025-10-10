using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Services;

public class AppService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;

    public AppService(IDbContextFactory<LlmDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<List<LlmApp>> GetAppsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Apps
            .Include(a => a.LlmPrompt)
            .Include(a => a.LlmConfig)
            .Include(a => a.Endpoint)
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync();
    }

    public async Task<LlmApp?> GetAppByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Apps
            .Include(a => a.LlmPrompt)
            .Include(a => a.LlmConfig)
            .Include(a => a.Endpoint)
            .Include(a => a.AgentMembers)
                .ThenInclude(m => m.LlmPrompt)
            .Include(a => a.AgentMembers)
                .ThenInclude(m => m.LlmConfig)
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public Task<LlmApp?> GetAppAsync(string id) => GetAppByIdAsync(id);

    public async Task<LlmApp?> GetAppByNameAsync(string name)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Apps
            .Include(a => a.LlmPrompt)
            .Include(a => a.LlmConfig)
            .Include(a => a.Endpoint)
            .Include(a => a.AgentMembers)
                .ThenInclude(m => m.LlmPrompt)
            .Include(a => a.AgentMembers)
                .ThenInclude(m => m.LlmConfig)
            .FirstOrDefaultAsync(a => a.Name == name && a.IsEnabled);
    }

    public async Task<LlmApp> AddAppAsync(LlmApp app)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        app.Id = Guid.NewGuid().ToString("N");
        app.CreatedAt = DateTime.UtcNow;
        app.UpdatedAt = DateTime.UtcNow;
        dbContext.Apps.Add(app);
        await dbContext.SaveChangesAsync();
        return app;
    }

    public async Task<LlmApp> UpdateAppAsync(LlmApp app)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.Apps.FindAsync(app.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"App with ID {app.Id} not found.");
        }

        existing.Name = app.Name;
        existing.Description = app.Description;
        existing.AppType = app.AppType;
        existing.OrchestrationMode = app.OrchestrationMode;
        existing.PromptId = app.PromptId;
        existing.LlmConfigId = app.LlmConfigId;
        existing.EndpointId = app.EndpointId;
        existing.IsEnabled = app.IsEnabled;
        existing.ConfigJson = app.ConfigJson;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAppAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var app = await dbContext.Apps.FindAsync(id);
        if (app == null)
        {
            throw new KeyNotFoundException($"App with ID {id} not found.");
        }

        dbContext.Apps.Remove(app);
        await dbContext.SaveChangesAsync();
    }

    // Create an AgentGroup app with members in one shot
    public async Task<LlmApp> CreateAgentGroupWithMembersAsync(LlmApp app, IEnumerable<AgentMember> members)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        app.Id = Guid.NewGuid().ToString("N");
        app.AppType = string.IsNullOrWhiteSpace(app.AppType) ? "AgentGroup" : app.AppType;
        app.CreatedAt = DateTime.UtcNow;
        app.UpdatedAt = DateTime.UtcNow;
        dbContext.Apps.Add(app);

        var order = 1;
        foreach (var m in members)
        {
            if (string.IsNullOrWhiteSpace(m.LlmPromptId) && string.IsNullOrWhiteSpace(m.LlmConfigId))
            {
                continue;
            }
            m.Id = Guid.NewGuid().ToString("N");
            m.LlmAppId = app.Id;
            m.Order = m.Order == 0 ? order : m.Order;
            if (string.IsNullOrWhiteSpace(m.Name))
            {
                m.Name = $"Agent {m.Order}";
            }
            dbContext.AgentMembers.Add(m);
            order = Math.Max(order + 1, m.Order + 1);
        }

        await dbContext.SaveChangesAsync();
        return app;
    }
}