using System.Collections.Concurrent;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using LY.LlmPool.Web.Services.LoadBalancing;
using Microsoft.EntityFrameworkCore;

namespace LY.LlmPool.Web.Services;

public class ConfigService
{
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly LoadBalancerService? _loadBalancer;
    private readonly LlmPoolCacheService _cacheService; // 🎯 统一缓存管理
    private readonly Dictionary<string, SemaphoreSlim> _configLocks = new();

    public ConfigService(
        IDbContextFactory<LlmDbContext> dbContextFactory,
        LlmPoolCacheService cacheService, // 🎯 注入缓存服务
        LoadBalancerService? loadBalancer = null) // Optional to avoid circular dependency
    {
        _dbContextFactory = dbContextFactory;
        _cacheService = cacheService;
        _loadBalancer = loadBalancer;
    }

    public async Task<List<LlmConfigGroup>> GetConfigGroupsAsync()
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
        // 检查名称全局唯一性
        if (_cacheService != null)
        {
            var uniquenessError = await _cacheService.CheckModelNameUniquenessAsync(config.Name, "Config");
            if (uniquenessError != null)
            {
                throw new InvalidOperationException(uniquenessError);
            }
        }
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        config.Id = Guid.NewGuid().ToString("N");
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;
        dbContext.Configs.Add(config);
        await dbContext.SaveChangesAsync();
        
        // 🎯 刷新 LoadBalancer 缓存
        if (_cacheService != null)
        {
            await _cacheService.RefreshModelCacheAsync(config.Name);
        }
        
        return config;
    }

    public async Task<LlmConfig> UpdateConfigAsync(LlmConfig config)
    {
        // 检查名称全局唯一性（排除自身）
        if (_cacheService != null)
        {
            var uniquenessError = await _cacheService.CheckModelNameUniquenessAsync(config.Name, "Config", config.Id);
            if (uniquenessError != null)
            {
                throw new InvalidOperationException(uniquenessError);
            }
        }
        
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var existing = await dbContext.Configs.FindAsync(config.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Config with ID {config.Id} not found.");
        }

        var oldName = existing.Name;
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
        
        // 🎯 使用统一缓存服务清理缓存（会自动触发 LoadBalancer 缓存预热）
        if (!string.IsNullOrEmpty(config.Id))
        {
            await _cacheService.InvalidateConfigRelatedCachesAsync(config.Id);
        }
        
        // 🎯 如果名称变更，清除旧名称的 LoadBalancer 缓存
        if (oldName != config.Name && _loadBalancer != null)
        {
            await _loadBalancer.InvalidateModelCacheAsync(oldName);
        }
        
        return existing;
    }

    public async Task DeleteConfigAsync(string id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var config = await dbContext.Configs.FindAsync(id);
        if (config == null)
        {
            throw new KeyNotFoundException($"Config with ID {id} not found.");
        }

        var configName = config.Name;
        dbContext.Configs.Remove(config);
        await dbContext.SaveChangesAsync();
        
        // 🎯 清除 LoadBalancer 缓存
        if (_loadBalancer != null)
        {
            await _loadBalancer.InvalidateModelCacheAsync(configName);
        }
        if (_cacheService != null)
        {
            // 清除所有引用此 Config 的实体缓存
            await _cacheService.RefreshModelCacheAsync(configName);
        }
    }

    // Resolve a config directly by its name (enabled only)
    public async Task<LlmConfig?> GetConfigByNameAsync(string name)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Configs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == name && c.IsEnabled);
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
                // Ignore - config was not acquired
            }
        }
    }

    // Public wrapper for acquiring a specific config if available (non-blocking)
    public Task<bool> AcquireConfigIfAvailableAsync(string configId)
        => TryAcquireConfigAsync(configId);

    private async Task<bool> TryAcquireConfigAsync(string configId)
    {
        if (!_configLocks.ContainsKey(configId))
        {
            _configLocks[configId] = new SemaphoreSlim(1, 1);
        }

        return await _configLocks[configId].WaitAsync(TimeSpan.Zero);
    }

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
}



