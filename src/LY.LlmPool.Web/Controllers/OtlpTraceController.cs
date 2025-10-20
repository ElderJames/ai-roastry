using Microsoft.AspNetCore.Mvc;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Controllers;

/// <summary>
/// OTLP Trace 接收端点
/// 接收来自外部进程 (如 TestMcpServer) 的 OpenTelemetry traces
/// 使 LlmPool 作为简易版 OTLP Collector
/// </summary>
[ApiController]
[Route("v1")]
public class OtlpTraceController : ControllerBase
{
    private readonly ILogger<OtlpTraceController> _logger;
    private readonly ActivityTraceService _activityTraceService;
    private readonly OtlpTraceParser _otlpTraceParser;

    public OtlpTraceController(
        ILogger<OtlpTraceController> logger,
        ActivityTraceService activityTraceService,
        OtlpTraceParser otlpTraceParser)
    {
        _logger = logger;
        _activityTraceService = activityTraceService;
        _otlpTraceParser = otlpTraceParser;
    }

    /// <summary>
    /// 接收 OTLP Protobuf 格式的 trace 数据 (HTTP/Protobuf)
    /// 符合 OpenTelemetry Protocol Specification
    /// </summary>
    [HttpPost("traces")]
    [Consumes("application/x-protobuf")]
    public async Task<IActionResult> ReceiveTracesProtobuf()
    {
        try
        {
            // 读取 Protobuf 数据
            using var memoryStream = new MemoryStream();
            await Request.Body.CopyToAsync(memoryStream);
            var protobufData = memoryStream.ToArray();

            if (protobufData.Length == 0)
            {
                _logger.LogWarning("收到空的 OTLP trace 数据");
                return BadRequest("Empty trace data");
            }

            _logger.LogDebug("收到 OTLP trace 数据: {Size} bytes", protobufData.Length);

            // 解析并存储
            var activities = _otlpTraceParser.ParseProtobuf(protobufData);
            
            foreach (var activity in activities)
            {
                _activityTraceService.AddExternalActivity(activity);
            }

            _logger.LogInformation("✅ 成功接收并存储 {Count} 个 Activities", activities.Count);

            // 返回 200 OK (OTLP 标准响应)
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ 解析 OTLP trace 数据失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 接收 JSON 格式的 trace 数据 (备用,用于调试)
    /// </summary>
    [HttpPost("traces/json")]
    [Consumes("application/json")]
    public IActionResult ReceiveTracesJson([FromBody] List<ExternalActivityDto> activities)
    {
        try
        {
            foreach (var activityDto in activities)
            {
                _activityTraceService.AddExternalActivity(activityDto);
            }

            _logger.LogInformation("✅ 成功接收并存储 {Count} 个 Activities (JSON)", activities.Count);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ 处理 JSON trace 数据失败");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 健康检查端点
    /// </summary>
    [HttpGet("traces/health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            service = "LlmPool OTLP Collector",
            endpoint = "/v1/traces",
            supportedFormats = new[] { "application/x-protobuf", "application/json" }
        });
    }
}
