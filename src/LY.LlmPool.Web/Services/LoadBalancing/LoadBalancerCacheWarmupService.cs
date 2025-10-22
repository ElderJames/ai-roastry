using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Web.Services.LoadBalancing;

/// <summary>
/// LoadBalancer 缓存预热后台服务
/// 在程序启动时自动预热所有模型名称的缓存映射
///  实际预热逻辑委托给 LlmPoolCacheService
/// </summary>
public class LoadBalancerCacheWarmupService : IHostedService
{
    private readonly LlmPoolCacheService _cacheService;
    private readonly ILogger<LoadBalancerCacheWarmupService> _logger;

    public LoadBalancerCacheWarmupService(
        LlmPoolCacheService cacheService,
        ILogger<LoadBalancerCacheWarmupService> logger)
    {
        _cacheService = cacheService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(" LoadBalancerCacheWarmupService 启动中...");

        try
        {
            // 延迟 2 秒确保数据库连接就绪
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            //  委托给 LlmPoolCacheService 进行预热
            await _cacheService.WarmupCacheAsync(cancellationToken);

            _logger.LogInformation(" LoadBalancerCacheWarmupService 启动完成");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(" LoadBalancerCacheWarmupService 启动被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " LoadBalancerCacheWarmupService 启动失败");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 LoadBalancerCacheWarmupService 停止");
        return Task.CompletedTask;
    }
}
