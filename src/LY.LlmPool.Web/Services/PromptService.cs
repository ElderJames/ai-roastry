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

    public PromptService(
        IDbContextFactory<LlmDbContext> dbContextFactory, 
        IChatClientService chatClientService,
        ILogger<PromptService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _chatClientService = chatClientService;
        _logger = logger;
    }

    public async Task<List<LlmPrompt>> GetPromptsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Prompts
            .AsNoTracking()
            .OrderByDescending(x => x.UpdateTime)
            .ToListAsync();
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

        dbContext.Prompts.Add(prompt);
        await dbContext.SaveChangesAsync();
        return prompt;
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
        existing.UpdateTime = DateTime.UtcNow;
        existing.Version++;

        await dbContext.SaveChangesAsync();
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

        var configService = new ConfigService(_dbContextFactory);
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

        var endpointService = new EndpointService(_dbContextFactory);
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
