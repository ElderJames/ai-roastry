using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Services;

public class AgentService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;

    public AgentService(IDbContextFactory<LlmDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    // Agent Members
    public async Task<List<AgentMember>> GetAgentMembersAsync(string appId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.AgentMembers
            .Where(m => m.LlmAppId == appId)
            .Include(m => m.LlmPrompt)
            .Include(m => m.LlmConfig)
            .OrderBy(m => m.Order)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<AgentMember?> GetAgentMemberByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.AgentMembers
            .Include(m => m.LlmPrompt)
            .Include(m => m.LlmConfig)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<AgentMember> AddAgentMemberAsync(AgentMember member)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        member.Id = Guid.NewGuid().ToString("N");
        member.CreatedAt = DateTime.UtcNow;
        member.UpdatedAt = DateTime.UtcNow;
        dbContext.AgentMembers.Add(member);
        await dbContext.SaveChangesAsync();
        return member;
    }

    public async Task<AgentMember> UpdateAgentMemberAsync(AgentMember member)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.AgentMembers.FindAsync(member.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Agent member with ID {member.Id} not found.");
        }

        existing.Name = member.Name;
        existing.Description = member.Description;
        existing.Role = member.Role;
        existing.Order = member.Order;
        existing.LlmPromptId = member.LlmPromptId;
        existing.LlmConfigId = member.LlmConfigId;
        existing.IsEnabled = member.IsEnabled;
        existing.ConfigJson = member.ConfigJson;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAgentMemberAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var member = await dbContext.AgentMembers.FindAsync(id);
        if (member == null)
        {
            throw new KeyNotFoundException($"Agent member with ID {id} not found.");
        }

        dbContext.AgentMembers.Remove(member);
        await dbContext.SaveChangesAsync();
    }

    // Tools are now bound to Prompts via PromptTool entity, not to AgentMembers
    // See ToolProviderService for creating tool objects from PromptTools

    // MCP Server Configs
    public async Task<List<McpServerConfig>> GetMcpServerConfigsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.McpServerConfigs
            .OrderBy(c => c.Name)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<McpServerConfig?> GetMcpServerConfigByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.McpServerConfigs.FindAsync(id);
    }

    public async Task<McpServerConfig> AddMcpServerConfigAsync(McpServerConfig config)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        config.Id = Guid.NewGuid().ToString("N");
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;
        dbContext.McpServerConfigs.Add(config);
        await dbContext.SaveChangesAsync();
        return config;
    }

    public async Task<McpServerConfig> UpdateMcpServerConfigAsync(McpServerConfig config)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.McpServerConfigs.FindAsync(config.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"MCP server config with ID {config.Id} not found.");
        }

        existing.Name = config.Name;
        existing.Description = config.Description;
        existing.Command = config.Command;
        existing.Args = config.Args;
        existing.Env = config.Env;
        existing.IsEnabled = config.IsEnabled;
        existing.ConfigJson = config.ConfigJson;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteMcpServerConfigAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var config = await dbContext.McpServerConfigs.FindAsync(id);
        if (config == null)
        {
            throw new KeyNotFoundException($"MCP server config with ID {id} not found.");
        }

        dbContext.McpServerConfigs.Remove(config);
        await dbContext.SaveChangesAsync();
    }
}
