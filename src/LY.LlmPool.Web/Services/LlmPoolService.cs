using System.Collections.Concurrent;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Services;

public class LlmPoolService
{
    private readonly LlmDbContext _dbContext;
    private readonly ILogger<LlmPoolService> _logger;
    private readonly Dictionary<string, SemaphoreSlim> _configLocks = new();

    public LlmPoolService(LlmDbContext dbContext, ILogger<LlmPoolService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    #region Model Types

    public async Task<List<LlmModelType>> GetModelTypesAsync()
    {
        return await _dbContext.ModelTypes
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<LlmModelType?> GetModelTypeByIdAsync(string id)
    {
        return await _dbContext.ModelTypes.FindAsync(id);
    }

    public async Task<LlmModelType> AddModelTypeAsync(LlmModelType modelType)
    {
        _dbContext.ModelTypes.Add(modelType);
        await _dbContext.SaveChangesAsync();
        return modelType;
    }

    public async Task<LlmModelType> UpdateModelTypeAsync(LlmModelType modelType)
    {
        var existing = await _dbContext.ModelTypes.FindAsync(modelType.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Model type with ID {modelType.Id} not found.");
        }

        existing.Name = modelType.Name;
        existing.Description = modelType.Description;
        existing.Icon = modelType.Icon;
        existing.DefaultEndpoint = modelType.DefaultEndpoint;
        existing.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteModelTypeAsync(string id)
    {
        var modelType = await _dbContext.ModelTypes
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

        _dbContext.ModelTypes.Remove(modelType);
        await _dbContext.SaveChangesAsync();
    }

    #endregion

    #region Configs

    public async Task<List<LlmConfigGroup>> GetLlmGroupsAsync()
    {
        var configs = await _dbContext.Configs
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
        return await _dbContext.Configs
            .Include(x => x.ModelType)
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<LlmConfig?> GetConfigByIdAsync(string id)
    {
        return await _dbContext.Configs
            .Include(x => x.ModelType)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<LlmConfig> AddConfigAsync(LlmConfig config)
    {
        _dbContext.Configs.Add(config);
        await _dbContext.SaveChangesAsync();
        _configLocks[config.Id] = new SemaphoreSlim(1, 1);
        return config;
    }

    public async Task<LlmConfig> UpdateConfigAsync(LlmConfig config)
    {
        var existing = await _dbContext.Configs.FindAsync(config.Id);
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

        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteConfigAsync(string id)
    {
        var config = await _dbContext.Configs.FindAsync(id);
        if (config == null)
        {
            throw new KeyNotFoundException($"Configuration with ID {id} not found.");
        }

        _dbContext.Configs.Remove(config);
        await _dbContext.SaveChangesAsync();

        if (_configLocks.ContainsKey(id))
        {
            _configLocks.Remove(id);
        }
    }

    #endregion

    #region Endpoints

    public async Task<List<LlmEndpoint>> GetEndpointsAsync()
    {
        return await _dbContext.Endpoints
            .Include(x => x.EndpointConfigs)
            .ThenInclude(x => x.LlmConfig)
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<LlmEndpoint?> GetEndpointByPathAsync(string path)
    {
        return await _dbContext.Endpoints
            .Include(x => x.EndpointConfigs)
            .ThenInclude(x => x.LlmConfig)
            .FirstOrDefaultAsync(x => x.Path == path && x.IsEnabled);
    }

    public async Task<LlmEndpoint> AddEndpointAsync(LlmEndpoint endpoint)
    {
        _dbContext.Endpoints.Add(endpoint);
        await _dbContext.SaveChangesAsync();
        return endpoint;
    }

    public async Task<LlmEndpoint> UpdateEndpointAsync(LlmEndpoint endpoint)
    {
        var existing = await _dbContext.Endpoints
            .Include(x => x.EndpointConfigs)
            .FirstOrDefaultAsync(x => x.Id == endpoint.Id);

        if (existing == null)
        {
            throw new KeyNotFoundException($"Endpoint with ID {endpoint.Id} not found.");
        }

        existing.Name = endpoint.Name;
        existing.Description = endpoint.Description;
        existing.Path = endpoint.Path;
        existing.IsEnabled = endpoint.IsEnabled;
        existing.UpdatedAt = DateTime.UtcNow;

        // Update configs
        _dbContext.EndpointConfigs.RemoveRange(existing.EndpointConfigs);
        _dbContext.EndpointConfigs.AddRange(endpoint.EndpointConfigs);

        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteEndpointAsync(string id)
    {
        var endpoint = await _dbContext.Endpoints.FindAsync(id);
        if (endpoint == null)
        {
            throw new KeyNotFoundException($"Endpoint with ID {id} not found.");
        }

        _dbContext.Endpoints.Remove(endpoint);
        await _dbContext.SaveChangesAsync();
    }

    #endregion

    #region Load Balancing

    public async Task<LlmConfig?> GetAvailableConfigAsync(string path)
    {
        var endpoint = await _dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(c => c.LlmConfig)
            .FirstOrDefaultAsync(e => e.Path == path && e.IsEnabled);

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
            if (await TryAcquireConfigAsync(config!.Id))
            {
                return config;
            }
        }

        return null;
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

    #endregion
} 