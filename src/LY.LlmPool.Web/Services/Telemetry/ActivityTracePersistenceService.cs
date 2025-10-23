using LY.LlmPool.Web.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// Activity 追踪持久化后台服务
/// 负责将内存中的 TraceNode 异步批量保存到数据库
/// </summary>
public class ActivityTracePersistenceService : BackgroundService
{
    private readonly Channel<TraceNode> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActivityTracePersistenceService> _logger;
    private readonly ActivityTracingOptions _options;

    public ActivityTracePersistenceService(
        IServiceProvider serviceProvider,
        ILogger<ActivityTracePersistenceService> logger,
        IOptions<ActivityTracingOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;

        // 创建有界 Channel（防止内存溢出）
        var channelOptions = new BoundedChannelOptions(_options.Persistence.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 满时丢弃最旧的
            SingleReader = true, // 单一读取者（本服务）
            SingleWriter = false // 多个写入者（ActivityTraceService）
        };

        _channel = Channel.CreateBounded<TraceNode>(channelOptions);
        _logger.LogInformation("🚀 ActivityTracePersistenceService 初始化完成，Channel 容量: {Capacity}",
            _options.Persistence.ChannelCapacity);
    }

    /// <summary>
    /// 将 TraceNode 入队等待持久化
    /// </summary>
    public async ValueTask EnqueueAsync(TraceNode node, CancellationToken cancellationToken = default)
    {
        try
        {
            await _channel.Writer.WriteAsync(node, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("⚠️ Channel 已关闭，无法入队 TraceNode: {ActivityId}", node.ActivityId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ TraceNode 入队失败: {ActivityId}", node.ActivityId);
        }
    }

    /// <summary>
    /// 后台服务主循环
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 ActivityTracePersistenceService 后台服务已启动");

        try
        {
            await foreach (var batch in ReadBatchesAsync(stoppingToken))
            {
                await SaveBatchWithRetryAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("⏹️ ActivityTracePersistenceService 后台服务已停止");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ ActivityTracePersistenceService 后台服务发生异常");
        }
    }

    /// <summary>
    /// 读取批次数据（批量 + 超时机制）
    /// </summary>
    private async IAsyncEnumerable<List<TraceNode>> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new List<TraceNode>(_options.Persistence.BatchSize);

        while (!cancellationToken.IsCancellationRequested)
        {
            List<TraceNode>? batchToReturn = null;
            bool shouldBreak = false;

            try
            {
                // 使用 Task.Delay 替代 PeriodicTimer（更灵活）
                var timeoutTask = Task.Delay(_options.Persistence.BatchDelayMilliseconds, cancellationToken);
                var readTask = ReadUpToBatchSizeAsync(batch, cancellationToken);

                // 等待读取完成或超时
                await Task.WhenAny(readTask, timeoutTask);

                // 如果有数据，准备返回批次
                if (batch.Count > 0)
                {
                    batchToReturn = new List<TraceNode>(batch);
                    batch.Clear();
                }
                else
                {
                    // 没有数据，等待一段时间再继续
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 取消时，准备返回剩余数据
                if (batch.Count > 0)
                {
                    batchToReturn = new List<TraceNode>(batch);
                    batch.Clear();
                }
                shouldBreak = true;
            }

            // 在 try-catch 外部执行 yield return
            if (batchToReturn != null)
            {
                yield return batchToReturn;
            }

            if (shouldBreak)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 读取最多 BatchSize 条数据
    /// </summary>
    private async Task ReadUpToBatchSizeAsync(List<TraceNode> batch, CancellationToken cancellationToken)
    {
        while (batch.Count < _options.Persistence.BatchSize)
        {
            if (_channel.Reader.TryRead(out var node))
            {
                batch.Add(node);
            }
            else
            {
                // Channel 暂时为空，尝试异步等待
                if (await _channel.Reader.WaitToReadAsync(cancellationToken))
                {
                    if (_channel.Reader.TryRead(out node))
                    {
                        batch.Add(node);
                    }
                }
                else
                {
                    // Channel 已关闭
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 带重试的批量保存
    /// </summary>
    private async Task SaveBatchWithRetryAsync(List<TraceNode> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        var attempt = 0;
        var maxRetries = _options.Persistence.MaxRetryAttempts;

        while (attempt <= maxRetries)
        {
            try
            {
                // 使用 Scoped 服务（每次保存创建新的 DbContext）
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IActivityTraceRepository>();

                await repository.SaveBatchAsync(batch, cancellationToken);

                _logger.LogInformation("✅ 批量保存成功：{Count} 条记录（尝试 {Attempt}/{MaxRetries}）",
                    batch.Count, attempt + 1, maxRetries + 1);

                return; // 成功，退出
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > maxRetries)
                {
                    _logger.LogError(ex, "❌ 批量保存失败，已达最大重试次数 ({MaxRetries})，批次大小: {Count}",
                        maxRetries, batch.Count);

                    // 记录失败的 ActivityId 用于调试
                    var failedIds = string.Join(", ", batch.Take(5).Select(n => n.ActivityId));
                    _logger.LogWarning("❌ 失败的批次前5条记录: {FailedIds}", failedIds);

                    return; // 放弃该批次
                }

                _logger.LogWarning(ex, "⚠️ 批量保存失败，将重试 ({Attempt}/{MaxRetries})，批次大小: {Count}",
                    attempt, maxRetries, batch.Count);

                // 延迟后重试（指数退避）
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 停止服务时的清理
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("⏹️ ActivityTracePersistenceService 正在停止...");

        // 标记 Channel 为完成（不再接受新数据）
        try
        {
            _channel.Writer.Complete();
        }
        catch (ChannelClosedException)
        {
            // Channel 已关闭，忽略重复 Complete 调用
            _logger.LogDebug("Channel 已关闭，忽略重复 Complete 调用。");
        }
        catch (InvalidOperationException)
        {
            // Channel 已标记完成，忽略重复 Complete 调用
            _logger.LogDebug("Channel 已标记完成，忽略重复 Complete 调用。");
        }
        catch (Exception ex)
        {
            // 其他异常不影响停止流程
            _logger.LogWarning(ex, "标记 Channel 完成时发生异常，继续停止。");
        }
       
        await base.StopAsync(cancellationToken);

        _logger.LogInformation("✅ ActivityTracePersistenceService 已停止");
    }
}
