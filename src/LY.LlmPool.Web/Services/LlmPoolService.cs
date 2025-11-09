using System.Collections.Concurrent;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services.LoadBalancing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using HandlebarsDotNet;

namespace LY.LlmPool.Web.Services;

public class LlmPoolService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly ILogger<LlmPoolService> _logger;
    private readonly Dictionary<string, SemaphoreSlim> _configLocks = new();
    private readonly IChatClientService _chatClientService;
    private readonly McpServerConfigService? _mcpService;
    private readonly LlmPoolCacheService? _cacheService;
    private readonly LoadBalancerService? _loadBalancer;

    public LlmPoolService(
        IDbContextFactory<LlmDbContext> dbContextFactory,
        ILogger<LlmPoolService> logger,
        IChatClientService chatClientService,
        McpServerConfigService? mcpService = null,
        LlmPoolCacheService? cacheWarmup = null,
        LoadBalancerService? loadBalancer = null) // Optional to avoid circular dependency
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _chatClientService = chatClientService;
        _mcpService = mcpService;
        _cacheService = cacheWarmup;
        _loadBalancer = loadBalancer;
    }

    #region Model Types

    public async Task<List<LlmModelType>> GetModelTypesAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.ModelTypes
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<LlmModelType?> GetModelTypeByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.ModelTypes.FindAsync(id);
    }

    public async Task<LlmModelType> AddModelTypeAsync(LlmModelType modelType)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        dbContext.ModelTypes.Add(modelType);
        await dbContext.SaveChangesAsync();
        return modelType;
    }

    public async Task<LlmModelType> UpdateModelTypeAsync(LlmModelType modelType)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.ModelTypes.FindAsync(modelType.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Model type with ID {modelType.Id} not found.");
        }

        existing.Name = modelType.Name;
        existing.Description = modelType.Description;
        existing.Icon = modelType.Icon;
        existing.DefaultEndpoint = modelType.DefaultEndpoint;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteModelTypeAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var modelType = await dbContext.ModelTypes
            .Include(x => x.Configs)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (modelType == null)
        {
            throw new KeyNotFoundException($"Model type with ID {id} not found.");
        }

        if (modelType.Configs.Any())
        {
            throw new InvalidOperationException("Cannot delete model type that has configurations.");
        }

        dbContext.ModelTypes.Remove(modelType);
        await dbContext.SaveChangesAsync();
    }

    #endregion

    #region MCP Tools Discovery

    /// <summary>
    /// Return available MCP tools grouped by server. Each entry contains server id, server name and list of (toolId, name).
    /// </summary>
    public async Task<Dictionary<string, List<(string toolId, string name)>>> GetAvailableMcpToolsAsync()
    {
        var result = new Dictionary<string, List<(string toolId, string name)>>();
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var servers = await dbContext.McpServerConfigs.Where(s => s.IsEnabled).ToListAsync();
        if (_mcpService == null)
        {
            // Mcp service not available (e.g. in some unit tests) - return empty entries for servers
            foreach (var srv in servers)
            {
                result[srv.Id] = new List<(string, string)>();
            }
            return result;
        }

        foreach (var srv in servers)
        {
            try
            {
                var tools = await _mcpService.GetToolsAsync(srv.Id);
                result[srv.Id] = tools.Select(t => (t.id, t.name)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get tools for MCP server {ServerId}", srv.Id);
                result[srv.Id] = new List<(string, string)>();
            }
        }
        return result;
    }

    #endregion

    #region Configs

    public async Task<List<LlmConfigGroup>> GetLlmGroupsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var configs = await dbContext.Configs
            .Include(x => x.ModelType)
            .AsNoTracking()
            .ToListAsync();

        return configs
            .GroupBy(x => x.ModelTypeId)
            .Select(g => new LlmConfigGroup
            {
                ModelTypeId = g.Key,
                Configs = g.ToList()
            })
            .ToList();
    }

    public async Task<List<LlmConfig>> GetConfigsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Configs
            .Include(x => x.ModelType)
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<LlmConfig?> GetConfigByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Configs
            .Include(x => x.ModelType)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<LlmConfig> AddConfigAsync(LlmConfig config)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        config.Id = Guid.NewGuid().ToString("N");
        dbContext.Configs.Add(config);
        await dbContext.SaveChangesAsync();
        _configLocks[config.Id] = new SemaphoreSlim(1, 1);
        return config;
    }

    public async Task<LlmConfig> UpdateConfigAsync(LlmConfig config)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.Configs.FindAsync(config.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Configuration with ID {config.Id} not found.");
        }

        existing.Name = config.Name;
        existing.Description = config.Description;
        existing.ModelTypeId = config.ModelTypeId;
        existing.BaseUrl = config.BaseUrl;
        existing.ApiKey = config.ApiKey;
        existing.Model = config.Model;
        existing.IsEnabled = config.IsEnabled;
        existing.AdditionalHeaders = config.AdditionalHeaders;
        existing.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteConfigAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var config = await dbContext.Configs.FindAsync(id);
        if (config == null)
        {
            throw new KeyNotFoundException($"Configuration with ID {id} not found.");
        }

        dbContext.Configs.Remove(config);
        await dbContext.SaveChangesAsync();

        if (_configLocks.ContainsKey(id))
        {
            _configLocks.Remove(id);
        }
    }

    #endregion

    #region Endpoints (Read-Only - Use EndpointService for CRUD)

    public async Task<List<LlmEndpoint>> GetEndpointsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Endpoints
            .Include(x => x.EndpointConfigs)
            .ThenInclude(x => x.LlmConfig)
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<LlmEndpoint?> GetEndpointByIdAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Endpoints
            .Include(x => x.EndpointConfigs)
            .ThenInclude(x => x.LlmConfig)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    #endregion

    #region Endpoint Call Records

    public async Task<EndpointCallRecord> CreateCallRecordAsync(string endpointId, object? requestData = null, string? parentCallId = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var record = new EndpointCallRecord
        {
            EndpointId = endpointId,
            RequestReceivedAt = DateTime.UtcNow,
            ParentCallId = parentCallId,
            RequestData = requestData
        };
        
        dbContext.EndpointCallRecords.Add(record);
        await dbContext.SaveChangesAsync();
        return record;
    }

    public async Task<EndpointCallRecord> UpdateCallRecordAsync(EndpointCallRecord record)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.EndpointCallRecords.FindAsync(record.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Call record with ID {record.Id} not found.");
        }
        
        dbContext.Entry(existing).CurrentValues.SetValues(record);
        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task<EndpointCallRecord?> GetCallRecordAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.EndpointCallRecords
            .Include(x => x.Endpoint)
            .Include(x => x.LlmConfig)
            .Include(x => x.ParentCall)
            .Include(x => x.ChildCalls)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<List<EndpointCallRecord>> GetCallRecordsAsync(
        string? endpointId = null,
        string? configId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int? skip = null,
        int? take = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var query = dbContext.EndpointCallRecords
            .Include(x => x.Endpoint)
            .Include(x => x.LlmConfig)
            .AsNoTracking();

        if (!string.IsNullOrEmpty(endpointId))
        {
            query = query.Where(x => x.EndpointId == endpointId);
        }

        if (!string.IsNullOrEmpty(configId))
        {
            query = query.Where(x => x.LlmConfigId == configId);
        }

        if (startDate.HasValue)
        {
            query = query.Where(x => x.RequestReceivedAt >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(x => x.RequestReceivedAt < endDate.Value);
        }

        query = query.OrderByDescending(x => x.RequestReceivedAt);

        if (skip.HasValue)
        {
            query = query.Skip(skip.Value);
        }

        if (take.HasValue)
        {
            query = query.Take(take.Value);
        }

        return await query.ToListAsync();
    }

    public async Task<List<EndpointCallRecord>> GetCallChainAsync(string rootCallId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var rootCall = await dbContext.EndpointCallRecords
            .Include(x => x.Endpoint)
            .Include(x => x.LlmConfig)
            .FirstOrDefaultAsync(x => x.Id == rootCallId);
        
        if (rootCall == null)
        {
            return new List<EndpointCallRecord>();
        }
        
        var calls = new List<EndpointCallRecord> { rootCall };
        await LoadChildCalls(rootCallId, calls);
        
        return calls.OrderBy(x => x.RequestReceivedAt).ToList();
    }

    private async Task LoadChildCalls(string parentId, List<EndpointCallRecord> calls)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var childCalls = await dbContext.EndpointCallRecords
            .Include(x => x.Endpoint)
            .Include(x => x.LlmConfig)
            .Where(x => x.ParentCallId == parentId)
            .ToListAsync();
        
        if (childCalls.Any())
        {
            calls.AddRange(childCalls);
            
            foreach (var child in childCalls)
            {
                await LoadChildCalls(child.Id, calls);
            }
        }
    }

    #endregion

    #region Load Balancing

    public async Task<LlmConfig?> GetAvailableConfigByKeyAsync(string key)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var endpoint = await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(c => c.LlmConfig)
            .FirstOrDefaultAsync(e => e.Id == key && e.IsEnabled);

        if (endpoint == null)
        {
            return null;
        }

        var configs = endpoint.EndpointConfigs
            .Where(c => c.LlmConfig != null && c.LlmConfig.IsEnabled)
            .OrderBy(c => c.Priority)
            .Select(c => c.LlmConfig)
            .ToList();

        foreach (var config in configs)
        {
            if (config?.Id != null && await TryAcquireConfigAsync(config.Id))
            {
                return config;
            }
        }

        return null;
    }

    // Resolve a config directly by its name (enabled only)
    public async Task<LlmConfig?> GetConfigByNameAsync(string name)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Configs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == name && c.IsEnabled);
    }

    // Resolve an endpoint by its name (enabled only)
    public async Task<LlmEndpoint?> GetEndpointByNameAsync(string name)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(ec => ec.LlmConfig)
            .FirstOrDefaultAsync(e => e.Name == name && e.IsEnabled);
    }

    public void ReleaseConfig(string configId)
    {
        if (_configLocks.TryGetValue(configId, out var semaphore))
        {
            try
            {
                semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                _logger.LogWarning("Attempted to release a config that was not acquired: {ConfigId}", configId);
            }
        }
    }

    private async Task<bool> TryAcquireConfigAsync(string configId)
    {
        if (!_configLocks.ContainsKey(configId))
        {
            _configLocks[configId] = new SemaphoreSlim(1, 1);
        }

        return await _configLocks[configId].WaitAsync(TimeSpan.Zero);
    }

    // Public wrapper for acquiring a specific config if available (non-blocking)
    public Task<bool> AcquireConfigIfAvailableAsync(string configId)
        => TryAcquireConfigAsync(configId);

    // Find an enabled endpoint that contains the given config, prefer lower priority mapping
    public async Task<LlmEndpoint?> GetEndpointForConfigAsync(string configId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var query = dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(ec => ec.LlmConfig)
            .Where(e => e.IsEnabled && e.EndpointConfigs.Any(ec => ec.LlmConfigId == configId));

        var endpoints = await query.ToListAsync();
        if (!endpoints.Any()) return null;

        // Choose the endpoint where this config has the lowest priority
        var best = endpoints
            .Select(e => new
            {
                Endpoint = e,
                Priority = e.EndpointConfigs.First(ec => ec.LlmConfigId == configId).Priority
            })
            .OrderBy(x => x.Priority)
            .First().Endpoint;
        return best;
    }

    #endregion

    #region Tools (now bound to Prompts via PromptTool)

    // Tools are now bound to Prompts, not AgentMembers
    // See ToolProviderService for creating tool objects from PromptTools

    #endregion

    #region MCP Server Configs

    public async Task<List<McpServerConfig>> GetMcpServerConfigsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.McpServerConfigs
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<McpServerConfig> AddMcpServerConfigAsync(McpServerConfig config)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        config.Id = Guid.NewGuid().ToString("N");
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
            throw new KeyNotFoundException($"McpServerConfig with ID {config.Id} not found.");
        }

        existing.Name = config.Name;
        existing.Url = config.Url;
        existing.Description = config.Description;
        existing.SchemaCacheJson = config.SchemaCacheJson;

        await dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task RemoveMcpServerConfigAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.McpServerConfigs.FindAsync(id);
        if (existing != null)
        {
            dbContext.McpServerConfigs.Remove(existing);
            await dbContext.SaveChangesAsync();
        }
    }

    #endregion
}



