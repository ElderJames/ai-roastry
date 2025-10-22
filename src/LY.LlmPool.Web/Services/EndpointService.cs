using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.LoadBalancing;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Services;

public class EndpointService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly LlmPoolCacheService? _cacheService;
    private readonly LoadBalancerService? _loadBalancer;

    public EndpointService(
        IDbContextFactory<LlmDbContext> dbContextFactory,
        LlmPoolCacheService? cacheWarmup = null,
        LoadBalancerService? loadBalancer = null)
    {
        _dbContextFactory = dbContextFactory;
        _cacheService = cacheWarmup;
        _loadBalancer = loadBalancer;
    }

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

    public async Task<LlmEndpoint> AddEndpointAsync(LlmEndpoint endpoint)
    {
        // 检查名称全局唯一性
        if (_cacheService != null)
        {
            var uniquenessError = await _cacheService.CheckModelNameUniquenessAsync(endpoint.Name, "Endpoint");
            if (uniquenessError != null)
            {
                throw new InvalidOperationException(uniquenessError);
            }
        }
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        endpoint.Id = Guid.NewGuid().ToString("N");
        dbContext.Endpoints.Add(endpoint);
        await dbContext.SaveChangesAsync();
        
        // 🎯 刷新 LoadBalancer 缓存
        if (_cacheService != null)
        {
            await _cacheService.RefreshModelCacheAsync(endpoint.Name);
            await _cacheService.RefreshModelCacheAsync(endpoint.Id);
        }
        
        return endpoint;
    }

    public async Task<LlmEndpoint> UpdateEndpointAsync(LlmEndpoint endpoint)
    {
        // 检查名称全局唯一性（排除自身）
        if (_cacheService != null)
        {
            var uniquenessError = await _cacheService.CheckModelNameUniquenessAsync(endpoint.Name, "Endpoint", endpoint.Id);
            if (uniquenessError != null)
            {
                throw new InvalidOperationException(uniquenessError);
            }
        }
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.Endpoints
            .Include(x => x.EndpointConfigs)
            .FirstOrDefaultAsync(x => x.Id == endpoint.Id);

        if (existing == null)
        {
            throw new KeyNotFoundException($"Endpoint with ID {endpoint.Id} not found.");
        }

        var oldName = existing.Name;
        existing.Name = endpoint.Name;
        existing.Description = endpoint.Description;
        existing.IsEnabled = endpoint.IsEnabled;
        existing.UpdatedAt = DateTime.UtcNow;

        // 更新 EndpointConfigs
        // 1. 删除已不存在的配置
        var configsToRemove = existing.EndpointConfigs
            .Where(ec => !endpoint.EndpointConfigs.Any(newEc =>
                newEc.Id == ec.Id ||
                (newEc.LlmConfigId == ec.LlmConfigId && newEc.Priority == ec.Priority)))
            .ToList();

        foreach (var config in configsToRemove)
        {
            existing.EndpointConfigs.Remove(config);
            dbContext.EndpointConfigs.Remove(config);
        }

        // 2. 更新或添加新的配置
        foreach (var newConfig in endpoint.EndpointConfigs)
        {
            var existingConfig = existing.EndpointConfigs
                .FirstOrDefault(ec => ec.Id == newConfig.Id ||
                    (ec.LlmConfigId == newConfig.LlmConfigId && ec.Priority == newConfig.Priority));

            if (existingConfig != null)
            {
                // 更新现有配置
                existingConfig.LlmConfigId = newConfig.LlmConfigId;
                existingConfig.Priority = newConfig.Priority;
            }
            else
            {
                // 添加新配置
                var config = new LlmEndpointConfig
                {
                    EndpointId = existing.Id,
                    LlmConfigId = newConfig.LlmConfigId,
                    Priority = newConfig.Priority,
                    CreatedAt = DateTime.UtcNow
                };
                existing.EndpointConfigs.Add(config);
            }
        }

        await dbContext.SaveChangesAsync();
        
        // 🎯 刷新 LoadBalancer 缓存（如果名称变更，需要清除旧名称和旧 ID）
        if (_cacheService != null)
        {
            if (oldName != existing.Name && _loadBalancer != null)
            {
                await _loadBalancer.InvalidateModelCacheAsync(oldName);
                // 也需要清除 ID 映射的旧缓存（因为 Endpoint 可以通过 ID 访问）
                await _loadBalancer.InvalidateModelCacheAsync(existing.Id);
            }
            await _cacheService.RefreshModelCacheAsync(existing.Name);
            await _cacheService.RefreshModelCacheAsync(existing.Id);
        }
        
        return existing;
    }

    public async Task DeleteEndpointAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var endpoint = await dbContext.Endpoints.FindAsync(id);
        if (endpoint == null)
        {
            throw new KeyNotFoundException($"Endpoint with ID {id} not found.");
        }

        var endpointName = endpoint.Name;
        dbContext.Endpoints.Remove(endpoint);
        await dbContext.SaveChangesAsync();
        
        // 🎯 清除 LoadBalancer 缓存
        if (_cacheService != null)
        {
            await _cacheService.RefreshModelCacheAsync(endpointName);
            await _cacheService.RefreshModelCacheAsync(id);
        }
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
}

