using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using Microsoft.Extensions.Logging;

namespace LY.LlmPool.Client.Telemetry;

/// <summary>
/// 自定义 Activity Exporter - 将 OpenTelemetry 的 Activity 导出到 LlmPool
/// 通过 HTTP POST 发送到 LlmPool 的 /v1/traces/json 端点
/// </summary>
public class LlmPoolActivityExporter : BaseExporter<Activity>
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly ILogger<LlmPoolActivityExporter> _logger;

    public LlmPoolActivityExporter(
        HttpClient httpClient,
        string endpoint,
        ILogger<LlmPoolActivityExporter> logger)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _logger = logger;
    }

    /// <summary>
    /// 导出 Activity Batch 到 LlmPool
    /// </summary>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        try
        {
            var activities = new List<object>();
            
            foreach (var activity in batch)
            {
                // 序列化 Activity 为符合 ExternalActivityDto 的 JSON 对象
                var activityJson = new
                {
                    traceId = activity.TraceId.ToString(),
                    spanId = activity.SpanId.ToString(),
                    parentSpanId = activity.ParentSpanId.ToString(),
                    operationName = activity.OperationName,
                    startTimeUtc = activity.StartTimeUtc,
                    duration = activity.Duration,
                    tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value as object),
                    source = "MCP Server", // 默认来源
                    status = (int)activity.Status,
                    statusDescription = activity.StatusDescription,
                    // 添加错误相关标签
                    events = activity.Events.Select(e => new
                    {
                        name = e.Name,
                        timestamp = e.Timestamp,
                        tags = e.Tags.ToDictionary(t => t.Key, t => t.Value as object)
                    }).ToList()
                };
                
                activities.Add(activityJson);
                
                _logger.LogDebug("Exporting Activity: {OperationName} (TraceId: {TraceId}, SpanId: {SpanId})",
                    activity.OperationName, activity.TraceId, activity.SpanId);
            }

            // 发送到 LlmPool
            if (activities.Count > 0)
            {
                var json = JsonSerializer.Serialize(activities, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = _httpClient.PostAsync(_endpoint, content).GetAwaiter().GetResult();
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("✅ Successfully exported {Count} activities to LlmPool", activities.Count);
                    return ExportResult.Success;
                }
                else
                {
                    var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    _logger.LogWarning("⚠️ Failed to export activities. Status: {StatusCode}, Body: {Body}", 
                        response.StatusCode, responseBody);
                    return ExportResult.Failure;
                }
            }

            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to export activities to LlmPool");
            return ExportResult.Failure;
        }
    }
}
