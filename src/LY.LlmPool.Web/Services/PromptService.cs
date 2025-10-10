using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HandlebarsDotNet;

namespace LY.LlmPool.Web.Services;

public class PromptService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly IChatClientService _chatClientService;

    public PromptService(IDbContextFactory<LlmDbContext> dbContextFactory, IChatClientService chatClientService)
    {
        _dbContextFactory = dbContextFactory;
        _chatClientService = chatClientService;
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
        return await dbContext.Prompts.FindAsync(id);
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

        // Parse parameters
        if (!string.IsNullOrEmpty(parameters))
        {
            var paramDict = parameters.Split(',')
                .Select(p => p.Split('='))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

            if (paramDict.TryGetValue("temperature", out var tempStr) && float.TryParse(tempStr, out var temp))
            {
                config.Temperature = temp;
            }
            if (paramDict.TryGetValue("max_tokens", out var maxTokensStr) && int.TryParse(maxTokensStr, out var maxTokens))
            {
                config.MaxTokens = maxTokens;
            }
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
            new ChatMessage { Role = "system", Content = promptContent }
        };

        if (!string.IsNullOrEmpty(userMessage))
        {
            messages.Add(new ChatMessage { Role = "user", Content = userMessage });
        }

        var result = await _chatClientService.SendMessageAsync(config, messages);
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

        // Parse parameters
        if (!string.IsNullOrEmpty(parameters))
        {
            var paramDict = parameters.Split(',')
                .Select(p => p.Split('='))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

            if (paramDict.TryGetValue("temperature", out var tempStr) && float.TryParse(tempStr, out var temp))
            {
                config.Temperature = temp;
            }
            if (paramDict.TryGetValue("max_tokens", out var maxTokensStr) && int.TryParse(maxTokensStr, out var maxTokens))
            {
                config.MaxTokens = maxTokens;
            }
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
            new ChatMessage { Role = "system", Content = promptContent }
        };

        if (!string.IsNullOrEmpty(userMessage))
        {
            messages.Add(new ChatMessage { Role = "user", Content = userMessage });
        }

        var result = await _chatClientService.SendMessageAsync(config, messages);
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
}