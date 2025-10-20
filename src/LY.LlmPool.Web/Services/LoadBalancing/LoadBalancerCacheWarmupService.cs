using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;

namespace LY.LlmPool.Web.Services.LoadBalancing;

/// <summary>
/// LoadBalancer 缓存预热后台服务
/// 在程序启动时自动预热所有模型名称的缓存映射
/// 负责查询数据库并填充缓存
/// </summary>
public class LoadBalancerCacheWarmupService : IHostedService
{
    private readonly LoadBalancerService _loadBalancer;
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory;
    private readonly HybridCache _cache;
    private readonly ILogger<LoadBalancerCacheWarmupService> _logger;
    
    private const string CacheKeyPrefix = "lb:model:";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

    public LoadBalancerCacheWarmupService(
        LoadBalancerService loadBalancer,
        IDbContextFactory<LlmDbContext> dbContextFactory,
        HybridCache cache,
        ILogger<LoadBalancerCacheWarmupService> logger)
    {
        _loadBalancer = loadBalancer;
        _dbContextFactory = dbContextFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🚀 LoadBalancerCacheWarmupService 启动中...");

        try
        {
            // 延迟 2 秒确保数据库连接就绪
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            // 预热缓存
            await WarmupCacheAsync();

            _logger.LogInformation("✅ LoadBalancerCacheWarmupService 启动完成");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("⚠️ LoadBalancerCacheWarmupService 启动被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ LoadBalancerCacheWarmupService 启动失败");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 LoadBalancerCacheWarmupService 停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 预热所有缓存（程序启动时调用）
    /// 🎯 将所有 App、Config、Endpoint 的名称映射到 ConfigSelectionResult 并缓存
    /// </summary>
    public async Task WarmupCacheAsync()
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("🔥 开始预热 LoadBalancer 缓存...");

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // 1. 预热所有启用的 App
        var apps = await dbContext.Apps
            .Include(a => a.LlmConfig)
            .Include(a => a.Endpoint)
                .ThenInclude(e => e!.EndpointConfigs)
                .ThenInclude(ec => ec.LlmConfig)
            .Where(a => a.IsEnabled)
            .ToListAsync();

        foreach (var app in apps)
        {
            try
            {
                var result = await _loadBalancer.SelectConfigForAppAsync(app);
                // 🎯 对于 AgentGroup 类型的 App，Config 为 null 但仍需要缓存
                if (result?.Success == true)
                {
                    var cacheKey = $"{CacheKeyPrefix}{app.Name}";
                    await _cache.SetAsync(cacheKey, result, new HybridCacheEntryOptions { Expiration = CacheExpiration });
                    _logger.LogInformation("✅ 预热 App: {AppName} -> {Strategy} | CacheKey={CacheKey}", 
                        app.Name, result.Strategy, cacheKey);
                }
                else
                {
                    _logger.LogWarning("⚠️ 预热 App {AppName} 失败: Success={Success}", 
                        app.Name, result?.Success ?? false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 预热 App {AppName} 失败", app.Name);
            }
        }

        // 2. 预热所有启用的 Config（直接配置名称访问）
        var configs = await dbContext.Configs
            .Where(c => c.IsEnabled)
            .ToListAsync();

        foreach (var config in configs)
        {
            try
            {
                var result = new ConfigSelectionResult
                {
                    Success = true,
                    Config = config,
                    Strategy = "直接配置名称",
                    NeedsRelease = false,
                    Message = $"直接使用配置: {config.Name}"
                };
                
                await _cache.SetAsync($"{CacheKeyPrefix}{config.Name}", result, new HybridCacheEntryOptions { Expiration = CacheExpiration });
                _logger.LogDebug("✅ 预热 Config: {ConfigName}", config.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 预热 Config {ConfigName} 失败", config.Name);
            }
        }

        // 3. 预热所有启用的 Endpoint（按名称和 ID）
        var endpoints = await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
                .ThenInclude(ec => ec.LlmConfig)
            .Where(e => e.IsEnabled)
            .ToListAsync();

        foreach (var endpoint in endpoints)
        {
            try
            {
                var result = await _loadBalancer.SelectConfigForEndpointAsync(endpoint);
                if (result?.Success == true && result.Config != null)
                {
                    // 缓存名称映射
                    await _cache.SetAsync($"{CacheKeyPrefix}{endpoint.Name}", result, new HybridCacheEntryOptions { Expiration = CacheExpiration });
                    _logger.LogDebug("✅ 预热 Endpoint (Name): {EndpointName} -> {ConfigName}", endpoint.Name, result.Config.Name);
                    
                    // 缓存 ID 映射
                    await _cache.SetAsync($"{CacheKeyPrefix}{endpoint.Id}", result, new HybridCacheEntryOptions { Expiration = CacheExpiration });
                    _logger.LogDebug("✅ 预热 Endpoint (ID): {EndpointId} -> {ConfigName}", endpoint.Id, result.Config.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 预热 Endpoint {EndpointName} 失败", endpoint.Name);
            }
        }

        var elapsed = DateTime.UtcNow - startTime;
        _logger.LogInformation("🔥 LoadBalancer 缓存预热完成: Apps={AppCount}, Configs={ConfigCount}, Endpoints={EndpointCount}, 耗时={Elapsed}ms",
            apps.Count, configs.Count, endpoints.Count, elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 核心解析逻辑:将 modelName 解析为 ConfigSelectionResult
    /// 🎯 此方法用于配置刷新时调用
    /// </summary>
    public async Task<ConfigSelectionResult?> ResolveModelNameAsync(string modelName)
    {
        _logger.LogDebug("🔍 开始解析模型名称（配置刷新）: {ModelName}", modelName);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // 1. 尝试按 App 名称解析
        var app = await dbContext.Apps
            .Include(a => a.LlmConfig)
            .Include(a => a.Endpoint)
                .ThenInclude(e => e!.EndpointConfigs)
                .ThenInclude(ec => ec.LlmConfig)
            .FirstOrDefaultAsync(a => a.Name == modelName && a.IsEnabled);
        
        if (app != null)
        {
            return await _loadBalancer.SelectConfigForAppAsync(app);
        }

        // 2. 尝试按 Config 名称解析
        var config = await dbContext.Configs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == modelName && c.IsEnabled);
        
        if (config != null)
        {
            _logger.LogDebug("找到 Config: {ConfigName}（直接使用，无需负载均衡）", config.Name);
            return new ConfigSelectionResult
            {
                Success = true,
                Config = config,
                Strategy = "直接配置名称",
                NeedsRelease = false,
                Message = $"直接使用配置: {config.Name}"
            };
        }

        // 3. 尝试按 Endpoint 名称解析
        var endpointByName = await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(ec => ec.LlmConfig)
            .FirstOrDefaultAsync(e => e.Name == modelName && e.IsEnabled);
        
        if (endpointByName != null)
        {
            return await _loadBalancer.SelectConfigForEndpointAsync(endpointByName);
        }

        // 4. 尝试按 Endpoint ID 解析
        var endpointById = await dbContext.Endpoints
            .Include(e => e.EndpointConfigs)
            .ThenInclude(ec => ec.LlmConfig)
            .FirstOrDefaultAsync(e => e.Id == modelName && e.IsEnabled);
        
        if (endpointById != null)
        {
            return await _loadBalancer.SelectConfigForEndpointAsync(endpointById);
        }

        // 5. 未找到任何匹配
        _logger.LogDebug("未找到模型: {ModelName}", modelName);
        return null;
    }

    /// <summary>
    /// 刷新单个模型的缓存（配置更新时调用）
    /// </summary>
    public async Task RefreshModelCacheAsync(string modelName)
    {
        _logger.LogDebug("🔄 刷新模型缓存: {ModelName}", modelName);
        
        // 先清除旧缓存
        await _cache.RemoveAsync($"{CacheKeyPrefix}{modelName}");
        
        // 重新解析并缓存
        var result = await ResolveModelNameAsync(modelName);
        if (result?.Success == true)
        {
            await _cache.SetAsync($"{CacheKeyPrefix}{modelName}", result, new HybridCacheEntryOptions { Expiration = CacheExpiration });
            _logger.LogDebug("✅ 已刷新模型缓存: {ModelName} -> {ConfigName}", modelName, result.Config?.Name);
        }
    }

    /// <summary>
    /// 当 App 配置更新时调用（创建、更新、删除）
    /// </summary>
    public async Task OnAppChangedAsync(string appName)
    {
        _logger.LogInformation("📝 App 配置变更: {AppName}", appName);
        await RefreshModelCacheAsync(appName);
    }

    /// <summary>
    /// 当 Config 配置更新时调用（创建、更新、删除）
    /// 需要同时刷新所有引用此 Config 的 App 和 Endpoint
    /// </summary>
    public async Task OnConfigChangedAsync(string configName, string? configId = null)
    {
        _logger.LogInformation("📝 Config 配置变更: {ConfigName}", configName);
        
        // 刷新 Config 自身
        await RefreshModelCacheAsync(configName);
        
        // 刷新所有引用此 Config 的实体
        if (!string.IsNullOrEmpty(configId))
        {
            await RefreshRelatedCachesForConfigAsync(configId);
        }
    }

    /// <summary>
    /// 当 Endpoint 配置更新时调用（创建、更新、删除）
    /// 需要同时刷新所有引用此 Endpoint 的 App
    /// </summary>
    public async Task OnEndpointChangedAsync(string endpointName, string? endpointId = null)
    {
        _logger.LogInformation("📝 Endpoint 配置变更: {EndpointName}", endpointName);
        
        // 刷新 Endpoint 自身（名称 + ID）
        await RefreshModelCacheAsync(endpointName);
        if (!string.IsNullOrEmpty(endpointId))
        {
            await RefreshModelCacheAsync(endpointId);
            await RefreshRelatedCachesForEndpointAsync(endpointId);
        }
    }

    /// <summary>
    /// 刷新与特定 Config 相关的所有缓存
    /// </summary>
    private async Task RefreshRelatedCachesForConfigAsync(string configId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        // 查找所有引用此 Config 的 App
        var relatedApps = await dbContext.Apps
            .Where(a => a.LlmConfigId == configId && a.IsEnabled)
            .Select(a => a.Name)
            .ToListAsync();
        
        // 查找所有引用此 Config 的 Endpoint
        var relatedEndpointNames = await dbContext.EndpointConfigs
            .Where(ec => ec.LlmConfigId == configId)
            .Select(ec => ec.Endpoint!.Name)
            .Distinct()
            .ToListAsync();
        
        var relatedEndpointIds = await dbContext.EndpointConfigs
            .Where(ec => ec.LlmConfigId == configId)
            .Select(ec => ec.EndpointId)
            .Distinct()
            .ToListAsync();
        
        // 刷新所有关联的缓存
        foreach (var appName in relatedApps)
        {
            await RefreshModelCacheAsync(appName);
        }
        
        foreach (var endpointName in relatedEndpointNames)
        {
            await RefreshModelCacheAsync(endpointName);
        }
        
        foreach (var endpointId in relatedEndpointIds)
        {
            await RefreshModelCacheAsync(endpointId);
        }
        
        _logger.LogDebug("🔄 已刷新 Config {ConfigId} 的关联缓存: {AppCount} Apps, {EndpointCount} Endpoints",
            configId, relatedApps.Count, relatedEndpointNames.Count);
    }

    /// <summary>
    /// 刷新与特定 Endpoint 相关的所有缓存
    /// </summary>
    private async Task RefreshRelatedCachesForEndpointAsync(string endpointId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        // 查找所有引用此 Endpoint 的 App
        var relatedApps = await dbContext.Apps
            .Where(a => a.EndpointId == endpointId && a.IsEnabled)
            .Select(a => a.Name)
            .ToListAsync();
        
        foreach (var appName in relatedApps)
        {
            await RefreshModelCacheAsync(appName);
        }
        
        _logger.LogDebug("🔄 已刷新 Endpoint {EndpointId} 的关联缓存: {AppCount} Apps", endpointId, relatedApps.Count);
    }

    /// <summary>
    /// 检查模型名称是否在 App、Config、Endpoint 三者中全局唯一
    /// 🎯 因为 modelName 会用来查询这三种实体,所以必须全局唯一
    /// </summary>
    /// <param name="name">要检查的名称</param>
    /// <param name="entityType">当前实体类型: "App", "Config", "Endpoint"</param>
    /// <param name="excludeId">排除的实体ID（更新时使用）</param>
    /// <returns>如果名称已被其他类型使用，返回错误信息；否则返回 null</returns>
    public async Task<string?> CheckModelNameUniquenessAsync(string name, string entityType, string? excludeId = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        
        // 检查 App 名称冲突
        if (entityType != "App")
        {
            var appExists = await dbContext.Apps.AnyAsync(a => a.Name == name);
            if (appExists)
            {
                return $"名称 '{name}' 已被一个 App 使用，请使用不同的名称。App、Config、Endpoint 的名称必须全局唯一。";
            }
        }
        else
        {
            // 当前是 App，检查是否有其他 App 使用此名称
            var appExists = await dbContext.Apps.AnyAsync(a => a.Name == name && a.Id != excludeId);
            if (appExists)
            {
                return $"App 名称 '{name}' 已被其他 App 使用，请使用不同的名称。";
            }
        }
        
        // 检查 Config 名称冲突
        if (entityType != "Config")
        {
            var configExists = await dbContext.Configs.AnyAsync(c => c.Name == name);
            if (configExists)
            {
                return $"名称 '{name}' 已被一个 Config 使用，请使用不同的名称。App、Config、Endpoint 的名称必须全局唯一。";
            }
        }
        else
        {
            // 当前是 Config，检查是否有其他 Config 使用此名称
            var configExists = await dbContext.Configs.AnyAsync(c => c.Name == name && c.Id != excludeId);
            if (configExists)
            {
                return $"Config 名称 '{name}' 已被其他 Config 使用，请使用不同的名称。";
            }
        }
        
        // 检查 Endpoint 名称冲突
        if (entityType != "Endpoint")
        {
            var endpointExists = await dbContext.Endpoints.AnyAsync(e => e.Name == name);
            if (endpointExists)
            {
                return $"名称 '{name}' 已被一个 Endpoint 使用，请使用不同的名称。App、Config、Endpoint 的名称必须全局唯一。";
            }
        }
        else
        {
            // 当前是 Endpoint，检查是否有其他 Endpoint 使用此名称
            var endpointExists = await dbContext.Endpoints.AnyAsync(e => e.Name == name && e.Id != excludeId);
            if (endpointExists)
            {
                return $"Endpoint 名称 '{name}' 已被其他 Endpoint 使用，请使用不同的名称。";
            }
        }
        
        return null; // 名称唯一
    }
}
