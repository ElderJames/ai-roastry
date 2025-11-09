using System.Collections.Concurrent;
using LY.LlmPool.Web.Data;
using LY.LlmPool.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Services.LoadBalancing;

/// <summary>
/// 负载均衡服务
/// 负责为 Endpoint 选择最优的 LlmConfig
/// 支持进程内部调用和 HTTP API 调用
/// 🎯 统一使用 LlmPoolCacheService 进行缓存管理
/// </summary>
public class LoadBalancerService
{
    private readonly ILogger<LoadBalancerService> _logger;
    private readonly IServiceProvider _serviceProvider; // 🎯 用于延迟获取 LlmPoolCacheService
    
    // 🎯 跟踪每个配置当前的请求数（用于负载均衡）
    private readonly ConcurrentDictionary<string, int> _configRequestCounts = new();

    public LoadBalancerService(
        ILogger<LoadBalancerService> logger,
        IServiceProvider serviceProvider) // 🎯 注入 IServiceProvider
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 解析模型名称并返回配置选择结果
    /// 🎯 统一使用 LlmPoolCacheService 进行缓存管理
    /// </summary>
    /// <param name="modelName">模型名称（可以是 App 名称、Config 名称、Endpoint 名称或 ID）</param>
    /// <returns>配置选择结果</returns>
    public async Task<ConfigSelectionResult?> SelectConfigAsync(string modelName)
    {
        _logger.LogDebug("从 LlmPoolCacheService 解析模型名称: {ModelName}", modelName);

        // 🎯 统一使用 LlmPoolCacheService 进行缓存管理（避免两个 _cache 不同步）
        var cacheService = _serviceProvider.GetService<LlmPoolCacheService>();
        if (cacheService == null)
        {
            _logger.LogError("❌ LlmPoolCacheService 未就绪，无法解析模型名称: {ModelName}", modelName);
            return null;
        }

        // 🎯 调用 LlmPoolCacheService 的统一方法（内部会先查缓存，再查数据库）
        var result = await cacheService.ResolveModelNameAsync(modelName);
        
        if (result == null || !result.Success)
        {
            _logger.LogError("❌ 模型 '{ModelName}' 解析失败", modelName);
            return null;
        }

        // 🎯 AgentGroup 直接返回，不进行配置选择
        if (result.IsAgentGroup)
        {
            _logger.LogInformation("✅ 模型 '{ModelName}' 是 AgentGroup: {AppName}",
                modelName, result.App?.Name);
            return result;
        }
        
        // 🎯 如果缓存的结果包含多个可用配置,根据并发数选择最优配置
        if (result.AvailableConfigs != null && result.AvailableConfigs.Count > 1)
        {
            _logger.LogInformation("🎯 Endpoint 有 {Count} 个可用配置，根据并发数选择最优配置...", 
                result.AvailableConfigs.Count);
            
            // 🎯 选择当前并发数最少的配置（优先）,然后按 Priority 排序
            var selectedConfig = result.AvailableConfigs
                .Select(c => new
                {
                    Config = c,
                    RequestCount = _configRequestCounts.GetOrAdd(c.Id!, 0)
                })
                .OrderBy(x => x.RequestCount)
                .ThenBy(x => result.AvailableConfigs.IndexOf(x.Config)) // 保持 Priority 顺序
                .First()
                .Config;
            
            _logger.LogInformation("✅ 从 {Count} 个配置中选择: {ConfigName} (当前并发: {Concurrent})",
                result.AvailableConfigs.Count, 
                selectedConfig.Name, 
                _configRequestCounts.GetOrAdd(selectedConfig.Id!, 0));
            
            // 🎯 更新选择结果
            result.Config = selectedConfig;
            result.Strategy = $"{result.Strategy} (并发优化选择)";
            result.NeedsRelease = true; // 🎯 多配置选择需要释放
            
            // 🎯 立即增加请求计数,确保下一次请求能看到最新状态
            IncrementRequestCount(selectedConfig.Id!);
            _logger.LogDebug("📈 配置 {ConfigName} 被选中,立即更新请求计数: {Count}",
                selectedConfig.Name, _configRequestCounts.GetOrAdd(selectedConfig.Id!, 0));
        }
        
        _logger.LogInformation("✅ 模型 '{ModelName}' 解析成功: Strategy={Strategy}, Config={ConfigName}",
            modelName, result.Strategy, result.Config?.Name);
        return result;
    }

    /// <summary>
    /// 为 App 选择配置
    /// 🎯 仅由 LoadBalancerCacheWarmupService 调用，传入的 App 对象已包含所有必要的导航属性
    /// </summary>
    internal async Task<ConfigSelectionResult> SelectConfigForAppAsync(LlmApp app)
    {
        _logger.LogInformation("找到 App: {AppName}, 类型: {AppType}", app.Name, app.AppType);

        // AgentGroup 类型特殊处理（不在这里处理）
        if (string.Equals(app.AppType, "AgentGroup", StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigSelectionResult
            {
                Success = true,
                App = app,
                Strategy = "代理组",
                NeedsRelease = false,
                IsAgentGroup = true,
                Message = $"AgentGroup 应用: {app.Name}"
            };
        }

        // App 直接指定 LlmConfig（不进行负载均衡）
        if (app.LlmConfig != null && app.LlmConfig.IsEnabled)
        {
            _logger.LogInformation("App {AppName} 直接使用 LlmConfig {ConfigName}（无需负载均衡）", 
                app.Name, app.LlmConfig.Name);
            
            return new ConfigSelectionResult
            {
                Success = true,
                Config = app.LlmConfig,
                App = app,
                Strategy = "应用直接配置",
                NeedsRelease = false,
                Message = $"App {app.Name} 直接使用配置 {app.LlmConfig.Name}"
            };
        }

        // App 指定 Endpoint（需要负载均衡）
        if (app.Endpoint != null)
        {
            var result = await SelectConfigForEndpointAsync(app.Endpoint);
            result.App = app;
            result.Message = $"App {app.Name} 通过 Endpoint {app.Endpoint.Name} 负载均衡";
            return result;
        }

        // App 没有有效配置
        return new ConfigSelectionResult
        {
            Success = false,
            App = app,
            Strategy = "应用无配置",
            NeedsRelease = false,
            Message = $"App {app.Name} 没有有效的模型配置"
        };
    }

    /// <summary>
    /// 为 Endpoint 选择配置（负载均衡）
    /// </summary>
    internal async Task<ConfigSelectionResult> SelectConfigForEndpointAsync(LlmEndpoint endpoint)
    {
        _logger.LogInformation("为 Endpoint {EndpointName} 选择配置", endpoint.Name);

        var configs = endpoint.EndpointConfigs
            .Where(ec => ec.LlmConfig != null && ec.LlmConfig.IsEnabled)
            .OrderBy(ec => ec.Priority)
            .Select(ec => ec.LlmConfig!)
            .ToList();

        if (configs.Count == 0)
        {
            return new ConfigSelectionResult
            {
                Success = false,
                Endpoint = endpoint,
                Strategy = "端点无配置",
                NeedsRelease = false,
                Message = $"Endpoint {endpoint.Name} 没有可用的配置"
            };
        }

        // 🎯 只有一个配置，直接使用（无需负载均衡）
        if (configs.Count == 1)
        {
            var singleConfig = configs[0];
            _logger.LogInformation("Endpoint {EndpointName} 只有一个配置 {ConfigName}（无需负载均衡）", 
                endpoint.Name, singleConfig.Name);
            
            return new ConfigSelectionResult
            {
                Success = true,
                Config = singleConfig,
                Endpoint = endpoint,
                Strategy = "端点单配置",
                NeedsRelease = false,
                Message = $"Endpoint {endpoint.Name} 只有一个配置 {singleConfig.Name}"
            };
        }

        // 🎯 多个配置，进行负载均衡
        _logger.LogInformation("Endpoint {EndpointName} 有 {Count} 个配置，开始负载均衡", 
            endpoint.Name, configs.Count);

        var selectedConfig = await SelectBestConfigAsync(configs, endpoint.Name);
        
        if (selectedConfig != null)
        {
            return new ConfigSelectionResult
            {
                Success = true,
                Config = selectedConfig,
                Endpoint = endpoint,
                Strategy = "端点负载均衡",
                NeedsRelease = true,
                Message = $"Endpoint {endpoint.Name} 负载均衡选择 {selectedConfig.Name}"
            };
        }

        return new ConfigSelectionResult
        {
            Success = false,
            Endpoint = endpoint,
            Strategy = "负载均衡失败",
            NeedsRelease = false,
            Message = $"Endpoint {endpoint.Name} 所有配置都不可用"
        };
    }

    /// <summary>
    /// 选择最优配置（负载均衡核心逻辑）
    /// 🎯 简化版本：直接选择请求数最少的配置，避免使用 Semaphore 造成死锁
    /// </summary>
    private Task<LlmConfig?> SelectBestConfigAsync(List<LlmConfig> configs, string endpointName)
    {
        // 选择当前请求数最少的配置
        var configWithMinRequests = configs
            .Select(c => new
            {
                Config = c,
                RequestCount = _configRequestCounts.GetOrAdd(c.Id!, 0)
            })
            .OrderBy(x => x.RequestCount)
            .ThenBy(x => configs.IndexOf(x.Config)) // 保持 Priority 顺序
            .First();

        _logger.LogInformation("✅ 选择配置 {ConfigName}（当前请求数: {RequestCount}），Endpoint: {EndpointName}", 
            configWithMinRequests.Config.Name, configWithMinRequests.RequestCount, endpointName);

        // 增加请求计数
        IncrementRequestCount(configWithMinRequests.Config.Id!);
        
        return Task.FromResult<LlmConfig?>(configWithMinRequests.Config);
    }

    /// <summary>
    /// 释放配置（减少请求计数）
    /// </summary>
    public void ReleaseConfig(string configId)
    {
        // 减少请求计数
        DecrementRequestCount(configId);
    }

    /// <summary>
    /// 增加请求计数
    /// </summary>
    private void IncrementRequestCount(string configId)
    {
        _configRequestCounts.AddOrUpdate(configId, 1, (_, count) => count + 1);
        var newCount = _configRequestCounts[configId];
        _logger.LogDebug("📈 配置 {ConfigId} 请求数 +1 = {Count}", configId, newCount);
    }

    /// <summary>
    /// 减少请求计数
    /// </summary>
    private void DecrementRequestCount(string configId)
    {
        _configRequestCounts.AddOrUpdate(configId, 0, (_, count) => Math.Max(0, count - 1));
        var newCount = _configRequestCounts[configId];
        _logger.LogDebug("📉 配置 {ConfigId} 请求数 -1 = {Count}", configId, newCount);
    }

    /// <summary>
    /// 获取配置当前的请求数（用于监控）
    /// </summary>
    public int GetConfigRequestCount(string configId)
    {
        return _configRequestCounts.GetOrAdd(configId, 0);
    }

    /// <summary>
    /// 获取所有配置的请求数统计（用于监控）
    /// </summary>
    public Dictionary<string, int> GetAllConfigRequestCounts()
    {
        return _configRequestCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}

/// <summary>
/// 配置选择结果
/// </summary>
public class ConfigSelectionResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 选中的配置ID（用于缓存序列化）
    /// </summary>
    public string? ConfigId { get; set; }

    /// <summary>
    /// 选中的配置（运行时加载）
    /// </summary>
    public LlmConfig? Config { get; set; }

    /// <summary>
    /// 相关的 App ID（用于缓存序列化）
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// 相关的 App（运行时加载）
    /// </summary>
    public LlmApp? App { get; set; }

    /// <summary>
    /// 相关的 Endpoint ID（用于缓存序列化）
    /// </summary>
    public string? EndpointId { get; set; }

    /// <summary>
    /// 相关的 Endpoint（运行时加载）
    /// </summary>
    public LlmEndpoint? Endpoint { get; set; }

    /// <summary>
    /// 选择策略
    /// </summary>
    public string Strategy { get; set; } = string.Empty;

    /// <summary>
    /// 是否需要释放（负载均衡的配置需要释放）
    /// </summary>
    public bool NeedsRelease { get; set; }

    /// <summary>
    /// 是否是 AgentGroup 应用
    /// </summary>
    public bool IsAgentGroup { get; set; }

    /// <summary>
    /// 所有可用配置ID列表（用于缓存序列化）
    /// </summary>
    public List<string>? AvailableConfigIds { get; set; }

    /// <summary>
    /// 所有可用配置列表（用于 Endpoint 多配置场景）
    /// 🎯 缓存时保存所有配置,使用时再进行负载均衡选择
    /// </summary>
    public List<LlmConfig>? AvailableConfigs { get; set; }

    /// <summary>
    /// 消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 从数据库加载完整的对象（用于缓存反序列化后）
    /// </summary>
    public async Task LoadFullObjectsAsync(LlmDbContext dbContext, CancellationToken cancellationToken = default)
    {
        // 加载 App（包含导航属性）
        if (!string.IsNullOrEmpty(AppId))
        {
            App = await dbContext.Apps
                .Include(a => a.LlmPrompt)
                .Include(a => a.LlmConfig)
                .Include(a => a.Endpoint)
                    .ThenInclude(e => e!.EndpointConfigs)
                    .ThenInclude(ec => ec.LlmConfig)
                .FirstOrDefaultAsync(a => a.Id == AppId && a.IsEnabled, cancellationToken);
        }

        // 加载 Config
        if (!string.IsNullOrEmpty(ConfigId))
        {
            Config = await dbContext.Configs
                .FirstOrDefaultAsync(c => c.Id == ConfigId && c.IsEnabled, cancellationToken);
        }

        // 加载 Endpoint
        if (!string.IsNullOrEmpty(EndpointId))
        {
            Endpoint = await dbContext.Endpoints
                .Include(e => e.EndpointConfigs)
                    .ThenInclude(ec => ec.LlmConfig)
                .FirstOrDefaultAsync(e => e.Id == EndpointId && e.IsEnabled, cancellationToken);
        }

        // 加载 AvailableConfigs
        if (AvailableConfigIds != null && AvailableConfigIds.Any())
        {
            var configsFromDb = await dbContext.Configs
                .Where(c => c.Id != null && AvailableConfigIds.Contains(c.Id) && c.IsEnabled)
                .ToListAsync(cancellationToken);

            AvailableConfigs = configsFromDb
                .OrderBy(c => AvailableConfigIds.IndexOf(c.Id!))
                .ToList();
        }
    }
}
