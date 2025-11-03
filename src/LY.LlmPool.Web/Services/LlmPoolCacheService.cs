using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.EntityFrameworkCore;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using LY.LlmPool.Web.Services.LoadBalancing;

namespace LY.LlmPool.Web.Services;

/// <summary>
/// LlmPool 缓存管理服务
/// 统一管理 App、Endpoint、LlmConfig 的缓存
/// 🎯 负责缓存预热和模型名称解析
/// </summary>
public class LlmPoolCacheService
{
    private readonly HybridCache _cache;
    private readonly ILogger<LlmPoolCacheService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<LlmDbContext> _dbContextFactory; // 🎯 用于数据库查询

    // 缓存键前缀
    private const string AppPrefix = "LlmPool:App:";
    private const string EndpointPrefix = "LlmPool:Endpoint:";
    private const string ConfigPrefix = "LlmPool:Config:";
    private const string AppListPrefix = "LlmPool:AppList:";
    private const string EndpointListPrefix = "LlmPool:EndpointList:";
    private const string ConfigListPrefix = "LlmPool:ConfigList:";
    
    // LoadBalancer 缓存键前缀
    private const string LoadBalancerCacheKeyPrefix = "lb:model:";
    private static readonly TimeSpan LoadBalancerCacheExpiration = TimeSpan.FromMinutes(5);

    // 缓存过期时间
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LocalCacheExpiration = TimeSpan.FromMinutes(5);

    public LlmPoolCacheService(
        HybridCache cache, 
        ILogger<LlmPoolCacheService> logger,
        IServiceProvider serviceProvider,
        IDbContextFactory<LlmDbContext> dbContextFactory) // 🎯 注入 DbContextFactory
    {
        _cache = cache;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _dbContextFactory = dbContextFactory;
    }

    #region App 缓存

    /// <summary>
    /// 获取或创建 App 缓存
    /// </summary>
    public async Task<LlmApp?> GetOrCreateAppAsync(string appId, Func<CancellationToken, ValueTask<LlmApp?>> factory, CancellationToken cancellationToken = default)
    {
        var key = $"{AppPrefix}{appId}";
        try
        {
            return await _cache.GetOrCreateAsync(
                key,
                factory,
                new HybridCacheEntryOptions
                {
                    Expiration = DefaultExpiration,
                    LocalCacheExpiration = LocalCacheExpiration
                },
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取或创建 App 缓存失败: {AppId}", appId);
            return null;
        }
    }

    /// <summary>
    /// 设置 App 缓存
    /// </summary>
    public async Task SetAppAsync(string appId, LlmApp app, CancellationToken cancellationToken = default)
    {
        var key = $"{AppPrefix}{appId}";
        try
        {
            await _cache.SetAsync(
                key,
                app,
                new HybridCacheEntryOptions
                {
                    Expiration = DefaultExpiration,
                    LocalCacheExpiration = LocalCacheExpiration
                },
                cancellationToken: cancellationToken);
            _logger.LogDebug("✅ 设置 App 缓存: {AppId}", appId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置 App 缓存失败: {AppId}", appId);
        }
    }

    /// <summary>
    /// 移除 App 缓存
    /// </summary>
    public async Task RemoveAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        var key = $"{AppPrefix}{appId}";
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _logger.LogInformation("🗑️ 移除 App 缓存: {AppId}", appId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除 App 缓存失败: {AppId}", appId);
        }
    }

    /// <summary>
    /// 移除所有 App 缓存（通过移除列表缓存触发重新加载）
    /// </summary>
    public async Task RemoveAllAppsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync($"{AppListPrefix}*", cancellationToken);
            _logger.LogInformation("🗑️ 移除所有 App 列表缓存");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除所有 App 缓存失败");
        }
    }

    #endregion

    #region Endpoint 缓存

    /// <summary>
    /// 获取或创建 Endpoint 缓存
    /// </summary>
    public async Task<LlmEndpoint?> GetOrCreateEndpointAsync(string endpointId, Func<CancellationToken, ValueTask<LlmEndpoint?>> factory, CancellationToken cancellationToken = default)
    {
        var key = $"{EndpointPrefix}{endpointId}";
        try
        {
            return await _cache.GetOrCreateAsync(
                key,
                factory,
                new HybridCacheEntryOptions
                {
                    Expiration = DefaultExpiration,
                    LocalCacheExpiration = LocalCacheExpiration
                },
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取或创建 Endpoint 缓存失败: {EndpointId}", endpointId);
            return null;
        }
    }

    /// <summary>
    /// 设置 Endpoint 缓存
    /// </summary>
    public async Task SetEndpointAsync(string endpointId, LlmEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var key = $"{EndpointPrefix}{endpointId}";
        try
        {
            await _cache.SetAsync(
                key,
                endpoint,
                new HybridCacheEntryOptions
                {
                    Expiration = DefaultExpiration,
                    LocalCacheExpiration = LocalCacheExpiration
                },
                cancellationToken: cancellationToken);
            _logger.LogDebug("✅ 设置 Endpoint 缓存: {EndpointId}", endpointId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置 Endpoint 缓存失败: {EndpointId}", endpointId);
        }
    }

    /// <summary>
    /// 移除 Endpoint 缓存
    /// </summary>
    public async Task RemoveEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        var key = $"{EndpointPrefix}{endpointId}";
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _logger.LogInformation("🗑️ 移除 Endpoint 缓存: {EndpointId}", endpointId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除 Endpoint 缓存失败: {EndpointId}", endpointId);
        }
    }

    /// <summary>
    /// 移除所有 Endpoint 缓存
    /// </summary>
    public async Task RemoveAllEndpointsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync($"{EndpointListPrefix}*", cancellationToken);
            _logger.LogInformation("🗑️ 移除所有 Endpoint 列表缓存");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除所有 Endpoint 缓存失败");
        }
    }

    #endregion

    #region LlmConfig 缓存

    /// <summary>
    /// 获取或创建 LlmConfig 缓存
    /// </summary>
    public async Task<LlmConfig?> GetOrCreateConfigAsync(string configId, Func<CancellationToken, ValueTask<LlmConfig?>> factory, CancellationToken cancellationToken = default)
    {
        var key = $"{ConfigPrefix}{configId}";
        try
        {
            return await _cache.GetOrCreateAsync(
                key,
                factory,
                new HybridCacheEntryOptions
                {
                    Expiration = DefaultExpiration,
                    LocalCacheExpiration = LocalCacheExpiration
                },
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取或创建 LlmConfig 缓存失败: {ConfigId}", configId);
            return null;
        }
    }

    /// <summary>
    /// 设置 LlmConfig 缓存
    /// </summary>
    public async Task SetConfigAsync(string configId, LlmConfig config, CancellationToken cancellationToken = default)
    {
        var key = $"{ConfigPrefix}{configId}";
        try
        {
            await _cache.SetAsync(
                key,
                config,
                new HybridCacheEntryOptions
                {
                    Expiration = DefaultExpiration,
                    LocalCacheExpiration = LocalCacheExpiration
                },
                cancellationToken: cancellationToken);
            _logger.LogDebug("✅ 设置 LlmConfig 缓存: {ConfigId}", configId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置 LlmConfig 缓存失败: {ConfigId}", configId);
        }
    }

    /// <summary>
    /// 移除 LlmConfig 缓存
    /// </summary>
    public async Task RemoveConfigAsync(string configId, CancellationToken cancellationToken = default)
    {
        var key = $"{ConfigPrefix}{configId}";
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _logger.LogInformation("🗑️ 移除 LlmConfig 缓存: {ConfigId}", configId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除 LlmConfig 缓存失败: {ConfigId}", configId);
        }
    }

    /// <summary>
    /// 移除所有 LlmConfig 缓存
    /// </summary>
    public async Task RemoveAllConfigsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync($"{ConfigListPrefix}*", cancellationToken);
            _logger.LogInformation("🗑️ 移除所有 LlmConfig 列表缓存");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除所有 LlmConfig 缓存失败");
        }
    }

    #endregion

    #region 批量操作

    /// <summary>
    /// 移除与指定 App 关联的所有缓存（包括关联的 Config 和 Endpoint）
    /// </summary>
    public async Task InvalidateAppRelatedCachesAsync(string appId, CancellationToken cancellationToken = default)
    {
        // 1. 获取 App 的模型名称，清除对应的负载均衡缓存
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var app = await dbContext.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken);
        if (app != null)
        {
            await InvalidateModelCacheAsync(app.Name, cancellationToken);
            _logger.LogInformation("🔄 已清除 App {AppId} 的模型缓存: lb:model:{ModelName}", appId, app.Name);
        }
        
        // 2. 清除 App 相关缓存
        await RemoveAppAsync(appId, cancellationToken);
        await RemoveAllAppsAsync(cancellationToken);
        _logger.LogInformation("🔄 已失效 App {AppId} 相关的所有缓存", appId);
    }

    /// <summary>
    /// 移除与指定 Config 关联的所有缓存（包括使用该 Config 的 App）
    /// 🎯 会同步更新所有引用该 Config 的 Endpoint 和 App
    /// </summary>
    public async Task InvalidateConfigRelatedCachesAsync(string configId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🔄 开始失效 Config {ConfigId} 相关的所有缓存...", configId);
        
        // 1. 获取所有使用该 Config 的 App 和 Config 本身的名称，清除模型缓存
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // 清除 Config 自身的模型缓存（Config 可以作为模型名称直接使用）
            var config = await dbContext.Configs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == configId, cancellationToken);
            if (config != null)
            {
                await InvalidateModelCacheAsync(config.Name, cancellationToken);
                _logger.LogInformation("🔄 已清除 Config {ConfigId} 的模型缓存: lb:model:{ModelName}", configId, config.Name);
            }
            
            // 清除所有使用该 Config 的 App 的模型缓存
            var appsUsingConfig = await dbContext.Apps.AsNoTracking()
                .Where(a => a.LlmConfigId == configId)
                .ToListAsync(cancellationToken);
            foreach (var app in appsUsingConfig)
            {
                await InvalidateModelCacheAsync(app.Name, cancellationToken);
                _logger.LogInformation("🔄 已清除使用 Config {ConfigId} 的 App {AppId} 的模型缓存: lb:model:{ModelName}", 
                    configId, app.Id, app.Name);
            }
        }
        
        // 2. 清除 Config 自身的缓存
        await RemoveConfigAsync(configId, cancellationToken);
        
        // 3. 清除所有列表缓存
        await RemoveAllConfigsAsync(cancellationToken);
        await RemoveAllAppsAsync(cancellationToken);
        await RemoveAllEndpointsAsync(cancellationToken); // 🎯 Endpoint 也可能引用了该 Config
        
        _logger.LogInformation("✅ 已失效 Config {ConfigId} 相关的所有缓存（包括关联的 App 和 Endpoint）", configId);
    }

    /// <summary>
    /// 移除与指定 Endpoint 关联的所有缓存
    /// </summary>
    public async Task InvalidateEndpointRelatedCachesAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        // 1. 获取 Endpoint 和所有使用该 Endpoint 的 App，清除模型缓存
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // 清除 Endpoint 自身的模型缓存（Endpoint 可以作为模型名称直接使用）
            var endpoint = await dbContext.Endpoints.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
            if (endpoint != null)
            {
                await InvalidateModelCacheAsync(endpoint.Name, cancellationToken);
                _logger.LogInformation("🔄 已清除 Endpoint {EndpointId} 的模型缓存: lb:model:{ModelName}", endpointId, endpoint.Name);
            }
            
            // 清除所有使用该 Endpoint 的 App 的模型缓存
            var appsUsingEndpoint = await dbContext.Apps.AsNoTracking()
                .Where(a => a.EndpointId == endpointId)
                .ToListAsync(cancellationToken);
            foreach (var app in appsUsingEndpoint)
            {
                await InvalidateModelCacheAsync(app.Name, cancellationToken);
                _logger.LogInformation("🔄 已清除使用 Endpoint {EndpointId} 的 App {AppId} 的模型缓存: lb:model:{ModelName}", 
                    endpointId, app.Id, app.Name);
            }
        }
        
        // 2. 清除 Endpoint 相关缓存
        await RemoveEndpointAsync(endpointId, cancellationToken);
        await RemoveAllEndpointsAsync(cancellationToken);
        await RemoveAllConfigsAsync(cancellationToken); // Config 可能引用了该 Endpoint
        _logger.LogInformation("🔄 已失效 Endpoint {EndpointId} 相关的所有缓存", endpointId);
    }

    /// <summary>
    /// 清空所有 LlmPool 相关缓存
    /// </summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await RemoveAllAppsAsync(cancellationToken);
        await RemoveAllEndpointsAsync(cancellationToken);
        await RemoveAllConfigsAsync(cancellationToken);
        _logger.LogWarning("🗑️ 已清空所有 LlmPool 缓存");
    }

    #endregion

    #region LoadBalancer 缓存预热和模型名称解析

    /// <summary>
    /// 预热所有 LoadBalancer 缓存（程序启动时调用）
    /// 🎯 将所有 App、Config、Endpoint 的名称映射到 ConfigSelectionResult 并缓存
    /// 🎯 直接解析配置，不调用 LoadBalancer 避免循环依赖
    /// </summary>
    public async Task WarmupCacheAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("🔥 开始预热 LoadBalancer 缓存...");

        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            // 1. 预热所有启用的 App
            var apps = await dbContext.Apps
                .Include(a => a.LlmConfig)
                .Include(a => a.Endpoint)
                    .ThenInclude(e => e!.EndpointConfigs)
                    .ThenInclude(ec => ec.LlmConfig)
                .Where(a => a.IsEnabled)
                .ToListAsync(cancellationToken);

            foreach (var app in apps)
            {
                try
                {
                    // 直接解析 App 的配置（优先使用直接指定的 Config，否则用 Endpoint）
                    ConfigSelectionResult? result = null;
                    
                    if (!string.IsNullOrEmpty(app.LlmConfigId) && app.LlmConfig != null && app.LlmConfig.IsEnabled)
                    {
                        result = new ConfigSelectionResult
                        {
                            Success = true,
                            Config = app.LlmConfig,
                            App = app, // 🎯 设置 App 信息
                            Strategy = "App 直接指定配置",
                            NeedsRelease = false,
                            Message = $"使用 App '{app.Name}' 直接指定的配置: {app.LlmConfig.Name}"
                        };
                    }
                    else if (!string.IsNullOrEmpty(app.EndpointId) && app.Endpoint != null && app.Endpoint.IsEnabled)
                    {
                        // 🎯 获取 Endpoint 的所有可用配置
                        var availableConfigsList = app.Endpoint.EndpointConfigs
                            .Where(ec => ec.LlmConfig != null && ec.LlmConfig.IsEnabled)
                            .OrderBy(ec => ec.Priority)
                            .Select(ec => ec.LlmConfig!)
                            .ToList();
                        
                        if (availableConfigsList.Any())
                        {
                            result = new ConfigSelectionResult
                            {
                                Success = true,
                                Config = availableConfigsList[0], // 默认第一个（最高优先级）
                                App = app, // 🎯 设置 App 信息
                                AvailableConfigs = availableConfigsList, // 🎯 保存所有配置
                                Strategy = availableConfigsList.Count == 1 ? "App 的 Endpoint (单一配置)" : $"App 的 Endpoint ({availableConfigsList.Count} 个配置)",
                                NeedsRelease = false,
                                Message = $"App '{app.Name}' 的 Endpoint '{app.Endpoint.Name}' 有 {availableConfigsList.Count} 个可用配置"
                            };
                            
                            _logger.LogDebug("App '{AppName}' 的 Endpoint '{EndpointName}' -> {Count} 个配置",
                                app.Name, app.Endpoint.Name, availableConfigsList.Count);
                        }
                    }
                    
                    if (result != null)
                    {
                        var cacheKey = $"{LoadBalancerCacheKeyPrefix}{app.Name}";
                        await _cache.SetAsync(cacheKey, result, new HybridCacheEntryOptions { Expiration = LoadBalancerCacheExpiration }, cancellationToken: cancellationToken);
                        _logger.LogInformation("✅ 预热 App: {AppName} -> {Strategy}", app.Name, result.Strategy);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ 预热 App {AppName} 失败: 没有可用的配置", app.Name);
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
                .ToListAsync(cancellationToken);

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
                    
                    await _cache.SetAsync($"{LoadBalancerCacheKeyPrefix}{config.Name}", result, new HybridCacheEntryOptions { Expiration = LoadBalancerCacheExpiration }, cancellationToken: cancellationToken);
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
                .ToListAsync(cancellationToken);

            foreach (var endpoint in endpoints)
            {
                try
                {
                    // 🎯 获取所有可用配置（不只是第一个）
                    var availableConfigs = endpoint.EndpointConfigs
                        .Where(ec => ec.LlmConfig != null && ec.LlmConfig.IsEnabled)
                        .OrderBy(ec => ec.Priority)
                        .Select(ec => ec.LlmConfig!)
                        .ToList();
                    
                    if (availableConfigs.Any())
                    {
                        // 🎯 缓存时保存所有可用配置,使用时再进行负载均衡选择
                        var result = new ConfigSelectionResult
                        {
                            Success = true,
                            Config = availableConfigs[0], // 默认第一个（最高优先级）
                            AvailableConfigs = availableConfigs, // 🎯 保存所有配置
                            Strategy = availableConfigs.Count == 1 ? "Endpoint (单一配置)" : $"Endpoint ({availableConfigs.Count} 个配置)",
                            NeedsRelease = false,
                            Message = $"Endpoint '{endpoint.Name}' 有 {availableConfigs.Count} 个可用配置"
                        };
                        
                        // 缓存名称映射
                        await _cache.SetAsync($"{LoadBalancerCacheKeyPrefix}{endpoint.Name}", result, new HybridCacheEntryOptions { Expiration = LoadBalancerCacheExpiration }, cancellationToken: cancellationToken);
                        _logger.LogDebug("✅ 预热 Endpoint (Name): {EndpointName} -> {Count} 个配置", endpoint.Name, availableConfigs.Count);
                        
                        // 缓存 ID 映射
                        await _cache.SetAsync($"{LoadBalancerCacheKeyPrefix}{endpoint.Id}", result, new HybridCacheEntryOptions { Expiration = LoadBalancerCacheExpiration }, cancellationToken: cancellationToken);
                        _logger.LogDebug("✅ 预热 Endpoint (ID): {EndpointId} -> {Count} 个配置", endpoint.Id, availableConfigs.Count);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ LoadBalancer 缓存预热失败");
        }
    }

    /// <summary>
    /// 核心解析逻辑:将 modelName 解析为 ConfigSelectionResult
    /// 🎯 用于缓存未命中时的降级查询（直接返回配置，不调用 LoadBalancer 避免循环）
    /// </summary>
    public async Task<ConfigSelectionResult?> ResolveModelNameAsync(string modelName, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("🔍 开始解析模型名称: {ModelName}", modelName);

        // 🎯 使用 HybridCache 缓存解析结果
        var cacheKey = $"{LoadBalancerCacheKeyPrefix}{modelName}";
        return await _cache.GetOrCreateAsync(
            cacheKey,
            async cancel =>
            {
                _logger.LogDebug("💾 从数据库加载模型配置: {ModelName}", modelName);
                return await ResolveModelNameFromDbAsync(modelName, cancel);
            },
            new HybridCacheEntryOptions
            {
                Expiration = LoadBalancerCacheExpiration, // 5 分钟缓存
                LocalCacheExpiration = LocalCacheExpiration // 1 分钟本地缓存
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 从数据库解析模型名称（不使用缓存）
    /// </summary>
    private async Task<ConfigSelectionResult?> ResolveModelNameFromDbAsync(string modelName, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            // 1. 尝试按 App 名称解析（直接返回 App 的 Config 或 Endpoint）
            var app = await dbContext.Apps
                .Include(a => a.LlmPrompt)
                .Include(a => a.LlmConfig)
                .Include(a => a.Endpoint)
                    .ThenInclude(e => e!.EndpointConfigs)
                    .ThenInclude(ec => ec.LlmConfig)
                .FirstOrDefaultAsync(a => a.Name == modelName && a.IsEnabled, cancellationToken);
            
            if (app != null)
            {
                // 🎯 特殊处理 AgentGroup 类型
                if (string.Equals(app.AppType, "AgentGroup", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("找到 AgentGroup: {AppName}（AgentGroup 不需要 Config）", app.Name);
                    return new ConfigSelectionResult
                    {
                        Success = true,
                        App = app,
                        Config = null, // AgentGroup 不使用 Config
                        Strategy = "代理组",
                        NeedsRelease = false,
                        IsAgentGroup = true,
                        Message = $"AgentGroup 应用: {app.Name}"
                    };
                }
                
                // 优先使用 App 直接指定的 Config
                if (!string.IsNullOrEmpty(app.LlmConfigId) && app.LlmConfig != null && app.LlmConfig.IsEnabled)
                {
                    _logger.LogDebug("找到 App: {AppName} -> 直接指定的 Config: {ConfigName}", app.Name, app.LlmConfig.Name);
                    return new ConfigSelectionResult
                    {
                        Success = true,
                        Config = app.LlmConfig,
                        App = app,
                        Strategy = "App 直接指定配置",
                        NeedsRelease = false,
                        Message = $"使用 App '{app.Name}' 直接指定的配置: {app.LlmConfig.Name}"
                    };
                }
                
                // 否则使用 App 的 Endpoint
                if (!string.IsNullOrEmpty(app.EndpointId) && app.Endpoint != null && app.Endpoint.IsEnabled)
                {
                    // 🎯 获取 Endpoint 的所有可用配置
                    var availableConfigsList = app.Endpoint.EndpointConfigs
                        .Where(ec => ec.LlmConfig != null && ec.LlmConfig.IsEnabled)
                        .OrderBy(ec => ec.Priority)
                        .Select(ec => ec.LlmConfig!)
                        .ToList();
                    
                    if (availableConfigsList.Any())
                    {
                        _logger.LogDebug("找到 App: {AppName} -> Endpoint: {EndpointName} -> {Count} 个配置", 
                            app.Name, app.Endpoint.Name, availableConfigsList.Count);
                        return new ConfigSelectionResult
                        {
                            Success = true,
                            Config = availableConfigsList[0], // 默认第一个（最高优先级）
                            AvailableConfigs = availableConfigsList, // 🎯 保存所有配置
                            App = app, // 🎯 重要: 设置 App 引用
                            Endpoint = app.Endpoint, // 🎯 设置 Endpoint 引用
                            Strategy = availableConfigsList.Count == 1 ? "App 的 Endpoint (单一配置)" : $"App 的 Endpoint ({availableConfigsList.Count} 个配置)",
                            NeedsRelease = false,
                            Message = $"App '{app.Name}' 的 Endpoint '{app.Endpoint.Name}' 有 {availableConfigsList.Count} 个可用配置"
                        };
                    }
                }
                
                _logger.LogWarning("App '{AppName}' 没有可用的配置或 Endpoint", app.Name);
                return null;
            }

            // 2. 尝试按 Config 名称解析
            var config = await dbContext.Configs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Name == modelName && c.IsEnabled, cancellationToken);
            
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

            // 3. 🎯 尝试按 Endpoint 名称解析（缓存所有可用配置）
            var endpointByName = await dbContext.Endpoints
                .Include(e => e.EndpointConfigs)
                .ThenInclude(ec => ec.LlmConfig)
                .FirstOrDefaultAsync(e => e.Name == modelName && e.IsEnabled, cancellationToken);
            
            if (endpointByName != null)
            {
                var availableConfigs = endpointByName.EndpointConfigs
                    .Where(ec => ec.LlmConfig != null && ec.LlmConfig.IsEnabled)
                    .OrderBy(ec => ec.Priority)
                    .Select(ec => ec.LlmConfig!)
                    .ToList();
                
                if (availableConfigs.Any())
                {
                    _logger.LogDebug("找到 Endpoint: {EndpointName} -> {Count} 个可用配置", endpointByName.Name, availableConfigs.Count);
                    return new ConfigSelectionResult
                    {
                        Success = true,
                        Config = availableConfigs[0], // 默认第一个（最高优先级）
                        AvailableConfigs = availableConfigs, // 🎯 保存所有配置
                        Strategy = availableConfigs.Count == 1 ? "Endpoint 名称 (单一配置)" : $"Endpoint 名称 ({availableConfigs.Count} 个配置)",
                        NeedsRelease = false,
                        Message = $"使用 Endpoint '{endpointByName.Name}' 的配置 (共 {availableConfigs.Count} 个)"
                    };
                }
            }

            // 4. 🎯 尝试按 Endpoint ID 解析（缓存所有可用配置）
            var endpointById = await dbContext.Endpoints
                .Include(e => e.EndpointConfigs)
                .ThenInclude(ec => ec.LlmConfig)
                .FirstOrDefaultAsync(e => e.Id == modelName && e.IsEnabled, cancellationToken);
            
            if (endpointById != null)
            {
                var availableConfigs = endpointById.EndpointConfigs
                    .Where(ec => ec.LlmConfig != null && ec.LlmConfig.IsEnabled)
                    .OrderBy(ec => ec.Priority)
                    .Select(ec => ec.LlmConfig!)
                    .ToList();
                
                if (availableConfigs.Any())
                {
                    _logger.LogDebug("找到 Endpoint ID: {EndpointId} -> {Count} 个可用配置", endpointById.Id, availableConfigs.Count);
                    return new ConfigSelectionResult
                    {
                        Success = true,
                        Config = availableConfigs[0], // 默认第一个（最高优先级）
                        AvailableConfigs = availableConfigs, // 🎯 保存所有配置
                        Strategy = availableConfigs.Count == 1 ? "Endpoint ID (单一配置)" : $"Endpoint ID ({availableConfigs.Count} 个配置)",
                        NeedsRelease = false,
                        Message = $"使用 Endpoint ID '{endpointById.Id}' 的配置 (共 {availableConfigs.Count} 个)"
                    };
                }
            }

            // 5. 未找到任何匹配
            _logger.LogDebug("未找到模型: {ModelName}", modelName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ 解析模型名称 '{ModelName}' 失败", modelName);
            return null;
        }
    }

    /// <summary>
    /// 刷新单个模型的缓存（配置更新时调用）
    /// </summary>
    public async Task RefreshModelCacheAsync(string modelName, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("🔄 刷新模型缓存: {ModelName}", modelName);
        
        // 先清除旧缓存
        await InvalidateModelCacheAsync(modelName, cancellationToken);
        
        // 重新解析并缓存（ResolveModelNameAsync 会自动缓存结果）
        var result = await ResolveModelNameAsync(modelName, cancellationToken);
        if (result != null && result.Success)
        {
            _logger.LogInformation("✅ 刷新模型缓存成功: {ModelName}", modelName);
        }
        else
        {
            _logger.LogWarning("⚠️ 刷新模型缓存失败,模型可能不存在或已禁用: {ModelName}", modelName);
        }
    }

    /// <summary>
    /// 检查模型名称是否在 App、Config、Endpoint 三者中全局唯一
    /// 🎯 因为 modelName 会用来查询这三种实体,所以必须全局唯一
    /// </summary>
    public async Task<string?> CheckModelNameUniquenessAsync(string name, string entityType, string? excludeId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            
            // 检查 App 名称冲突
            if (entityType != "App")
            {
                var appExists = await dbContext.Apps.AnyAsync(a => a.Name == name, cancellationToken);
                if (appExists)
                {
                    return $"名称 '{name}' 已被一个 App 使用，请使用不同的名称。App、Config、Endpoint 的名称必须全局唯一。";
                }
            }
            else
            {
                var appExists = await dbContext.Apps.AnyAsync(a => a.Name == name && a.Id != excludeId, cancellationToken);
                if (appExists)
                {
                    return $"App 名称 '{name}' 已被其他 App 使用，请使用不同的名称。";
                }
            }
            
            // 检查 Config 名称冲突
            if (entityType != "Config")
            {
                var configExists = await dbContext.Configs.AnyAsync(c => c.Name == name, cancellationToken);
                if (configExists)
                {
                    return $"名称 '{name}' 已被一个 Config 使用，请使用不同的名称。App、Config、Endpoint 的名称必须全局唯一。";
                }
            }
            else
            {
                var configExists = await dbContext.Configs.AnyAsync(c => c.Name == name && c.Id != excludeId, cancellationToken);
                if (configExists)
                {
                    return $"Config 名称 '{name}' 已被其他 Config 使用，请使用不同的名称。";
                }
            }
            
            // 检查 Endpoint 名称冲突
            if (entityType != "Endpoint")
            {
                var endpointExists = await dbContext.Endpoints.AnyAsync(e => e.Name == name, cancellationToken);
                if (endpointExists)
                {
                    return $"名称 '{name}' 已被一个 Endpoint 使用，请使用不同的名称。App、Config、Endpoint 的名称必须全局唯一。";
                }
            }
            else
            {
                var endpointExists = await dbContext.Endpoints.AnyAsync(e => e.Name == name && e.Id != excludeId, cancellationToken);
                if (endpointExists)
                {
                    return $"Endpoint 名称 '{name}' 已被其他 Endpoint 使用，请使用不同的名称。";
                }
            }
            
            return null; // 名称唯一
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ 检查模型名称唯一性失败: {Name}", name);
            return "检查名称唯一性时发生错误";
        }
    }

    /// <summary>
    /// 清除指定模型名称的缓存（LoadBalancer 使用）
    /// </summary>
    public async Task InvalidateModelCacheAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var key = $"{LoadBalancerCacheKeyPrefix}{modelName}";
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _logger.LogDebug("🗑️ 已清除模型缓存: {ModelName}, Key={Key}", modelName, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清除模型缓存失败: {ModelName}", modelName);
        }
    }

    #endregion
}
