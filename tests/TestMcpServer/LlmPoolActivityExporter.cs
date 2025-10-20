using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using Microsoft.Extensions.Logging;

namespace TestMcpServer;

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
                // 🔍 记录所有 Activity（包括 HttpClient 的）
                Console.WriteLine($"🔄 [Exporter] Exporting Activity: {activity.OperationName} | DisplayName: {activity.DisplayName} | SpanId: {activity.SpanId} | ParentSpanId: {activity.ParentSpanId}");
                
                // 🔑 序列化 Activity 为符合 ExternalActivityDto 的 JSON 对象
                var activityJson = new
                {
                    traceId = activity.TraceId.ToString(),
                    spanId = activity.SpanId.ToString(),
                    parentSpanId = activity.ParentSpanId.ToString(),
                    operationName = activity.OperationName,
                    startTimeUtc = activity.StartTimeUtc,  // DateTime 类型
                    duration = activity.Duration,  // TimeSpan 类型（会序列化为 "00:00:00.123" 格式）
                    tags = activity.Tags.ToDictionary(t => t.Key, t => t.Value as object),
                    source = "TestMcpServer",
                    status = (int)activity.Status,  // ActivityStatusCode enum as int
                    statusDescription = activity.StatusDescription
                };
                
                activities.Add(activityJson);
                
                _logger.LogDebug("Exporting Activity: {OperationName} (TraceId: {TraceId}, SpanId: {SpanId})",
                    activity.OperationName, activity.TraceId, activity.SpanId);
            }

            // 发送到 LlmPool
            if (activities.Count > 0)
            {
                // 🔑 直接发送 Activity 数组，不包装（Controller 期望 List<ExternalActivityDto>）
                var json = JsonSerializer.Serialize(activities, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                Console.WriteLine($"📤 [Exporter] Sending {activities.Count} activities to LlmPool");
                Console.WriteLine($"📍 [Exporter] Endpoint: {_endpoint}");
                Console.WriteLine($"📝 [Exporter] Payload preview: {json.Substring(0, Math.Min(200, json.Length))}...");
                
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                try
                {
                    var response = _httpClient.PostAsync(_endpoint, content).GetAwaiter().GetResult();
                    
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("✅ Successfully exported {Count} activities to LlmPool", activities.Count);
                        Console.WriteLine($"✅ [Exporter] HTTP {response.StatusCode} - Success");
                        return ExportResult.Success;
                    }
                    else
                    {
                        var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        _logger.LogWarning("⚠️ Failed to export activities. Status: {StatusCode}, Body: {Body}", 
                            response.StatusCode, responseBody);
                        Console.WriteLine($"❌ [Exporter] HTTP {response.StatusCode} - Failed");
                        Console.WriteLine($"Response: {responseBody}");
                        return ExportResult.Failure;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [Exporter] Exception: {ex.Message}");
                    throw;
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
