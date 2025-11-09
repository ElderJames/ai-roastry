using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.Tools;
using LY.LlmPool.Web.Services.LoadBalancing;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Services;

public class AppService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly ToolMetadataService _toolMetadataService;
    private readonly LlmPoolCacheService _cacheService;
    private readonly LoadBalancerService _loadBalancer;
    private readonly ILogger<AppService> _logger;

    public AppService(
        IDbContextFactory<LlmDbContext> dbContextFactory,
        ToolMetadataService toolMetadataService,
        ILogger<AppService> logger,
        LlmPoolCacheService cacheService,
        LoadBalancerService loadBalancer)
    {
        _dbContextFactory = dbContextFactory;
        _toolMetadataService = toolMetadataService;
        _logger = logger;
        _cacheService = cacheService;
        _loadBalancer = loadBalancer;
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
        // 检查名称全局唯一性
        var uniquenessError = await _cacheService.CheckModelNameUniquenessAsync(app.Name, "App");
        if (uniquenessError != null)
        {
            throw new InvalidOperationException(uniquenessError);
        }

        // 如果是 Tool 类型，验证名称只包含 ASCII 字母、数字、下划线和横线
        if (app.AppType == "Tool")
        {
            if (string.IsNullOrWhiteSpace(app.Name))
            {
                throw new ArgumentException("Tool App name cannot be empty.", nameof(app.Name));
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(app.Name, @"^[a-zA-Z0-9_-]+$"))
            {
                throw new ArgumentException(
                    "Tool App name can only contain ASCII letters (a-z, A-Z), digits (0-9), underscores (_), and hyphens (-). " +
                    "Chinese characters and special symbols are not allowed. " +
                    $"Invalid name: '{app.Name}'",
                    nameof(app.Name));
            }
        }
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        app.Id = Guid.NewGuid().ToString("N");
        app.CreatedAt = DateTime.UtcNow;
        app.UpdatedAt = DateTime.UtcNow;
        dbContext.Apps.Add(app);
        await dbContext.SaveChangesAsync();

        // 🎯 刷新 App 相关缓存（包括 Tool 类型的工具元数据）
        try
        {
            _logger.LogInformation("Refreshing cache for new App: {AppId} ({AppName}, Type: {AppType})", 
                app.Id, app.Name, app.AppType);
            await _cacheService.InvalidateAppRelatedCachesAsync(app.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh cache for new App: {AppId}", app.Id);
            // 非关键错误,继续执行
        }

        return app;
    }

    public async Task<LlmApp> UpdateAppAsync(LlmApp app)
    {
        // 检查名称全局唯一性（排除自身）
        var uniquenessError = await _cacheService.CheckModelNameUniquenessAsync(app.Name, "App", app.Id);
        if (uniquenessError != null)
        {
            throw new InvalidOperationException(uniquenessError);
        }
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.Apps.FindAsync(app.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"App with ID {app.Id} not found.");
        }

        // 如果是 Tool 类型，验证名称只包含 ASCII 字母、数字和下划线
        if (app.AppType == "Tool")
        {
            if (string.IsNullOrWhiteSpace(app.Name))
            {
                throw new ArgumentException("Tool App name cannot be empty.", nameof(app.Name));
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(app.Name, @"^[a-zA-Z0-9_-]+$"))
            {
                throw new ArgumentException(
                    "Tool App name can only contain ASCII letters (a-z, A-Z), digits (0-9), underscores (_), and hyphens (-). " +
                    "Chinese characters and special symbols are not allowed. " +
                    $"Invalid name: '{app.Name}'",
                    nameof(app.Name));
            }
        }

        // 记录旧的 AppType 用于检测类型变化
        var oldAppType = existing.AppType;
        var oldIsEnabled = existing.IsEnabled;
        var oldName = existing.Name;

        existing.Name = app.Name;
        existing.Description = app.Description;
        existing.AppType = app.AppType;
        existing.OrchestrationMode = app.OrchestrationMode;
        existing.LlmPromptId = app.LlmPromptId;
        existing.LlmConfigId = app.LlmConfigId;
        existing.EndpointId = app.EndpointId;
        existing.IsEnabled = app.IsEnabled;
        existing.ConfigJson = app.ConfigJson;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();

        // 🎯 刷新 App 相关缓存（包括 Tool 类型的工具元数据）
        try
        {
            _logger.LogInformation("Refreshing cache for updated App: {AppId} ({AppName}, Type: {AppType})", 
                app.Id, app.Name, app.AppType);
            
            // 🔥 如果 AppType 从 Tool 变为其他类型，需要先移除工具元数据
            if (oldAppType == "Tool" && app.AppType != "Tool")
            {
                _logger.LogInformation("App {AppId} 的类型从 Tool 改为 {NewType}，移除工具元数据", app.Id, app.AppType);
                await _toolMetadataService.RefreshAppToolAsync(app.Id!);
            }
            
            // 使用支持旧名称的重载方法（如果名称变更，会自动清除旧名称缓存）
            var oldNameToInvalidate = oldName != app.Name ? oldName : null;
            await _cacheService.InvalidateAppRelatedCachesAsync(app.Id!, oldNameToInvalidate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh cache for updated App: {AppId}", app.Id);
            // 非关键错误,继续执行
        }

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

        // 记录信息用于日志和后续缓存处理
        var appName = app.Name;
        var appType = app.AppType;
        var isToolType = appType == "Tool";

        // 🔥 如果是 Tool 类型，先移除工具元数据缓存（在删除前）
        if (isToolType)
        {
            try
            {
                _logger.LogInformation("删除前先移除 Tool App {AppId} ({AppName}) 的工具元数据", id, appName);
                await _toolMetadataService.RemoveToolByNameAsync(appName);
                
                // 🔥 额外确保所有工具缓存也被清除
                await _toolMetadataService.RefreshAllToolsCacheAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除前移除工具元数据失败: {AppId}", id);
            }
        }

        // 🎯 删除 App
        dbContext.Apps.Remove(app);
        await dbContext.SaveChangesAsync();

        _logger.LogInformation("App deleted: {AppId} ({AppName}, Type: {AppType})", id, appName, appType);

        // 🎯 删除后刷新其他缓存（负载均衡缓存等）
        try
        {
            _logger.LogInformation("Refreshing cache after deleting App: {AppId} ({AppName}, Type: {AppType})", 
                id, appName, appType);
            await _cacheService.InvalidateAppRelatedCachesAsync(id);
            
            // 🔥 如果是 Tool 类型，确保工具缓存完全刷新
            if (isToolType)
            {
                _logger.LogInformation("删除后刷新 Tool App {AppId} ({AppName}) 的工具元数据缓存", id, appName);
                await _toolMetadataService.RefreshAllToolsCacheAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh cache after deleting App: {AppId}", id);
            // 非关键错误
        }
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

    // Agent Member Management
    public async Task<List<AgentMember>> GetAgentMembersByAppIdAsync(string appId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.AgentMembers
            .Include(m => m.LlmPrompt)
                .ThenInclude(p => p!.PromptTools)
            .Include(m => m.LlmConfig)
            .Where(m => m.LlmAppId == appId)
            .OrderBy(m => m.Order)
            .ToListAsync();
    }

    public async Task<AgentMember> AddAgentMemberAsync(AgentMember member)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        if (string.IsNullOrWhiteSpace(member.Id))
        {
            member.Id = Guid.NewGuid().ToString("N");
        }
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
            throw new KeyNotFoundException($"AgentMember with ID {member.Id} not found.");
        }

        existing.Name = member.Name;
        existing.Role = member.Role;
        existing.Order = member.Order;
        existing.LlmPromptId = member.LlmPromptId;
        existing.LlmConfigId = member.LlmConfigId;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAgentMemberAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.AgentMembers.FindAsync(id);
        if (existing == null)
        {
            return;
        }
        dbContext.AgentMembers.Remove(existing);
        await dbContext.SaveChangesAsync();
    }

    public Task RemoveAgentMemberAsync(string id) => DeleteAgentMemberAsync(id);

    // Prompt Management (helper methods)
    public async Task<LlmPrompt> AddPromptAsync(LlmPrompt prompt)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        if (string.IsNullOrWhiteSpace(prompt.Id))
        {
            prompt.Id = Guid.NewGuid().ToString("N");
        }
        prompt.CreateTime = DateTime.UtcNow;
        prompt.UpdateTime = DateTime.UtcNow;

        dbContext.Prompts.Add(prompt);
        await dbContext.SaveChangesAsync();
        return prompt;
    }
}

