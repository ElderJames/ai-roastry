using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HandlebarsDotNet;
using Microsoft.Extensions.AI;

namespace LY.LlmPool.Web.Services;

public class PromptService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly IChatClientService _chatClientService;
    private readonly ILogger<PromptService> _logger;
    private readonly LlmPoolCacheService _cacheService; // 🎯 缓存服务
    private readonly IServiceProvider _serviceProvider;

    public PromptService(
        IDbContextFactory<LlmDbContext> dbContextFactory, 
        IChatClientService chatClientService,
        ILogger<PromptService> logger,
        LlmPoolCacheService cacheService, // 🎯 注入缓存服务
        IServiceProvider serviceProvider)
    {
        _dbContextFactory = dbContextFactory;
        _chatClientService = chatClientService;
        _logger = logger;
        _cacheService = cacheService;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Get prompts, optionally filtering by tags (any match).
    /// </summary>
    public async Task<List<LlmPrompt>> GetPromptsAsync(IEnumerable<string>? tags = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var query = dbContext.Prompts
            .AsNoTracking()
            .OrderByDescending(x => x.UpdateTime);

        var prompts = await query.ToListAsync();

        if (tags != null && tags.Any())
        {
            var set = new HashSet<string>(tags.Select(t => t?.Trim() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            prompts = prompts.Where(p => !string.IsNullOrEmpty(p.Tags) && p.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(t => set.Contains(t))).ToList();
        }

        return prompts;
    }

    /// <summary>
    /// 获取所有已存在的标签（去重并排序）
    /// </summary>
    public async Task<List<string>> GetAllTagsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var tagsList = await dbContext.Prompts
            .AsNoTracking()
            .Select(p => p.Tags)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToListAsync();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tags in tagsList)
        {
            foreach (var t in (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(t);
            }
        }

        return set.OrderBy(t => t).ToList();
    }

    public async Task<LlmPrompt?> GetPromptByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Prompts
            .AsNoTracking()
            .Include(x => x.PromptTools)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<LlmPrompt> CreatePromptAsync(LlmPrompt prompt)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        prompt.Id = Guid.NewGuid().ToString("N");
        prompt.CreateTime = DateTime.UtcNow;
        prompt.UpdateTime = DateTime.UtcNow;
        prompt.Version = 1;
        prompt.Status = prompt.Status ?? "Draft";

        dbContext.Prompts.Add(prompt);
        await dbContext.SaveChangesAsync();
        return prompt;
    }

    public async Task SubmitForReviewAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var prompt = await dbContext.Prompts.FindAsync(id);
        if (prompt == null) throw new KeyNotFoundException($"Prompt with ID {id} not found.");
        prompt.Status = "PendingReview";
        prompt.UpdateTime = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    public async Task PublishPromptAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var prompt = await dbContext.Prompts.FindAsync(id);
        if (prompt == null) throw new KeyNotFoundException($"Prompt with ID {id} not found.");
        prompt.Status = "Published";
        prompt.UpdateTime = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        // Invalidate cache for related apps
        try
        {
            var relatedApps = await dbContext.Apps
                .Where(a => a.LlmPromptId == prompt.Id)
                .Select(a => a.Id)
                .ToListAsync();
            foreach (var appId in relatedApps)
            {
                await _cacheService.InvalidateAppRelatedCachesAsync(appId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh caches after publishing Prompt {PromptId}", prompt.Id);
        }
    }

    public async Task<LlmPrompt> UpdatePromptAsync(LlmPrompt prompt)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.Prompts.FindAsync(prompt.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Prompt with ID {prompt.Id} not found.");
        }

        // Create history record
        var history = new LlmPromptHistory
        {
            Id = Guid.NewGuid().ToString("N"),
            PromptId = existing.Id!,
            Content = existing.Content,
            Version = existing.Version,
            CreateTime = DateTime.UtcNow
        };
        dbContext.PromptHistory.Add(history);

        // Update prompt
        existing.Name = prompt.Name;
        existing.Description = prompt.Description;
        existing.Content = prompt.Content;
        existing.ModelParameters = prompt.ModelParameters; // 🎯 保存模型参数
        existing.UpdateTime = DateTime.UtcNow;
        existing.Version++;

        await dbContext.SaveChangesAsync();

        // 🎯 刷新所有关联该 Prompt 的 App 的缓存（所有类型）
        try
        {
            var relatedApps = await dbContext.Apps
                .Where(a => a.LlmPromptId == prompt.Id)
                .Select(a => new { a.Id, a.Name, a.AppType })
                .ToListAsync();

            if (relatedApps.Any())
            {
                _logger.LogInformation(
                    "Prompt {PromptId} ({PromptName}) updated, refreshing {Count} related App(s): {AppNames}",
                    prompt.Id, prompt.Name, relatedApps.Count, string.Join(", ", relatedApps.Select(a => $"{a.Name}({a.AppType})")));

                foreach (var app in relatedApps)
                {
                    if (!string.IsNullOrEmpty(app.Id))
                    {
                        // InvalidateAppRelatedCachesAsync 会自动处理 Tool 类型的工具元数据刷新
                        await _cacheService.InvalidateAppRelatedCachesAsync(app.Id);
                    }
                }
            }
            else
            {
                _logger.LogDebug("Prompt {PromptId} ({PromptName}) updated, no related Apps found", 
                    prompt.Id, prompt.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh caches after Prompt {PromptId} update", prompt.Id);
            // 不抛出异常,允许 Prompt 更新继续完成
        }

        return existing;
    }

    public async Task DeletePromptAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var prompt = await dbContext.Prompts.FindAsync(id);
        if (prompt == null)
        {
            throw new KeyNotFoundException($"Prompt with ID {id} not found.");
        }

        // Delete history records
        var history = await dbContext.PromptHistory.Where(x => x.PromptId == id).ToListAsync();
        dbContext.PromptHistory.RemoveRange(history);

        // Delete prompt
        dbContext.Prompts.Remove(prompt);
        await dbContext.SaveChangesAsync();
    }

    public async Task<List<LlmPromptHistory>> GetPromptHistoryAsync(string? promptId = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var query = dbContext.PromptHistory
            .Include(x => x.Prompt)
            .AsNoTracking();

        if (!string.IsNullOrEmpty(promptId))
        {
            query = query.Where(x => x.PromptId == promptId);
        }

        return await query
            .OrderByDescending(x => x.CreateTime).ToListAsync();
    }

    public async Task<LlmPromptHistory> SavePromptHistoryAsync(LlmPromptHistory history)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Get the next version number
        var lastVersion = await dbContext.PromptHistory
            .Where(x => x.PromptId == history.PromptId)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Version)
            .FirstOrDefaultAsync();

        history.Id = Guid.NewGuid().ToString("N");
        history.Version = lastVersion + 1;
        history.CreateTime = DateTime.UtcNow;

        // 🎯 同步更新 prompt 的版本号保持最新（避免 history 记录版本增加 prompt 没有同步更新）
        var prompt = await dbContext.Prompts.FindAsync(history.PromptId);
        if (prompt != null) 
        {  
            prompt.Version = history.Version;
        }

        dbContext.PromptHistory.Add(history);
        await dbContext.SaveChangesAsync();
        return history;
    }

    public async Task<string> TestPromptWithModelAsync(
        string promptId,
        string configId,
        string? parameters,
        Dictionary<string, string>? promptParameters = null,
        string? userMessage = null)
    {
        var prompt = await GetPromptByIdAsync(promptId);
        if (prompt == null)
        {
            throw new KeyNotFoundException($"Prompt with ID {promptId} not found.");
        }

        // 🎯 使用注入的缓存服务创建 ConfigService
        var configService = new ConfigService(_dbContextFactory, _cacheService);
        var config = await configService.GetConfigByIdAsync(configId);
        if (config == null)
        {
            throw new KeyNotFoundException($"Configuration with ID {configId} not found.");
        }

        if (!config.IsEnabled)
        {
            throw new InvalidOperationException($"Configuration {config.Name} is disabled.");
        }

        // Parse parameters into dictionary
        Dictionary<string, object>? modelParams = null;
        if (!string.IsNullOrEmpty(parameters))
        {
            modelParams = ModelParameterHelper.ParseFromKeyValueString(parameters);
        }

        // Replace prompt parameters if provided
        var promptContent = prompt.Content;
        if (promptParameters != null)
        {
            var template = Handlebars.Compile(promptContent);
            promptContent = template(promptParameters);
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, promptContent)
        };

        if (!string.IsNullOrEmpty(userMessage))
        {
            messages.Add(new ChatMessage(ChatRole.User, userMessage));
        }

        var result = await _chatClientService.SendMessageAsync(config, messages, modelParams);
        if (result.Status != "success")
        {
            throw new Exception(result.Message);
        }

        return result.Message;
    }

    public async Task<string> TestPromptWithEndpointAsync(
        string promptId,
        string endpointId,
        string? parameters,
        Dictionary<string, string>? promptParameters = null,
        string? userMessage = null)
    {
        var prompt = await GetPromptByIdAsync(promptId);
        if (prompt == null)
        {
            throw new KeyNotFoundException($"Prompt with ID {promptId} not found.");
        }

        using var scope = _serviceProvider.CreateScope();
        var endpointService = scope.ServiceProvider.GetRequiredService<EndpointService>();
        var endpoint = await endpointService.GetEndpointByIdAsync(endpointId);
        if (endpoint == null)
        {
            throw new KeyNotFoundException($"Endpoint with ID {endpointId} not found.");
        }

        if (!endpoint.IsEnabled)
        {
            throw new InvalidOperationException($"Endpoint {endpoint.Name} is disabled.");
        }

        // Get available config for the endpoint
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var endpointEntity = await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(c => c.LlmConfig)
            .FirstOrDefaultAsync(e => e.Id == endpoint.Id && e.IsEnabled);

        if (endpointEntity == null)
        {
            throw new Exception($"Endpoint {endpoint.Name} not found or disabled");
        }

        var configs = endpointEntity.EndpointConfigs
            .Where(c => c.LlmConfig != null && c.LlmConfig.IsEnabled)
            .OrderBy(c => c.Priority)
            .Select(c => c.LlmConfig)
            .ToList();

        LlmConfig? config = null;
        foreach (var cfg in configs)
        {
            // Simple check - in real implementation we'd use load balancing
            if (cfg != null)
            {
                config = cfg;
                break;
            }
        }

        if (config == null)
        {
            throw new Exception($"No available configuration found for endpoint {endpoint.Name}");
        }

        // Parse parameters into dictionary
        Dictionary<string, object>? modelParams = null;
        if (!string.IsNullOrEmpty(parameters))
        {
            modelParams = ModelParameterHelper.ParseFromKeyValueString(parameters);
        }

        // Replace prompt parameters if provided
        var promptContent = prompt.Content;
        if (promptParameters != null)
        {
            var template = Handlebars.Compile(promptContent);
            promptContent = template(promptParameters);
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, promptContent)
        };

        if (!string.IsNullOrEmpty(userMessage))
        {
            messages.Add(new ChatMessage(ChatRole.User, userMessage));
        }

        var result = await _chatClientService.SendMessageAsync(config, messages, modelParams);
        if (result.Status != "success")
        {
            throw new Exception(result.Message);
        }

        return result.Message;
    }

    public async Task DeletePromptHistoryAsync(string historyId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var history = await dbContext.PromptHistory.FindAsync(historyId);
        if (history != null)
        {
            dbContext.PromptHistory.Remove(history);
            await dbContext.SaveChangesAsync();
        }
    }

    #region PromptTool Management

    /// <summary>
    /// 获取 Prompt 关联的所有工具
    /// </summary>
    public async Task<List<PromptTool>> GetPromptToolsAsync(string promptId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.PromptTools
            .Where(pt => pt.PromptId == promptId)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <summary>
    /// 添加 Prompt 和 Tool 的关联
    /// </summary>
    public async Task<PromptTool> AddPromptToolAsync(PromptTool promptTool)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        // 检查是否已存在
        var existing = await dbContext.PromptTools
            .FirstOrDefaultAsync(pt => 
                pt.PromptId == promptTool.PromptId && 
                pt.ToolId == promptTool.ToolId && 
                pt.ToolType == promptTool.ToolType);

        if (existing != null)
        {
            return existing; // 已存在，直接返回
        }

        dbContext.PromptTools.Add(promptTool);
        await dbContext.SaveChangesAsync();
        return promptTool;
    }

    /// <summary>
    /// 删除 Prompt 和 Tool 的关联
    /// </summary>
    public async Task DeletePromptToolAsync(string promptId, string toolId, ToolType toolType)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        var promptTool = await dbContext.PromptTools
            .FirstOrDefaultAsync(pt => 
                pt.PromptId == promptId && 
                pt.ToolId == toolId && 
                pt.ToolType == toolType);

        if (promptTool != null)
        {
            dbContext.PromptTools.Remove(promptTool);
            await dbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 设置 Prompt 的所有工具（替换现有的）
    /// </summary>
    public async Task SetPromptToolsAsync(string promptId, List<PromptTool> promptTools)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        // 验证所有 App Tool 是否存在
        var appToolIds = promptTools
            .Where(pt => pt.ToolType == ToolType.Internal)
            .Select(pt => pt.ToolId)
            .ToList();

        if (appToolIds.Any())
        {
            // 获取所有存在的 App IDs
            var allApps = await dbContext.Apps.ToListAsync();
            var existingAppIds = new List<string>();
            foreach (var app in allApps)
            {
                if (app.Id != null && appToolIds.Contains(app.Id))
                {
                    existingAppIds.Add(app.Id);
                }
            }

            var missingAppIds = appToolIds.Except(existingAppIds).ToList();
            if (missingAppIds.Any())
            {
                _logger.LogWarning(
                    "⚠️  Attempting to bind non-existent App Tools: {MissingIds}. " +
                    "These will be skipped.",
                    string.Join(", ", missingAppIds));
                
                // 过滤掉不存在的 App Tools
                promptTools = promptTools
                    .Where(pt => pt.ToolType != ToolType.Internal || existingAppIds.Contains(pt.ToolId))
                    .ToList();
            }
        }
        
        // 删除现有的所有工具关联
        var existingTools = await dbContext.PromptTools
            .Where(pt => pt.PromptId == promptId)
            .ToListAsync();

        if (existingTools.Any())
        {
            dbContext.PromptTools.RemoveRange(existingTools);
        }

        // 添加新的工具关联
        if (promptTools.Any())
        {
            foreach (var tool in promptTools)
            {
                tool.PromptId = promptId; // 确保 PromptId 正确
            }
            dbContext.PromptTools.AddRange(promptTools);
        }

        await dbContext.SaveChangesAsync();
    }

    #endregion
}
