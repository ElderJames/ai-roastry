using LY.LlmPool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LY.LlmPool.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CallRecordsController : ControllerBase
{
    private readonly LlmPoolService _llmPoolService;
    private readonly ILogger<CallRecordsController> _logger;

    public CallRecordsController(
        LlmPoolService llmPoolService,
        ILogger<CallRecordsController> logger)
    {
        _llmPoolService = llmPoolService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetCallRecords(
        [FromQuery] string? endpointId = null,
        [FromQuery] string? configId = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        try
        {
            var skip = (page - 1) * pageSize;
            var records = await _llmPoolService.GetCallRecordsAsync(
                endpointId, 
                configId, 
                startDate, 
                endDate, 
                skip, 
                pageSize);

            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting call records");
            return StatusCode(500, new { error = "Error retrieving call records" });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetCallRecord(string id)
    {
        try
        {
            var record = await _llmPoolService.GetCallRecordAsync(id);
            if (record == null)
            {
                return NotFound(new { error = $"Call record with ID {id} not found" });
            }

            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting call record");
            return StatusCode(500, new { error = "Error retrieving call record" });
        }
    }

    [HttpGet("{id}/chain")]
    public async Task<IActionResult> GetCallChain(string id)
    {
        try
        {
            var chain = await _llmPoolService.GetCallChainAsync(id);
            return Ok(chain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting call chain");
            return StatusCode(500, new { error = "Error retrieving call chain" });
        }
    }

    [HttpGet("model/{configId}")]
    public async Task<IActionResult> GetModelCallRecords(
        string configId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        try
        {
            var skip = (page - 1) * pageSize;
            var records = await _llmPoolService.GetCallRecordsAsync(
                null, 
                configId, 
                startDate, 
                endDate, 
                skip, 
                pageSize);

            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting model call records");
            return StatusCode(500, new { error = "Error retrieving model call records" });
        }
    }
} 