using Microsoft.AspNetCore.Mvc;
using LY.LlmPool.Web.Services.Telemetry;

namespace LY.LlmPool.Web.Controllers;

/// <summary>
/// Activity 追踪 API 控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ActivityTraceController : ControllerBase
{
    private readonly ActivityTraceService _traceService;
    private readonly ILogger<ActivityTraceController> _logger;

    public ActivityTraceController(
        ActivityTraceService traceService,
        ILogger<ActivityTraceController> logger)
    {
        _traceService = traceService;
        _logger = logger;
    }

    /// <summary>
    /// 获取活跃的追踪
    /// </summary>
    [HttpGet("active")]
    public IActionResult GetActiveTraces()
    {
        var traces = _traceService.GetActiveTraces();
        return Ok(traces);
    }

    /// <summary>
    /// 获取已完成的追踪
    /// </summary>
    [HttpGet("completed")]
    public IActionResult GetCompletedTraces([FromQuery] int count = 50)
    {
        var traces = _traceService.GetCompletedTraces(count);
        return Ok(traces);
    }

    /// <summary>
    /// 获取追踪树
    /// </summary>
    [HttpGet("tree/{traceId}")]
    public IActionResult GetTraceTree(string traceId)
    {
        var tree = _traceService.GetTraceTree(traceId);
        return Ok(tree);
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    [HttpGet("statistics")]
    public IActionResult GetStatistics([FromQuery] DateTime? since = null)
    {
        var stats = _traceService.GetStatistics(since);
        return Ok(stats);
    }

    /// <summary>
    /// 清除历史追踪
    /// </summary>
    [HttpDelete("clear")]
    public IActionResult ClearHistory()
    {
        _traceService.ClearHistory();
        return Ok(new { message = "History cleared successfully" });
    }
}
