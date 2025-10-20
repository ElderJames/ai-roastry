using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// 自定义 Activity Exporter - 将 OpenTelemetry 的 Activity 导出到 ActivityTraceService
/// 使 LlmPool 成为简易版 OTLP Collector,无需依赖外部 Jaeger/Tempo
/// </summary>
public class InMemoryActivityExporter : BaseExporter<Activity>
{
    private readonly ActivityTraceService _traceService;
    private readonly ILogger<InMemoryActivityExporter> _logger;

    public InMemoryActivityExporter(
        ActivityTraceService traceService,
        ILogger<InMemoryActivityExporter> logger)
    {
        _traceService = traceService;
        _logger = logger;
    }

    /// <summary>
    /// 导出 Activity Batch 到内存存储
    /// </summary>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        try
        {
            foreach (var activity in batch)
            {
                // ActivityTraceService 已经通过 ActivityListener 自动捕获了所有 Activity
                // 这里可以记录导出日志,或做额外的处理
                _logger.LogDebug("Exported Activity: {OperationName} (TraceId: {TraceId}, SpanId: {SpanId})",
                    activity.OperationName, activity.TraceId, activity.SpanId);
            }

            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export activities");
            return ExportResult.Failure;
        }
    }
}
