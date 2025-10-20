using System.Diagnostics;

namespace LY.LlmPool.Web.Services.Telemetry;

/// <summary>
/// 外部 Activity DTO (用于 OTLP 接收)
/// </summary>
public class ExternalActivityDto
{
    public required string TraceId { get; set; }
    public required string SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public required string OperationName { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public TimeSpan Duration { get; set; }
    public Dictionary<string, object?>? Tags { get; set; }
    public string? Source { get; set; }
    public ActivityStatusCode Status { get; set; }
    public string? StatusDescription { get; set; }
}
